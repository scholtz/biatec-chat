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

    private static BedrockClientOptions Opts(int maxConcurrency = 8, int maxRetries = 3, int queueTimeoutMs = 30000, int baseDelayMs = 1) => new()
    {
        MaxConcurrency = maxConcurrency, MaxRetries = maxRetries, QueueTimeoutMs = queueTimeoutMs,
        RetryBaseDelayMs = baseDelayMs, RetryMaxDelayMs = Math.Max(5, baseDelayMs),
    };

    private static BedrockModelClient Client(IConverseStreamSource source, BedrockClientOptions? c = null) =>
        new(source, Options.Create(new BedrockOptions { MaxTokens = 1000 }), Options.Create(c ?? Opts()), NullLogger<BedrockModelClient>.Instance);

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
        Assert.Equal("Hello", Assert.Single(stop.Message!.Content).Text);
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
        Assert.Equal(2, stop.Message!.Content.Count);
        Assert.Equal("Checking.", stop.Message.Content[0].Text);
        var block = stop.Message!.Content[1].ToolUse;
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
    public void Backoff_uses_equal_jitter_under_a_capped_exponential_bound()
    {
        var c = Client(new FakeSource(), new BedrockClientOptions { RetryBaseDelayMs = 100, RetryMaxDelayMs = 1000 });
        for (var i = 0; i < 200; i++)
        {
            Assert.InRange(c.BackoffDelay(0).TotalMilliseconds, 50, 100);
            Assert.InRange(c.BackoffDelay(2).TotalMilliseconds, 200, 400);
            Assert.InRange(c.BackoffDelay(10).TotalMilliseconds, 500, 1000);
        }
    }

    [Fact]
    public async Task A_stream_that_ends_before_the_message_stop_is_truncated_not_done()
    {
        var retried = new FakeSource([], TextAnswer);
        Assert.Single((await Collect(Client(retried).StreamAsync(Request()))).OfType<ModelMessageStop>());
        Assert.Equal(2, retried.Calls);

        var afterText = new FakeSource([Text("partial")], TextAnswer);
        await Assert.ThrowsAsync<ModelStreamIncompleteException>(() => Collect(Client(afterText).StreamAsync(Request())));
        Assert.Equal(1, afterText.Calls);
    }

    [Fact]
    public async Task An_error_after_the_message_stop_keeps_the_answer_and_is_not_retried()
    {
        var source = new FakeSource([Text("Hi"), BlockStop(), Stop(StopReason.End_turn), new ModelStreamErrorException("late")], TextAnswer);

        var events = await Collect(Client(source).StreamAsync(Request()));

        Assert.Equal(1, source.Calls);
        Assert.Equal("Hi", Assert.Single(events.OfType<ModelMessageStop>()).Message!.Content[0].Text);
        Assert.Empty(events.OfType<ModelUsage>());
    }

    [Fact]
    public void Model_timeouts_and_5xx_or_429_responses_are_transient()
    {
        Assert.True(BedrockModelClient.IsRetryable(new ModelTimeoutException("slow model"), default));
        Assert.True(BedrockModelClient.IsRetryable(new AmazonBedrockRuntimeException("bad gateway") { StatusCode = System.Net.HttpStatusCode.BadGateway }, default));
        Assert.True(BedrockModelClient.IsRetryable(new AmazonBedrockRuntimeException("rate") { StatusCode = System.Net.HttpStatusCode.TooManyRequests }, default));
        Assert.False(BedrockModelClient.IsRetryable(new AmazonBedrockRuntimeException("denied") { StatusCode = System.Net.HttpStatusCode.Forbidden }, default));
        Assert.False(BedrockModelClient.IsRetryable(new AccessDeniedException("no"), default));
        Assert.False(BedrockModelClient.IsRetryable(new AmazonBedrockRuntimeException("not implemented") { StatusCode = System.Net.HttpStatusCode.NotImplemented }, default));
        // Only the stream wrapper is looked through; another error that wraps a transient one fails fast.
        Assert.False(BedrockModelClient.IsRetryable(new InvalidOperationException("unmarshal", new IOException("eof")), default));
        Assert.False(BedrockModelClient.IsRetryable(new ValidationException("bad", new HttpRequestException("x")), default));
    }

    [Fact]
    public async Task Caller_cancelling_after_the_message_stop_still_gets_the_answer()
    {
        using var cts = new CancellationTokenSource();
        var source = new FakeSource([Text("Hi"), BlockStop(), Stop(StopReason.End_turn), new OperationCanceledException(cts.Token)]);
        cts.Cancel();

        var events = await Collect(Client(source).StreamAsync(Request()));

        Assert.Single(events.OfType<ModelMessageStop>());
    }

    [Fact]
    public void Retry_max_delay_below_the_base_delay_is_invalid()
    {
        var o = new BedrockClientOptions { RetryBaseDelayMs = 5000, RetryMaxDelayMs = 100 };
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        Assert.False(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(o, new(o), results, validateAllProperties: true));
        Assert.Contains(results, r => r.ErrorMessage!.Contains("RetryMaxDelayMs"));
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
    public async Task Retries_errors_raised_inside_the_event_stream()
    {
        var wrapped = new BedrockRuntimeEventStreamException("stream error", new ThrottlingException("slow down"));
        var source = new FakeSource([wrapped], [new ModelStreamErrorException("model hiccup")], TextAnswer);

        var events = await Collect(Client(source).StreamAsync(Request()));

        Assert.Equal(3, source.Calls);
        Assert.Single(events.OfType<ModelUsage>());
    }

    [Fact]
    public async Task Backoff_releases_the_concurrency_slot()
    {
        // One slot; the first call is throttled and backs off for up to 2 s. Another call gets the slot meanwhile.
        var source = new FakeSource([new ThrottlingException("slow down")], TextAnswer);
        var client = Client(source, Opts(maxConcurrency: 1, baseDelayMs: 2000));
        var throttled = Task.Run(() => Collect(client.StreamAsync(Request())));
        while (source.Calls == 0) await Task.Delay(5);

        var other = await Collect(client.StreamAsync(Request())).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEmpty(other);
        await throttled;
    }

    [Fact]
    public async Task Waiting_for_a_slot_times_out_with_model_busy()
    {
        var release = new TaskCompletionSource();
        var source = new FakeSource(TextAnswer) { OnOpen = () => release.Task };
        var client = Client(source, Opts(maxConcurrency: 1, queueTimeoutMs: 100));
        var holder = Task.Run(() => Collect(client.StreamAsync(Request())));
        while (source.Calls == 0) await Task.Delay(5);

        await Assert.ThrowsAsync<ModelBusyException>(() => Collect(client.StreamAsync(Request())));

        release.SetResult();
        await holder;
    }

    [Fact]
    public async Task Empty_answer_has_no_message_to_append()
    {
        var events = await Collect(Client(new FakeSource([Stop(StopReason.End_turn), Usage(1, 0)])).StreamAsync(Request()));

        Assert.Null(Assert.Single(events.OfType<ModelMessageStop>()).Message);
    }

    [Fact]
    public void Tool_history_without_tools_is_rejected_before_calling_bedrock()
    {
        var request = Request() with
        {
            Messages =
            [
                new Message { Role = ConversationRole.User, Content = [new ContentBlock { Text = "hi" }] },
                new Message { Role = ConversationRole.Assistant, Content = [new ContentBlock { ToolUse = new ToolUseBlock { ToolUseId = "t", Name = "kb_search", Input = DocumentJson.FromJson("{}") } }] },
            ],
        };

        Assert.Throws<ArgumentException>(() => Client(new FakeSource()).BuildRequest(request));
    }

    private static object[] ToolCall(string id, string inputJson, int index = 0) =>
    [
        new ContentBlockStartEvent { ContentBlockIndex = index, Start = new ContentBlockStart { ToolUse = new ToolUseBlockStart { ToolUseId = id, Name = "wallet_transfer" } } },
        new ContentBlockDeltaEvent { ContentBlockIndex = index, Delta = new ContentBlockDelta { ToolUse = new ToolUseBlockDelta { Input = inputJson } } },
        BlockStop(index),
    ];

    [Theory]
    [InlineData("""{"amount":1,"amount":2}""")]
    [InlineData("""{"amount":1e400}""")]
    [InlineData("""{"amount":""")]
    public async Task Odd_tool_input_reaches_the_caller_raw_and_never_breaks_the_stream(string input)
    {
        var events = await Collect(Client(new FakeSource([.. ToolCall("tu-1", input), Stop(StopReason.Tool_use)])).StreamAsync(Request()));

        Assert.Equal(input, Assert.Single(events.OfType<ModelToolUse>()).InputJson);
        Assert.NotNull(Assert.Single(events.OfType<ModelMessageStop>()).Message!.Content[0].ToolUse);
    }

    [Fact]
    public async Task Raw_tool_input_keeps_uint64_digits_the_document_cannot()
    {
        const string input = """{"amount":18446744073709551615}""";
        var events = await Collect(Client(new FakeSource([.. ToolCall("tu-1", input), Stop(StopReason.Tool_use)])).StreamAsync(Request()));

        Assert.Equal(input, Assert.Single(events.OfType<ModelToolUse>()).InputJson);
        var doc = Assert.Single(events.OfType<ModelMessageStop>()).Message!.Content[0].ToolUse.Input.AsDictionary();
        Assert.True(doc["amount"].IsDouble());
    }

    [Fact]
    public async Task Blocks_are_assembled_by_index()
    {
        // A stop for another block (say reasoning) while the tool is open doesn't close the tool early.
        var source = new FakeSource([
            new ContentBlockStartEvent { ContentBlockIndex = 1, Start = new ContentBlockStart { ToolUse = new ToolUseBlockStart { ToolUseId = "tu-1", Name = "kb_search" } } },
            new ContentBlockDeltaEvent { ContentBlockIndex = 1, Delta = new ContentBlockDelta { ToolUse = new ToolUseBlockDelta { Input = """{"q":""" } } },
            BlockStop(0),
            new ContentBlockDeltaEvent { ContentBlockIndex = 1, Delta = new ContentBlockDelta { ToolUse = new ToolUseBlockDelta { Input = "\"fees\"}" } } },
            BlockStop(1),
            new ContentBlockDeltaEvent { ContentBlockIndex = 2, Delta = new ContentBlockDelta { Text = "Done" } },
            BlockStop(2),
            Stop(StopReason.Tool_use),
        ]);

        var events = await Collect(Client(source).StreamAsync(Request()));

        Assert.Equal("""{"q":"fees"}""", Assert.Single(events.OfType<ModelToolUse>()).InputJson);
        var content = Assert.Single(events.OfType<ModelMessageStop>()).Message!.Content;
        Assert.Equal(2, content.Count);
        Assert.Equal("tu-1", content[0].ToolUse.ToolUseId);
        Assert.Equal("Done", content[1].Text);
    }

    [Fact]
    public async Task Retries_transport_failures_but_not_caller_cancellation()
    {
        var source = new FakeSource([new HttpRequestException("connection reset")], [new TaskCanceledException("sdk timeout")], TextAnswer);
        await Collect(Client(source).StreamAsync(Request()));
        Assert.Equal(3, source.Calls);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(BedrockModelClient.IsRetryable(new OperationCanceledException(cts.Token), cts.Token));
    }

    [Fact]
    public async Task Slot_is_free_while_the_caller_handles_the_message_stop()
    {
        var client = Client(new FakeSource(TextAnswer), Opts(maxConcurrency: 1, queueTimeoutMs: 1000));
        await foreach (var e in client.StreamAsync(Request()))
        {
            if (e is not ModelMessageStop) continue;
            // Still inside the first loop, as a tool executor would be: a second call must get the slot.
            Assert.NotEmpty(await Collect(client.StreamAsync(Request())));
        }
    }

    [Fact]
    public async Task Busy_after_a_retry_keeps_the_retry_cause()
    {
        // The first call is throttled while a second call already queues for the slot; that second call then holds
        // the slot, so the first call's retry can't get it back.
        var release = new TaskCompletionSource();
        var blocker = new FakeSource(TextAnswer) { OnOpen = () => release.Task };
        BedrockModelClient client = null!;
        Task? holder = null;
        var throttled = new FakeSource([new ThrottlingException("slow down")])
        {
            OnOpen = async () =>
            {
                holder = Task.Run(() => Collect(client.StreamAsync(Request())));
                await Task.Delay(100);
            },
        };
        client = Client(new SwitchSource(throttled, blocker), Opts(maxConcurrency: 1, queueTimeoutMs: 300, baseDelayMs: 1));

        var busy = await Assert.ThrowsAsync<ModelBusyException>(() => Collect(client.StreamAsync(Request())));
        Assert.IsType<ThrottlingException>(busy.InnerException);
        release.SetResult();
        await holder!;
    }

    /// <summary>The first call goes to <c>first</c>, every later one to <c>rest</c>.</summary>
    private sealed class SwitchSource(IConverseStreamSource first, IConverseStreamSource rest) : IConverseStreamSource
    {
        private int _calls;
        public IAsyncEnumerable<IEventStreamEvent> OpenAsync(ConverseStreamRequest request, CancellationToken ct) =>
            (Interlocked.Increment(ref _calls) == 1 ? first : rest).OpenAsync(request, ct);
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
