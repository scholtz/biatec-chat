using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Amazon.Runtime.EventStreams;
using Biatec.Chat.Api.Config;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Model;

/// <summary>Opens a Bedrock ConverseStream and yields its raw events. The seam the unit tests fake.</summary>
public interface IConverseStreamSource
{
    IAsyncEnumerable<IEventStreamEvent> OpenAsync(ConverseStreamRequest request, CancellationToken ct);
}

public sealed class BedrockConverseStreamSource(IAmazonBedrockRuntime bedrock) : IConverseStreamSource
{
    public async IAsyncEnumerable<IEventStreamEvent> OpenAsync(ConverseStreamRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await bedrock.ConverseStreamAsync(request, ct);
        // Disposing the response aborts a read that is already waiting on the network, so cancellation (the
        // caller leaving, or the wait for usage running out) doesn't depend on the read honouring the token.
        // The callback may run on a timer or the caller's thread, so it must never throw.
        await using var abort = ct.Register(() =>
        {
            try { response.Dispose(); }
            catch (Exception) { }
        });
        await foreach (var ev in response.Stream.WithCancellation(ct))
            yield return ev;
    }
}

/// <summary>
/// ConverseStream client: picks the model id for the profile, always sets maxTokens, places a cache point after
/// each static system block, caps concurrent Bedrock calls process-wide and retries transient failures with equal
/// jitter. A background pump drains the Bedrock stream into a channel while it holds a slot, so the slot's lifetime
/// never depends on how fast the caller reads, and a caller that stops reading frees it at once. A call is retried
/// only while nothing has been handed out, so a caller never sees a partial answer twice. Events keep Bedrock's
/// order: text and tool-use deltas, the message stop, then usage (which the client also logs). A ModelToolUse is
/// only a proposal until a ModelMessageStop with stop reason tool_use arrives: run tools after that stop, never on
/// the tool-use event, and not at all after another stop reason (its message then carries no tool calls).
/// </summary>
public sealed class BedrockModelClient(
    IConverseStreamSource source,
    IOptions<BedrockOptions> options,
    IOptions<BedrockClientOptions> clientOptions,
    ILogger<BedrockModelClient> logger) : IModelClient
{
    /// <summary>How long the pump waits for the usage event once the message has stopped.</summary>
    public static readonly TimeSpan UsageWait = TimeSpan.FromSeconds(10);

    private readonly BedrockOptions _o = options.Value;
    private readonly BedrockClientOptions _c = clientOptions.Value;
    private readonly SemaphoreSlim _gate = new(clientOptions.Value.MaxConcurrency, clientOptions.Value.MaxConcurrency);

    public ConverseStreamRequest BuildRequest(ModelRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Persona))
            throw new ArgumentException("The persona system block is required.", nameof(request));

        var system = new List<SystemContentBlock> { new() { Text = request.Persona }, CachePoint() };
        if (!string.IsNullOrWhiteSpace(request.Catalogue))
        {
            system.Add(new SystemContentBlock { Text = request.Catalogue });
            system.Add(CachePoint());
        }

        var maxTokens = Math.Min(request.MaxTokens ?? _o.MaxTokens, _o.MaxTokens);
        if (maxTokens < 1)
            throw new ArgumentOutOfRangeException(nameof(request), "MaxTokens must be at least 1.");
        // Bedrock requires a tool config whenever the history holds tool blocks, and rejects an empty tool list.
        if (request.Tools.Count == 0 && request.Messages.Any(m => m.Content?.Any(c => c.ToolUse is not null || c.ToolResult is not null) == true))
            throw new ArgumentException("A conversation with tool-use or tool-result blocks must be sent with its tools.", nameof(request));

        return new ConverseStreamRequest
        {
            ModelId = request.Profile == ModelProfile.Authenticated ? _o.AuthenticatedModelId : _o.AnonymousModelId,
            System = system,
            // With tools a follow-up call (the tool result) is likely to reuse the history; without, it would be a paid write nobody reads.
            Messages = request.Tools.Count > 0 ? WithHistoryCachePoint(request.Messages) : [.. request.Messages],
            InferenceConfig = new InferenceConfiguration { MaxTokens = maxTokens },
            ToolConfig = request.Tools.Count == 0
                ? null
                : new ToolConfiguration { Tools = [.. request.Tools.Select(t => new Tool { ToolSpec = t })] },
        };
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var converse = BuildRequest(request);
        var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Unbounded is fine: one answer is at most maxTokens of output.
        var channel = Channel.CreateUnbounded<ModelStreamEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var pump = PumpAsync(converse, channel.Writer, pumpCts.Token);
        var stopSeen = false;
        try
        {
            // WaitToReadAsync rethrows the error the pump completed the channel with.
            while (await channel.Reader.WaitToReadAsync(ct))
            {
                while (channel.Reader.TryRead(out var output))
                {
                    ct.ThrowIfCancellationRequested();
                    stopSeen |= output is ModelMessageStop;
                    yield return output;
                }
            }
        }
        finally
        {
            if (stopSeen)
            {
                // A reader that stops at the message stop (to run tools) doesn't wait for usage, but the pump still
                // reads and logs it, for at most UsageWait, and then frees the slot.
                _ = pump.ContinueWith(_ => pumpCts.Dispose(), TaskScheduler.Default);
            }
            else
            {
                // Cancel runs the response-abort callback inline; whatever it throws must not skip awaiting the pump.
                try { pumpCts.Cancel(); }
                catch (AggregateException ex) { logger.LogDebug(ex, "Aborting the Bedrock stream threw"); }
                await pump;
                pumpCts.Dispose();
            }
        }
    }

    private async Task PumpAsync(ConverseStreamRequest converse, ChannelWriter<ModelStreamEvent> writer, CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
            await RunAttemptsAsync(converse, writer, ct);
        }
        catch (Exception ex) when (Unwrap(ex) is ThrottlingException or ServiceUnavailableException)
        {
            // Retries ran out on an overloaded service: the same "busy, try again" as a full queue.
            failure = new ModelBusyException("Bedrock stayed throttled or unavailable through every retry.", ex);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        writer.TryComplete(failure);
    }

    private async Task RunAttemptsAsync(ConverseStreamRequest converse, ChannelWriter<ModelStreamEvent> writer, CancellationToken ct)
    {
        // One budget for time spent waiting on a slot, shared by all attempts (backoff doesn't count).
        var budget = TimeSpan.FromMilliseconds(_c.QueueTimeoutMs);
        var waited = TimeSpan.Zero;
        Exception? lastError = null;
        var stalls = 0;
        for (var attempt = 0; ; attempt++)
        {
            var queued = Stopwatch.StartNew();
            var acquired = await _gate.WaitAsync(budget > waited ? budget - waited : TimeSpan.Zero, ct);
            waited += queued.Elapsed;
            if (!acquired)
                throw new ModelBusyException(budget, lastError);
            lastError = null;
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var assembler = new StreamAssembler(converse.ModelId, logger);
                var written = false;  // something was handed to the caller: no retry from here on
                var received = false; // the stream is live: later reads get the idle limit, not the first-event one
                var stopped = false;
                var sw = Stopwatch.StartNew();
                var events = source.OpenAsync(converse, attemptCts.Token).GetAsyncEnumerator(attemptCts.Token);
                Task<bool> read = Task.FromResult(false);
                try
                {
                    while (true)
                    {
                        bool more;
                        // Each read has its own limit: the idle timeout before the stop, the usage wait after it. Only
                        // a read still pending when the limit passes counts, so a slow but live stream is never cut.
                        read = events.MoveNextAsync().AsTask();
                        // The first read includes connecting and the model's time to first token, so it gets longer.
                        var limit = stopped ? UsageWait
                            : TimeSpan.FromMilliseconds(received ? _c.StreamIdleTimeoutMs : _c.FirstEventTimeoutMs);
                        if (!await CompletesWithinAsync(read, limit, ct))
                        {
                            await SettleAsync(attemptCts, read);
                            if (stopped)
                            {
                                if (!ct.IsCancellationRequested)
                                    logger.LogWarning("Bedrock {ModelId} sent no usage within {Wait}", converse.ModelId, UsageWait);
                                return;
                            }
                            ct.ThrowIfCancellationRequested();
                            // A stall is retried once per call at most: each try costs a whole idle timeout.
                            var stalled = new ModelStreamIncompleteException(converse.ModelId, $"stalled for {limit.TotalMilliseconds:0} ms");
                            if (written || stalls > 0 || attempt >= _c.MaxRetries) throw stalled;
                            stalls++;
                            lastError = stalled;
                            break;
                        }
                        try
                        {
                            more = await read;
                        }
                        catch (Exception ex) when (stopped)
                        {
                            // The answer is complete and handed out; only the usage event is missing. Don't fail it.
                            if (!ct.IsCancellationRequested)
                                logger.LogWarning(ex, "Bedrock {ModelId} stream failed after the message stop; usage is missing", converse.ModelId);
                            return;
                        }
                        catch (Exception ex) when (!written && attempt < _c.MaxRetries && IsRetryable(ex, ct))
                        {
                            lastError = ex;
                            break;
                        }
                        if (!more)
                        {
                            if (stopped) return;
                            // A stream that ends without a message stop is a truncated answer, never a success.
                            var truncated = new ModelStreamIncompleteException(converse.ModelId);
                            if (written || attempt >= _c.MaxRetries) throw truncated;
                            lastError = truncated;
                            break;
                        }

                        received = true;
                        stopped |= events.Current is MessageStopEvent;
                        foreach (var output in assembler.Accept(events.Current, sw))
                        {
                            if (output is ModelUsage u)
                                logger.LogInformation("Bedrock usage {ModelId}: in {In}, out {Out}, cache read {CacheRead}, cache write {CacheWrite}, {LatencyMs} ms",
                                    u.ModelId, u.InputTokens, u.OutputTokens, u.CacheReadInputTokens, u.CacheWriteInputTokens, u.LatencyMs);
                            written = true;
                            writer.TryWrite(output);
                        }
                        // Usage is the last event Bedrock sends after the stop; nothing else is worth waiting for.
                        if (stopped && events.Current is ConverseStreamMetadataEvent) return;
                    }
                }
                finally
                {
                    // An enumerator can't be disposed while a read is pending (the caller cancelled mid-read).
                    await SettleAsync(attemptCts, read);
                    // A failure while closing the response must not turn a delivered answer into an error.
                    try { await events.DisposeAsync(); }
                    catch (Exception ex) { logger.LogWarning(ex, "Closing the Bedrock {ModelId} stream failed", converse.ModelId); }
                }
            }
            finally
            {
                _gate.Release();
            }

            // Every path out of an attempt without a retry cause returns or throws.
            var cause = lastError ?? throw new UnreachableException();
            var delay = BackoffDelay(attempt);
            logger.LogWarning("Bedrock {ModelId} {Error}; retry {Attempt} of {Max} in {DelayMs} ms",
                converse.ModelId, cause.GetType().Name, attempt + 1, _c.MaxRetries, (int)delay.TotalMilliseconds);
            await Task.Delay(delay, ct);
        }
    }

    /// <summary>True when the read finished within the limit; false on timeout or caller cancellation.</summary>
    private static async Task<bool> CompletesWithinAsync(Task read, TimeSpan limit, CancellationToken ct)
    {
        if (read.IsCompleted) return true;
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var first = await Task.WhenAny(read, Task.Delay(limit, delayCts.Token));
        delayCts.Cancel();
        return first == read;
    }

    /// <summary>Aborts a read that is still pending (the source disposes the response on cancel) and waits for it.</summary>
    private static async Task SettleAsync(CancellationTokenSource attemptCts, Task<bool> read)
    {
        if (read.IsCompleted) return;
        try { attemptCts.Cancel(); }
        catch (AggregateException) { }
        try { await read; }
        catch (Exception) { }
    }

    /// <summary>
    /// Equal jitter: half the capped exponential bound plus a uniform share of the other half, so a retry into the
    /// team's shared, exhausted rate window never fires immediately.
    /// </summary>
    public TimeSpan BackoffDelay(int attempt)
    {
        var bound = Math.Min(_c.RetryMaxDelayMs, _c.RetryBaseDelayMs * Math.Pow(2, attempt));
        return TimeSpan.FromMilliseconds(bound / 2 + Random.Shared.NextDouble() * bound / 2);
    }

    /// <summary>
    /// Transient errors: Bedrock's throttling and availability errors (also when they arrive inside the event
    /// stream, where the SDK may wrap them in a <see cref="BedrockRuntimeEventStreamException"/>), transport
    /// failures, SDK timeouts, a truncated stream, and any other 429, 500, 502, 503 or 504 service response. The SDK's own retries
    /// are off, so this is the only retry layer. Retrying is only safe while nothing of the answer was handed out.
    /// </summary>
    public static bool IsRetryable(Exception ex, CancellationToken ct)
    {
        // Once the caller cancelled, a failing read is the abort itself, not a transient fault.
        if (ct.IsCancellationRequested) return false;
        ex = Unwrap(ex);
        return ex switch
        {
            ModelStreamIncompleteException => true,
            // ModelTimeoutException isn't here: a prompt that timed out the model usually does so again, at full price.
            ThrottlingException or ServiceUnavailableException or ModelNotReadyException or InternalServerException => true,
            // Wraps the model's own error, which can be a 4xx that would fail the same way again.
            ModelStreamErrorException stream => stream.OriginalStatusCode is null or 429 or >= 500,
            AmazonServiceException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout } => true,
            // A generic (not modelled) error without a status code: the SDK wrapped a transport failure (connection
            // reset, DNS) before any response arrived.
            AmazonServiceException { StatusCode: 0, InnerException: { } inner } generic
                when (generic.GetType() == typeof(AmazonBedrockRuntimeException) || generic.GetType() == typeof(AmazonServiceException))
                     && IsTransport(inner) => true,
            AmazonServiceException => false,
            // The SDK's client-side wrapper for a failure before any response (connection reset, DNS, timeout).
            AmazonClientException { InnerException: { } inner } client
                when client.GetType() == typeof(AmazonClientException) && IsTransport(inner) => true,
            _ => IsTransport(ex),
        };
    }

    // Only the SDK's stream wrapper is looked through; any other error that merely wraps a transient one isn't.
    private static Exception Unwrap(Exception ex)
    {
        while (ex is BedrockRuntimeEventStreamException { InnerException: { } inner }) ex = inner;
        return ex;
    }

    // An HttpClient timeout surfaces as a TaskCanceledException; IsRetryable already ruled out caller cancellation.
    private static bool IsTransport(Exception ex) =>
        ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException;

    /// <summary>
    /// Adds a cache point after the last message (the third of Bedrock's four), so a tool loop that resends the
    /// growing history reads it from the cache. The caller's messages are copied, never changed.
    /// </summary>
    private static List<Message> WithHistoryCachePoint(IReadOnlyList<Message> messages)
    {
        var copy = messages.ToList();
        // A cache point can't be a message's only content, and a caller that placed its own keeps the 4-point limit.
        if (copy.Count > 0 && copy[^1].Content is { Count: > 0 }
            && !copy.Any(m => m.Content?.Any(c => c.CachePoint is not null) == true))
        {
            var last = copy[^1];
            copy[^1] = new Message
            {
                Role = last.Role,
                Content = [.. last.Content ?? [], new ContentBlock { CachePoint = new CachePointBlock { Type = CachePointType.Default } }],
            };
        }
        return copy;
    }

    private static SystemContentBlock CachePoint() => new() { CachePoint = new CachePointBlock { Type = CachePointType.Default } };

    /// <summary>
    /// Turns ConverseStream events into text deltas, complete tool calls, the final message and usage. Blocks are
    /// tracked by their content-block index, so the reconstructed message keeps the model's block order.
    /// </summary>
    private sealed class StreamAssembler(string modelId, ILogger logger)
    {
        private sealed class Block
        {
            public readonly StringBuilder Text = new();
            public ToolUseBlockStart? Tool;
            public StringBuilder? Input; // set while a tool block is open
            public string? InputJson;    // set once the tool block stopped
        }

        private readonly SortedDictionary<int, Block> _blocks = [];

        public IEnumerable<ModelStreamEvent> Accept(IEventStreamEvent ev, Stopwatch sw)
        {
            switch (ev)
            {
                case ContentBlockStartEvent { Start.ToolUse: { } start } e:
                    var opened = BlockAt(e.ContentBlockIndex);
                    opened.Tool = start;
                    opened.Input = new StringBuilder();
                    break;
                case ContentBlockDeltaEvent { Delta: { } delta } e:
                    if (delta.Text is { Length: > 0 } text)
                    {
                        BlockAt(e.ContentBlockIndex).Text.Append(text);
                        yield return new ModelTextDelta(text);
                    }
                    else if (delta.ToolUse?.Input is { } input)
                    {
                        if (BlockAt(e.ContentBlockIndex).Input is { } open) open.Append(input);
                        else logger.LogWarning("Bedrock {ModelId} sent tool input for block {Index} without a tool start; dropped", modelId, e.ContentBlockIndex);
                    }
                    break;
                case ContentBlockStopEvent e when BlockAt(e.ContentBlockIndex) is { Tool: not null, Input: not null } block:
                    yield return Close(block);
                    break;
                case MessageStopEvent stop:
                    // A tool block whose stop never came: on a tool_use stop it is complete and closed here; on any other
                    // stop (max_tokens mid-input) it is a truncated proposal and is dropped from the answer.
                    var toolUseStop = stop.StopReason == StopReason.Tool_use;
                    foreach (var (index, open) in _blocks)
                    {
                        if (open is not { Tool: not null, Input: not null }) continue;
                        logger.LogWarning("Bedrock {ModelId} stopped ({Reason}) without closing tool block {Index}", modelId, stop.StopReason?.Value, index);
                        if (toolUseStop) yield return Close(open);
                        else open.Tool = null;
                    }
                    yield return new ModelMessageStop(stop.StopReason?.Value ?? "", BuildMessage(includeTools: toolUseStop));
                    break;
                case ConverseStreamMetadataEvent { Usage: { } usage } meta:
                    yield return new ModelUsage(modelId,
                        usage.InputTokens ?? 0, usage.OutputTokens ?? 0,
                        usage.CacheReadInputTokens ?? 0, usage.CacheWriteInputTokens ?? 0,
                        meta.Metrics?.LatencyMs ?? sw.ElapsedMilliseconds);
                    break;
            }
        }

        private static ModelToolUse Close(Block block)
        {
            block.InputJson = block.Input!.Length > 0 ? block.Input.ToString() : "{}";
            block.Input = null;
            return new ModelToolUse(block.Tool!.ToolUseId, block.Tool.Name, block.InputJson);
        }

        private Block BlockAt(int? index)
        {
            var i = index ?? 0;
            if (!_blocks.TryGetValue(i, out var b)) _blocks[i] = b = new Block();
            return b;
        }

        // Bedrock rejects an assistant message without content, and a whitespace-only text block, so those are dropped
        // and an empty answer has no message to append.
        // Tool calls belong in the history only on a tool_use stop, where tool results follow; after any other stop
        // (max_tokens) no results will come, and Bedrock rejects a toolUse without its toolResult.
        private Message? BuildMessage(bool includeTools)
        {
            var content = new List<ContentBlock>();
            foreach (var b in _blocks.Values)
            {
                if (b.Tool is { } tool && b.InputJson is { } input)
                {
                    if (includeTools)
                        content.Add(new ContentBlock { ToolUse = new ToolUseBlock { ToolUseId = tool.ToolUseId, Name = tool.Name, Input = ToDocument(tool, input) } });
                }
                else if (b.Tool is null && b.Text.ToString() is var text && !string.IsNullOrWhiteSpace(text))
                    content.Add(new ContentBlock { Text = text });
            }
            return content.Count == 0 ? null : new Message { Role = ConversationRole.Assistant, Content = content };
        }

        // Tool input is model output: anything that doesn't convert (malformed JSON, duplicate keys, a number out of
        // double range) goes back to the model as an empty object; the orchestrator rejects the call from InputJson.
        private Amazon.Runtime.Documents.Document ToDocument(ToolUseBlockStart tool, string json)
        {
            try
            {
                // Bedrock requires toolUse.input to be an object; anything else would poison the conversation.
                var document = DocumentJson.FromJson(json);
                if (document.IsDictionary()) return document;
                throw new System.Text.Json.JsonException("Tool input is not a JSON object.");
            }
            catch (Exception ex)
            {
                logger.LogWarning("Tool input of {Tool} ({ToolUseId}) is not convertible ({Error}); the history carries {{}} instead",
                    tool.Name, tool.ToolUseId, ex.GetType().Name);
                return new Amazon.Runtime.Documents.Document(new Dictionary<string, Amazon.Runtime.Documents.Document>());
            }
        }
    }
}
