using System.Runtime.CompilerServices;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.EventStreams;
using Biatec.Chat.Api.Config;
using Biatec.Chat.Api.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Tests.Model;

public class BedrockModelClientTests
{
    /// <summary>Plays one scripted attempt per call; an Exception in the script is thrown at that point of the stream.</summary>
    private sealed class FakeSource(params object[][] attempts) : IConverseStreamSource
    {
        private int _calls;
        public int Calls => _calls;
        public List<ConverseStreamRequest> Requests { get; } = [];
        public Func<Task>? OnOpen { get; init; }

        public async IAsyncEnumerable<IEventStreamEvent> OpenAsync(ConverseStreamRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            var script = attempts[Math.Min(Interlocked.Increment(ref _calls) - 1, attempts.Length - 1)];
            lock (Requests) Requests.Add(request);
            if (OnOpen is not null) await OnOpen();
            foreach (var item in script)
            {
                if (item is Exception ex) throw ex;
                yield return (IEventStreamEvent)item;
            }
        }
    }

    private static BedrockOptions Opts(int maxConcurrency = 8, int maxRetries = 3) => new()
    {
        MaxTokens = 1000, MaxConcurrency = maxConcurrency, MaxRetries = maxRetries, RetryBaseDelayMs = 1, RetryMaxDelayMs = 5,
    };

    private static BedrockModelClient Client(IConverseStreamSource source, BedrockOptions? o = null) =>
        new(source, Options.Create(o ?? Opts()), NullLogger<BedrockModelClient>.Instance);

    private static ModelRequest Request(ModelProfile profile = ModelProfile.Anonymous, int? maxTokens = null, params ToolSpecification[] tools) => new()
    {
        Profile = profile,
        Persona = "PERSONA",
        Catalogue = "CATALOGUE",
        Messages = [new Message { Role = ConversationRole.User, Content = [new ContentBlock { Text = "hello" }] }],
        Tools = tools,
        MaxTokens = maxTokens,
    };

    private static ContentBlockDeltaEvent Text(string t) => new() { ContentBlockIndex = 0, Delta = new ContentBlockDelta { Text = t } };
    private static ContentBlockStopEvent BlockStop(int i = 0) => new() { ContentBlockIndex = i };
    private static MessageStopEvent Stop(StopReason r) => new() { StopReason = r };
    private static ConverseStreamMetadataEvent Usage(int i, int o, int cr = 0, int cw = 0) => new()
    {
        Usage = new TokenUsage { InputTokens = i, OutputTokens = o, CacheReadInputTokens = cr, CacheWriteInputTokens = cw },
        Metrics = new ConverseStreamMetrics { LatencyMs = 42 },
    };

    private static readonly object[] TextAnswer = [Text("Hel"), Text("lo"), BlockStop(), Stop(StopReason.End_turn), Usage(10, 2, 900, 0)];

    private static async Task<List<ModelStreamEvent>> Collect(IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var list = new List<ModelStreamEvent>();
        await foreach (var e in stream) list.Add(e);
        return list;
    }

    [Fact]
    public void Cache_points_follow_persona_and_catalogue()
    {
        var r = Client(new FakeSource()).BuildRequest(Request());

        Assert.Equal(4, r.System.Count);
        Assert.Equal("PERSONA", r.System[0].Text);
        Assert.Equal(CachePointType.Default, r.System[1].CachePoint.Type);
        Assert.Equal("CATALOGUE", r.System[2].Text);
        Assert.Equal(CachePointType.Default, r.System[3].CachePoint.Type);
    }

    [Fact]
    public void Max_tokens_is_always_set_and_never_above_the_configured_cap()
    {
        var c = Client(new FakeSource());
        Assert.Equal(1000, c.BuildRequest(Request()).InferenceConfig.MaxTokens);
        Assert.Equal(200, c.BuildRequest(Request(maxTokens: 200)).InferenceConfig.MaxTokens);
        Assert.Equal(1000, c.BuildRequest(Request(maxTokens: 50_000)).InferenceConfig.MaxTokens);
        Assert.Throws<ArgumentOutOfRangeException>(() => c.BuildRequest(Request(maxTokens: 0)));
    }

    [Fact]
    public void Model_id_comes_from_the_profile_and_tools_only_when_present()
    {
        var c = Client(new FakeSource());
        var anon = c.BuildRequest(Request());
        Assert.Equal("eu.anthropic.claude-sonnet-4-5-20250929-v1:0", anon.ModelId);
        Assert.Null(anon.ToolConfig);

        var tool = new ToolSpecification { Name = "requestSignIn", Description = "d", InputSchema = new ToolInputSchema { Json = DocumentJson.FromJson("""{"type":"object"}""") } };
        var auth = c.BuildRequest(Request(ModelProfile.Authenticated, tools: tool));
        Assert.Equal("eu.anthropic.claude-opus-4-5-20251101-v1:0", auth.ModelId);
        Assert.Equal("requestSignIn", Assert.Single(auth.ToolConfig.Tools).ToolSpec.Name);
    }

    [Fact]
    public async Task Streams_text_then_stop_and_usage()
    {
        var events = await Collect(Client(new FakeSource(TextAnswer)).StreamAsync(Request()));

        Assert.Equal(["Hel", "lo"], events.OfType<ModelTextDelta>().Select(t => t.Text));
        var stop = Assert.Single(events.OfType<ModelMessageStop>());
        Assert.Equal("end_turn", stop.StopReason);
        Assert.Equal(ConversationRole.Assistant, stop.Message.Role);
        Assert.Equal("Hello", Assert.Single(stop.Message.Content).Text);
        var usage = Assert.IsType<ModelUsage>(events[^1]);
        Assert.Equal(("eu.anthropic.claude-sonnet-4-5-20250929-v1:0", 10, 2, 900, 0, 42L),
            (usage.ModelId, usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens, usage.CacheWriteInputTokens, usage.LatencyMs));
    }

    [Fact]
    public async Task Assembles_tool_use_from_input_fragments()
    {
        var source = new FakeSource([
            Text("Checking."), BlockStop(0),
            new ContentBlockStartEvent { ContentBlockIndex = 1, Start = new ContentBlockStart { ToolUse = new ToolUseBlockStart { ToolUseId = "tu-1", Name = "wallet_balance" } } },
            new ContentBlockDeltaEvent { ContentBlockIndex = 1, Delta = new ContentBlockDelta { ToolUse = new ToolUseBlockDelta { Input = """{"address":"AB""" } } },
            new ContentBlockDeltaEvent { ContentBlockIndex = 1, Delta = new ContentBlockDelta { ToolUse = new ToolUseBlockDelta { Input = """C","n":2}""" } } },
            BlockStop(1), Stop(StopReason.Tool_use), Usage(5, 5),
        ]);

        var events = await Collect(Client(source).StreamAsync(Request()));

        var tool = Assert.Single(events.OfType<ModelToolUse>());
        Assert.Equal(("tu-1", "wallet_balance", """{"address":"ABC","n":2}"""), (tool.ToolUseId, tool.Name, tool.InputJson));
        var stop = Assert.Single(events.OfType<ModelMessageStop>());
        Assert.Equal("tool_use", stop.StopReason);
        Assert.Equal(2, stop.Message.Content.Count);
        Assert.Equal("Checking.", stop.Message.Content[0].Text);
        var block = stop.Message.Content[1].ToolUse;
        Assert.Equal("tu-1", block.ToolUseId);
        Assert.Equal("ABC", block.Input.AsDictionary()["address"].AsString());
        Assert.Equal(2, block.Input.AsDictionary()["n"].AsLong());
    }

    [Fact]
    public async Task Tool_use_without_input_becomes_an_empty_object()
    {
        var source = new FakeSource([
            new ContentBlockStartEvent { ContentBlockIndex = 0, Start = new ContentBlockStart { ToolUse = new ToolUseBlockStart { ToolUseId = "tu-2", Name = "requestSignIn" } } },
            BlockStop(0), Stop(StopReason.Tool_use),
        ]);

        var events = await Collect(Client(source).StreamAsync(Request()));

        Assert.Equal("{}", Assert.Single(events.OfType<ModelToolUse>()).InputJson);
    }

    [Fact]
    public async Task Retries_throttling_before_anything_was_streamed()
    {
        var source = new FakeSource(
            [new ThrottlingException("slow down")],
            [new ServiceUnavailableException("busy")],
            TextAnswer);

        var events = await Collect(Client(source).StreamAsync(Request()));

        Assert.Equal(3, source.Calls);
        Assert.Equal(["Hel", "lo"], events.OfType<ModelTextDelta>().Select(t => t.Text));
        Assert.Single(events.OfType<ModelUsage>());
    }

    [Fact]
    public async Task Does_not_retry_once_text_was_streamed()
    {
        var source = new FakeSource([Text("partial"), new ThrottlingException("mid-stream")], TextAnswer);
        var seen = new List<ModelStreamEvent>();

        await Assert.ThrowsAsync<ThrottlingException>(async () =>
        {
            await foreach (var e in Client(source).StreamAsync(Request())) seen.Add(e);
        });

        Assert.Equal(1, source.Calls);
        Assert.Equal("partial", Assert.IsType<ModelTextDelta>(Assert.Single(seen)).Text);
    }

    [Fact]
    public async Task Gives_up_after_max_retries_and_does_not_retry_validation_errors()
    {
        var throttled = new FakeSource([new ThrottlingException("slow down")]);
        await Assert.ThrowsAsync<ThrottlingException>(() => Collect(Client(throttled, Opts(maxRetries: 2)).StreamAsync(Request())));
        Assert.Equal(3, throttled.Calls);

        var invalid = new FakeSource([new ValidationException("bad model id")]);
        await Assert.ThrowsAsync<ValidationException>(() => Collect(Client(invalid).StreamAsync(Request())));
        Assert.Equal(1, invalid.Calls);
    }

    [Fact]
    public void Backoff_uses_full_jitter_under_a_capped_exponential_bound()
    {
        var c = Client(new FakeSource(), new BedrockOptions { RetryBaseDelayMs = 100, RetryMaxDelayMs = 1000 });
        for (var i = 0; i < 200; i++)
        {
            Assert.InRange(c.BackoffDelay(0).TotalMilliseconds, 0, 100);
            Assert.InRange(c.BackoffDelay(2).TotalMilliseconds, 0, 400);
            Assert.InRange(c.BackoffDelay(10).TotalMilliseconds, 0, 1000);
        }
    }

    [Fact]
    public async Task Concurrency_cap_holds_back_extra_streams()
    {
        var release = new TaskCompletionSource();
        var open = 0;
        var maxOpen = 0;
        var source = new FakeSource(TextAnswer)
        {
            OnOpen = async () =>
            {
                var now = Interlocked.Increment(ref open);
                InterlockedMax(ref maxOpen, now);
                await release.Task;
                Interlocked.Decrement(ref open);
            },
        };
        var client = Client(source, Opts(maxConcurrency: 2));

        var runs = Enumerable.Range(0, 5).Select(_ => Task.Run(() => Collect(client.StreamAsync(Request())))).ToArray();
        await Task.Delay(200);
        Assert.Equal(2, source.Calls);
        release.SetResult();
        await Task.WhenAll(runs);

        Assert.Equal(5, source.Calls);
        Assert.Equal(2, maxOpen);
    }

    [Fact]
    public async Task Abandoning_a_stream_releases_its_concurrency_slot()
    {
        var client = Client(new FakeSource(TextAnswer), Opts(maxConcurrency: 1));
        await foreach (var _ in client.StreamAsync(Request())) break;

        var events = await Collect(client.StreamAsync(Request())).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(events);
    }

    [Fact]
    public void Document_json_round_trips_nested_values()
    {
        var d = DocumentJson.FromJson("""{"a":[1,2.5,"x",true,null],"b":{"c":false}}""").AsDictionary();
        var a = d["a"].AsList();
        Assert.Equal(1, a[0].AsLong());
        Assert.Equal(2.5, a[1].AsDouble());
        Assert.Equal("x", a[2].AsString());
        Assert.True(a[3].AsBool());
        Assert.True(a[4].IsNull());
        Assert.False(d["b"].AsDictionary()["c"].AsBool());
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}
