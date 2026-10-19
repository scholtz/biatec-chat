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
        await using var abort = ct.Register(response.Dispose);
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
/// order: text and tool-use deltas, the message stop, then usage (which the client also logs).
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
            Messages = [.. request.Messages],
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
                pumpCts.Cancel();
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
                var written = false;
                var stopped = false;
                var sw = Stopwatch.StartNew();
                var events = source.OpenAsync(converse, attemptCts.Token).GetAsyncEnumerator(attemptCts.Token);
                try
                {
                    while (true)
                    {
                        bool more;
                        try
                        {
                            more = await events.MoveNextAsync();
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

                        if (events.Current is MessageStopEvent)
                        {
                            stopped = true;
                            attemptCts.CancelAfter(UsageWait);
                        }
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
                    // A failure while closing the response must not turn a delivered answer into an error.
                    try { await events.DisposeAsync(); }
                    catch (Exception ex) { logger.LogWarning(ex, "Closing the Bedrock {ModelId} stream failed", converse.ModelId); }
                }
            }
            finally
            {
                _gate.Release();
            }

            var delay = BackoffDelay(attempt);
            logger.LogWarning("Bedrock {ModelId} {Error}; retry {Attempt} of {Max} in {DelayMs} ms",
                converse.ModelId, lastError.GetType().Name, attempt + 1, _c.MaxRetries, (int)delay.TotalMilliseconds);
            await Task.Delay(delay, ct);
        }
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
    /// are off, so this is the only retry layer.
    /// </summary>
    public static bool IsRetryable(Exception ex, CancellationToken ct)
    {
        // Only the SDK's stream wrapper is looked through; any other error that merely wraps a transient one isn't.
        while (ex is BedrockRuntimeEventStreamException { InnerException: { } inner }) ex = inner;
        return ex switch
        {
            ThrottlingException or ServiceUnavailableException or ModelNotReadyException or InternalServerException
                or ModelTimeoutException => true,
            // Wraps the model's own error, which can be a 4xx that would fail the same way again.
            ModelStreamErrorException stream => stream.OriginalStatusCode is null or 429 or >= 500,
            AmazonServiceException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout } => true,
            // A generic (not modelled) error without a status code: the SDK wrapped a transport failure (connection
            // reset, DNS) before any response arrived.
            AmazonServiceException { StatusCode: 0, InnerException: { } inner } generic
                when (generic.GetType() == typeof(AmazonBedrockRuntimeException) || generic.GetType() == typeof(AmazonServiceException))
                     && IsTransport(inner, ct) => true,
            AmazonServiceException => false,
            // The SDK's client-side wrapper for a failure before any response (connection reset, DNS, timeout).
            AmazonClientException { InnerException: { } inner } client
                when client.GetType() == typeof(AmazonClientException) && IsTransport(inner, ct) => true,
            _ => IsTransport(ex, ct),
        };
    }

    // An HttpClient timeout surfaces as a TaskCanceledException the caller didn't ask for.
    private static bool IsTransport(Exception ex, CancellationToken ct) => ex switch
    {
        HttpRequestException or IOException or TimeoutException => true,
        OperationCanceledException => !ct.IsCancellationRequested,
        _ => false,
    };

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
                case ContentBlockStopEvent e when BlockAt(e.ContentBlockIndex) is { Tool: { } tool, Input: { } raw } block:
                    block.InputJson = raw.Length > 0 ? raw.ToString() : "{}";
                    block.Input = null;
                    yield return new ModelToolUse(tool.ToolUseId, tool.Name, block.InputJson);
                    break;
                case MessageStopEvent stop:
                    yield return new ModelMessageStop(stop.StopReason?.Value ?? "", BuildMessage());
                    break;
                case ConverseStreamMetadataEvent { Usage: { } usage } meta:
                    yield return new ModelUsage(modelId,
                        usage.InputTokens ?? 0, usage.OutputTokens ?? 0,
                        usage.CacheReadInputTokens ?? 0, usage.CacheWriteInputTokens ?? 0,
                        meta.Metrics?.LatencyMs ?? sw.ElapsedMilliseconds);
                    break;
            }
        }

        private Block BlockAt(int? index)
        {
            var i = index ?? 0;
            if (!_blocks.TryGetValue(i, out var b)) _blocks[i] = b = new Block();
            return b;
        }

        // Bedrock rejects an assistant message without content, and a whitespace-only text block, so those are dropped
        // and an empty answer has no message to append.
        // A tool block that never completed is left out: the caller never got a ModelToolUse for it.
        private Message? BuildMessage()
        {
            var content = new List<ContentBlock>();
            foreach (var b in _blocks.Values)
            {
                if (b.Tool is { } tool && b.InputJson is { } input)
                    content.Add(new ContentBlock { ToolUse = new ToolUseBlock { ToolUseId = tool.ToolUseId, Name = tool.Name, Input = ToDocument(tool, input) } });
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
                return DocumentJson.FromJson("{}");
            }
        }
    }
}
