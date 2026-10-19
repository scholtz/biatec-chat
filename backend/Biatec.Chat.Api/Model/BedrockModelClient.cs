using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
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
        await foreach (var ev in response.Stream.WithCancellation(ct))
            yield return ev;
    }
}

/// <summary>
/// ConverseStream client: picks the model id for the profile, always sets maxTokens, places a cache point after
/// each static system block, caps concurrent streams process-wide and retries throttled calls with full jitter.
/// A call is retried only while nothing has been yielded, so a caller never sees a partial answer twice. A slot
/// is held while the stream is open, including while the caller handles text and tool-use events, and is released
/// during backoff and before the final stop and usage events, so run tools after the message stop.
/// </summary>
public sealed class BedrockModelClient(
    IConverseStreamSource source,
    IOptions<BedrockOptions> options,
    IOptions<BedrockClientOptions> clientOptions,
    ILogger<BedrockModelClient> logger) : IModelClient
{
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
            // Events from the message stop on (stop and usage) are held back and handed out after the slot is
            // released, so a caller that works on the finished answer inside its loop doesn't keep a Bedrock slot.
            var tail = new List<ModelStreamEvent>();
            var tailStarted = false;
            try
            {
                var assembler = new StreamAssembler(converse.ModelId, logger);
                var yielded = false;
                var sw = Stopwatch.StartNew();
                await using var events = source.OpenAsync(converse, ct).GetAsyncEnumerator(ct);
                while (true)
                {
                    bool more;
                    try
                    {
                        more = await events.MoveNextAsync();
                    }
                    catch (Exception ex) when (tailStarted && !ct.IsCancellationRequested)
                    {
                        // The answer is complete; only the usage event can be missing. Neither retry nor fail.
                        logger.LogWarning(ex, "Bedrock {ModelId} stream failed after the message stop; usage may be missing", converse.ModelId);
                        break;
                    }
                    catch (Exception ex) when (!yielded && attempt < _c.MaxRetries && IsRetryable(ex, ct))
                    {
                        lastError = ex;
                        break;
                    }
                    if (!more)
                    {
                        if (tailStarted) break;
                        // A stream that ends without a message stop is a truncated answer, never a success.
                        var truncated = new ModelStreamIncompleteException(converse.ModelId);
                        if (yielded || attempt >= _c.MaxRetries) throw truncated;
                        lastError = truncated;
                        break;
                    }

                    tailStarted |= events.Current is MessageStopEvent;
                    foreach (var output in assembler.Accept(events.Current, sw))
                    {
                        if (tailStarted)
                        {
                            tail.Add(output);
                            continue;
                        }
                        yielded = true;
                        yield return output;
                    }
                }
            }
            finally
            {
                _gate.Release();
            }

            if (lastError is null)
            {
                foreach (var output in tail)
                {
                    if (output is ModelUsage u)
                        logger.LogInformation("Bedrock usage {ModelId}: in {In}, out {Out}, cache read {CacheRead}, cache write {CacheWrite}, {LatencyMs} ms",
                            u.ModelId, u.InputTokens, u.OutputTokens, u.CacheReadInputTokens, u.CacheWriteInputTokens, u.LatencyMs);
                    yield return output;
                }
                yield break;
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
    /// failures, SDK timeouts, a truncated stream, and any other 429 or 5xx service response. The SDK's own retries
    /// are off, so this is the only retry layer.
    /// </summary>
    public static bool IsRetryable(Exception ex, CancellationToken ct)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case ThrottlingException or ServiceUnavailableException or ModelNotReadyException or InternalServerException
                    or ModelStreamErrorException or ModelTimeoutException:
                case AmazonServiceException { StatusCode: HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError }:
                case HttpRequestException or IOException or TimeoutException:
                case OperationCanceledException when !ct.IsCancellationRequested:
                    return true;
            }
        }
        return false;
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
                        BlockAt(e.ContentBlockIndex).Input?.Append(input);
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

        // Bedrock rejects an assistant message without content, so an empty answer has no message to append.
        // A tool block that never completed is left out: the caller never got a ModelToolUse for it.
        private Message? BuildMessage()
        {
            var content = new List<ContentBlock>();
            foreach (var b in _blocks.Values)
            {
                if (b.Tool is { } tool && b.InputJson is { } input)
                    content.Add(new ContentBlock { ToolUse = new ToolUseBlock { ToolUseId = tool.ToolUseId, Name = tool.Name, Input = ToDocument(tool, input) } });
                else if (b.Tool is null && b.Text.Length > 0)
                    content.Add(new ContentBlock { Text = b.Text.ToString() });
            }
            return content.Count == 0 ? null : new Message { Role = ConversationRole.Assistant, Content = content };
        }

        // Tool input is model output: anything that doesn't convert (malformed JSON, duplicate keys, a number out of
        // double range) goes back to the model as an empty object; the orchestrator rejects the call from InputJson.
        private Amazon.Runtime.Documents.Document ToDocument(ToolUseBlockStart tool, string json)
        {
            try
            {
                return DocumentJson.FromJson(json);
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
