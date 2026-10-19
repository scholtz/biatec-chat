using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
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
/// is held while a stream is open (including while the caller reads it) and released during backoff.
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
        var queueTimeout = TimeSpan.FromMilliseconds(_c.QueueTimeoutMs);
        for (var attempt = 0; ; attempt++)
        {
            if (!await _gate.WaitAsync(queueTimeout, ct))
                throw new ModelBusyException(queueTimeout);
            Exception? retryable = null;
            try
            {
                var assembler = new StreamAssembler(converse.ModelId);
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
                    catch (Exception ex) when (!yielded && attempt < _c.MaxRetries && IsRetryable(ex))
                    {
                        retryable = ex;
                        break;
                    }
                    if (!more) break;

                    foreach (var output in assembler.Accept(events.Current, sw))
                    {
                        yielded = true;
                        if (output is ModelUsage u)
                            logger.LogInformation("Bedrock usage {ModelId}: in {In}, out {Out}, cache read {CacheRead}, cache write {CacheWrite}, {LatencyMs} ms",
                                u.ModelId, u.InputTokens, u.OutputTokens, u.CacheReadInputTokens, u.CacheWriteInputTokens, u.LatencyMs);
                        yield return output;
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
            if (retryable is null) yield break;

            var delay = BackoffDelay(attempt);
            logger.LogWarning("Bedrock {ModelId} {Error}; retry {Attempt} of {Max} in {DelayMs} ms",
                converse.ModelId, retryable.GetType().Name, attempt + 1, _c.MaxRetries, (int)delay.TotalMilliseconds);
            await Task.Delay(delay, ct);
        }
    }

    /// <summary>Full jitter: a uniform delay between zero and the capped exponential bound.</summary>
    public TimeSpan BackoffDelay(int attempt)
    {
        var bound = Math.Min(_c.RetryMaxDelayMs, _c.RetryBaseDelayMs * Math.Pow(2, attempt));
        return TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * bound);
    }

    /// <summary>
    /// Transient errors, also when they arrive inside the event stream, where the SDK may wrap them in a
    /// <see cref="BedrockRuntimeEventStreamException"/>.
    /// </summary>
    public static bool IsRetryable(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is ThrottlingException or ServiceUnavailableException or ModelNotReadyException or InternalServerException or ModelStreamErrorException)
                return true;
        return false;
    }

    private static SystemContentBlock CachePoint() => new() { CachePoint = new CachePointBlock { Type = CachePointType.Default } };

    /// <summary>Turns ConverseStream events into text deltas, complete tool calls, the final message and usage.</summary>
    private sealed class StreamAssembler(string modelId)
    {
        private readonly List<ContentBlock> _content = [];
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _toolInput = new();
        private ToolUseBlockStart? _tool;
        private string? _stopReason;

        public IEnumerable<ModelStreamEvent> Accept(IEventStreamEvent ev, Stopwatch sw)
        {
            switch (ev)
            {
                case ContentBlockStartEvent { Start.ToolUse: { } start }:
                    FlushText();
                    _tool = start;
                    _toolInput.Clear();
                    break;
                case ContentBlockDeltaEvent { Delta: { } delta }:
                    if (delta.Text is { Length: > 0 } text)
                    {
                        _text.Append(text);
                        yield return new ModelTextDelta(text);
                    }
                    else if (delta.ToolUse?.Input is { } input)
                    {
                        _toolInput.Append(input);
                    }
                    break;
                case ContentBlockStopEvent:
                    if (_tool is { } tool)
                    {
                        var json = _toolInput.Length == 0 ? "{}" : _toolInput.ToString();
                        _content.Add(new ContentBlock
                        {
                            ToolUse = new ToolUseBlock { ToolUseId = tool.ToolUseId, Name = tool.Name, Input = ToDocument(json) },
                        });
                        _tool = null;
                        yield return new ModelToolUse(tool.ToolUseId, tool.Name, json);
                    }
                    else
                    {
                        FlushText();
                    }
                    break;
                case MessageStopEvent stop:
                    FlushText();
                    _stopReason = stop.StopReason?.Value ?? "";
                    // Bedrock rejects an assistant message without content, so an empty answer has no message to append.
                    yield return new ModelMessageStop(_stopReason,
                        _content.Count == 0 ? null : new Message { Role = ConversationRole.Assistant, Content = [.. _content] });
                    break;
                case ConverseStreamMetadataEvent { Usage: { } usage } meta:
                    yield return new ModelUsage(modelId,
                        usage.InputTokens ?? 0, usage.OutputTokens ?? 0,
                        usage.CacheReadInputTokens ?? 0, usage.CacheWriteInputTokens ?? 0,
                        meta.Metrics?.LatencyMs ?? sw.ElapsedMilliseconds);
                    break;
            }
        }

        private void FlushText()
        {
            if (_text.Length == 0) return;
            _content.Add(new ContentBlock { Text = _text.ToString() });
            _text.Clear();
        }

        // Malformed tool input still goes back to the model as an empty object; the orchestrator rejects the call.
        private static Amazon.Runtime.Documents.Document ToDocument(string json)
        {
            try { return DocumentJson.FromJson(json); }
            catch (System.Text.Json.JsonException) { return DocumentJson.FromJson("{}"); }
        }
    }
}
