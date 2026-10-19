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
/// A call is retried only while nothing has been yielded, so a caller never sees a partial answer twice.
/// </summary>
public sealed class BedrockModelClient(
    IConverseStreamSource source,
    IOptions<BedrockOptions> options,
    ILogger<BedrockModelClient> logger) : IModelClient
{
    private readonly BedrockOptions _o = options.Value;
    private readonly SemaphoreSlim _gate = new(options.Value.MaxConcurrency, options.Value.MaxConcurrency);

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

        return new ConverseStreamRequest
        {
            ModelId = request.Profile == ModelProfile.Authenticated ? _o.AuthenticatedModelId : _o.AnonymousModelId,
            System = system,
            Messages = [.. request.Messages],
            InferenceConfig = new InferenceConfiguration { MaxTokens = maxTokens },
            // Bedrock rejects an empty tool list, so a request without tools carries no tool config at all.
            ToolConfig = request.Tools.Count == 0
                ? null
                : new ToolConfiguration { Tools = [.. request.Tools.Select(t => new Tool { ToolSpec = t })] },
        };
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var converse = BuildRequest(request);
        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var assembler = new StreamAssembler(converse.ModelId);
                var yielded = false;
                Exception? retryable = null;
                var sw = Stopwatch.StartNew();
                await using var events = source.OpenAsync(converse, ct).GetAsyncEnumerator(ct);
                while (true)
                {
                    bool more;
                    try
                    {
                        more = await events.MoveNextAsync();
                    }
                    catch (Exception ex) when (!yielded && attempt < _o.MaxRetries && IsRetryable(ex))
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
                if (retryable is null) yield break;

                var delay = BackoffDelay(attempt);
                logger.LogWarning("Bedrock {ModelId} {Error}; retry {Attempt} of {Max} in {DelayMs} ms",
                    converse.ModelId, retryable.GetType().Name, attempt + 1, _o.MaxRetries, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Full jitter: a uniform delay between zero and the capped exponential bound.</summary>
    public TimeSpan BackoffDelay(int attempt)
    {
        var bound = Math.Min(_o.RetryMaxDelayMs, _o.RetryBaseDelayMs * Math.Pow(2, attempt));
        return TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * bound);
    }

    public static bool IsRetryable(Exception ex) =>
        ex is ThrottlingException or ServiceUnavailableException or ModelNotReadyException or InternalServerException;

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
                    yield return new ModelMessageStop(_stopReason, new Message { Role = ConversationRole.Assistant, Content = [.. _content] });
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
