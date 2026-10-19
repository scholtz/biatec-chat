using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Biatec.Chat.Api.Chat.SignIn;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Api.Telemetry;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Evidence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;

namespace Biatec.Chat.Tests.Telemetry;

public class TelemetryTests
{
    private static IConfiguration Config(bool enabled) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Telemetry:Enabled"] = enabled ? "true" : "false",
        ["Telemetry:Exporter"] = "None",
    }).Build();

    private sealed class FakeEvidence : IEvidenceLog
    {
        public readonly List<string> Kinds = [];
        public Task<EvidenceEntry> AppendAsync(Guid conversationId, string kind, JsonObject payload, Guid? anchorRef = null, CancellationToken ct = default)
        {
            Kinds.Add(kind);
            return Task.FromResult(new EvidenceEntry());
        }
        public Task<bool> DeletePayloadAsync(Guid conversationId, long seq, CancellationToken ct = default) => Task.FromResult(true);
        public Task<int> DeleteAllPayloadsAsync(Guid conversationId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<EvidenceEntry>> GetChainAsync(Guid conversationId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EvidenceEntry>>([]);
        public Task<EvidenceVerification> VerifyAsync(Guid conversationId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoTenants : Biatec.Chat.Api.Tenancy.ITenantStore
    {
        public Task<Tenant?> FindByHostAsync(string host, CancellationToken ct) => Task.FromResult<Tenant?>(null);
    }

    private sealed class OtherExecutor : IToolExecutor
    {
        public string CapabilityName => "kb.search";
        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, JsonNode arguments, CancellationToken ct) => Task.FromResult(new ToolExecutionResult("ok"));
    }

    private static ServiceCollection AppServices()
    {
        var s = new ServiceCollection();
        s.AddLogging();
        s.AddSingleton<IPolicyEventSink, InMemoryPolicyEventSink>();
        s.AddScoped<IEvidenceLog, FakeEvidence>();
        s.AddSingleton<IToolExecutor, OtherExecutor>();
        s.AddSingleton<IToolExecutor, RequestSignInExecutor>();
        return s;
    }

    /// <summary>Collects biatec.chat.* measurements as (instrument, tags) pairs.</summary>
    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _l = new();
        public readonly List<(string Name, Dictionary<string, string?> Tags)> Seen = [];
        public Recorder()
        {
            _l.InstrumentPublished = (i, l) => { if (i.Meter.Name == ChatMetrics.MeterName) l.EnableMeasurementEvents(i); };
            _l.SetMeasurementEventCallback<long>((i, _, tags, _) =>
            {
                lock (Seen) Seen.Add((i.Name, tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString())));
            });
            _l.Start();
        }
        public void Dispose() => _l.Dispose();
    }

    private static ToolExecutionContext Ctx(SessionTier tier) =>
        new(CapabilityRegistry.CreateDefault().Find(CapabilityRegistry.RequestSignIn)!, "tu1", Guid.NewGuid(), "session-secret", "tenant-1", tier, []);

    [Fact]
    public void Disabled_ByDefault_RegistersNothing()
    {
        var s = AppServices();
        var before = s.Count;
        s.AddBiatecTelemetry(new ConfigurationBuilder().Build());
        Assert.Equal(before, s.Count);
        using var sp = s.BuildServiceProvider();
        Assert.Null(sp.GetService<TracerProvider>());
        Assert.IsType<InMemoryPolicyEventSink>(sp.GetRequiredService<IPolicyEventSink>());
    }

    [Fact]
    public void Enabled_DecoratesSinkEvidenceAndOnlyTheSignInExecutor()
    {
        var s = AppServices();
        s.AddBiatecTelemetry(Config(true));
        using var sp = s.BuildServiceProvider();
        Assert.IsType<MeteringPolicyEventSink>(sp.GetRequiredService<IPolicyEventSink>());
        using var scope = sp.CreateScope();
        Assert.IsType<MeteringEvidenceLog>(scope.ServiceProvider.GetRequiredService<IEvidenceLog>());
        var executors = sp.GetServices<IToolExecutor>().ToList();
        Assert.Equal(2, executors.Count);
        Assert.IsType<OtherExecutor>(executors[0]);
        Assert.IsType<MeteringSignInExecutor>(executors[1]);
        Assert.Equal(CapabilityRegistry.RequestSignIn, executors[1].CapabilityName);
    }

    [Fact]
    public async Task Metrics_CountRefusalsPolicyViolationsAndConsent_WithLowCardinalityTagsOnly()
    {
        using var rec = new Recorder();
        var s = AppServices();
        s.AddBiatecTelemetry(Config(true));
        using var sp = s.BuildServiceProvider();

        sp.GetRequiredService<IPolicyEventSink>().Emit(new PolicyEvent("tool_call_rejected", "wallet.transfer", "tier", SessionTier.Anonymous, "session-secret", "tenant-1", DateTimeOffset.UtcNow));
        await sp.GetServices<IToolExecutor>().Last().ExecuteAsync(Ctx(SessionTier.Anonymous), new JsonObject(), default);
        using (var scope = sp.CreateScope())
        {
            var log = scope.ServiceProvider.GetRequiredService<IEvidenceLog>();
            await log.AppendAsync(Guid.NewGuid(), "consent_requested", new JsonObject());
            await log.AppendAsync(Guid.NewGuid(), "action_submitted", new JsonObject());
        }

        var seen = rec.Seen.ToList();
        Assert.Contains(seen, m => m.Name == "biatec.chat.policy_violations" && m.Tags["reason"] == "tier" && m.Tags["tier"] == "anonymous" && m.Tags["kind"] == "tool_call_rejected");
        Assert.Contains(seen, m => m.Name == "biatec.chat.refusals" && m.Tags["reason"] == "tier" && m.Tags["tier"] == "anonymous");
        Assert.Contains(seen, m => m.Name == "biatec.chat.refusals" && m.Tags["reason"] == MeteringSignInExecutor.Reason && m.Tags["tier"] == "anonymous");
        Assert.Single(seen, m => m.Name == "biatec.chat.consent");
        Assert.Equal("consent_requested", seen.Single(m => m.Name == "biatec.chat.consent").Tags["kind"]);
        Assert.All(seen, m => Assert.DoesNotContain(m.Tags.Values, v => v is "session-secret" or "tenant-1" or "wallet.transfer"));
    }

    [Fact]
    public void Metrics_FoldUnexpectedLabels()
    {
        using var rec = new Recorder();
        using var metrics = new ChatMetrics();
        metrics.Refusal(SessionTier.Authenticated, "free text from somewhere 192.168.0.1");
        Assert.Equal("other", rec.Seen.Single().Tags["reason"]);
    }

    [Fact]
    public void Scrubber_DropsAddressesUserAgentQueryAndCredentialsInUrls()
    {
        using var source = new ActivitySource("scrub-test");
        using var listener = new ActivityListener { ShouldListenTo = s => s.Name == "scrub-test", Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData };
        ActivitySource.AddActivityListener(listener);
        using var a = source.StartActivity("x")!;
        a.SetTag("client.address", "203.0.113.9");
        a.SetTag("user_agent.original", "Mozilla");
        a.SetTag("url.query", "code=abc");
        a.SetTag("url.full", "https://user:pw@oidc.example/connect/token?code=abc");
        a.SetTag("http.request.method", "POST");
        new TelemetryScrubber().OnEnd(a);
        Assert.Null(a.GetTagItem("client.address"));
        Assert.Null(a.GetTagItem("user_agent.original"));
        Assert.Null(a.GetTagItem("url.query"));
        Assert.Equal("https://oidc.example/connect/token", a.GetTagItem("url.full"));
        Assert.Equal("POST", a.GetTagItem("http.request.method"));
    }

    [Fact]
    public async Task Spans_ForOutboundHttpAndAwsCalls_WithoutQueryOrClientAddress()
    {
        // A local stand-in for every outbound dependency: HTTP (MCP, OIDC, algod, builders) and the Bedrock endpoint.
        var stub = WebApplication.CreateSlimBuilder();
        stub.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = stub.Build();
        server.Run(ctx => { ctx.Response.StatusCode = 400; return ctx.Response.WriteAsync("{\"message\":\"no\"}"); });
        await server.StartAsync();
        var baseUrl = server.Urls.First();

        var exported = new List<Activity>();
        var s = new ServiceCollection();
        s.AddLogging();
        s.AddBiatecTelemetry(Config(true));
        s.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(exported));
        await using (var sp = s.BuildServiceProvider())
        {
            var tracer = sp.GetRequiredService<TracerProvider>();

            using var http = new HttpClient();
            await http.GetAsync(baseUrl + "/connect/token?code=secret-code");

            var bedrock = new AmazonBedrockRuntimeClient(new BasicAWSCredentials("test", "test"),
                new AmazonBedrockRuntimeConfig { ServiceURL = baseUrl, AuthenticationRegion = "eu-central-1", MaxErrorRetry = 0 });
            await Assert.ThrowsAnyAsync<Exception>(() => bedrock.ConverseAsync(new ConverseRequest { ModelId = "eu.anthropic.claude-haiku-4-5-20251001-v1:0", Messages = [] }));
            tracer.ForceFlush();
        }

        var httpSpan = Assert.Single(exported, a => a.Kind == ActivityKind.Client && (a.GetTagItem("url.full") as string)?.Contains("/connect/token") == true);
        Assert.DoesNotContain("secret-code", (string)httpSpan.GetTagItem("url.full")!);
        Assert.Contains(exported, a => a.Source.Name.Contains("AWS", StringComparison.OrdinalIgnoreCase) && a.DisplayName.Contains("Converse", StringComparison.Ordinal));
        Assert.All(exported, a =>
        {
            foreach (var key in TelemetryScrubber.Dropped) Assert.Null(a.GetTagItem(key));
            Assert.DoesNotContain(a.TagObjects, t => t.Value?.ToString()?.Contains("secret-code") == true);
        });
    }

    /// <summary>The same minimal valid configuration as ConfigAndHealthTests; dependencies are down.</summary>
    private static Dictionary<string, string?> AppConfig() => new()
    {
        ["Oidc:Issuer"] = "https://oidc.example.test",
        ["Oidc:McpResource"] = "https://mcp.example.test",
        ["Mcp:Endpoint"] = "https://mcp.example.test/mcp",
        ["Redis:ConnectionString"] = "127.0.0.1:1", // closed port: dependency down
        ["Postgres:ConnectionString"] = "Host=127.0.0.1;Port=1;Database=x;Username=x",
        ["Algod:Networks:testnet:Url"] = "https://algod.example.test",
        ["Kms:KeyId"] = "alias/test",
        ["Bedrock:Region"] = "eu-central-1",
    };

    [Fact]
    public async Task App_StartsWithTelemetryEnabled_AndTracesIncomingRequests()
    {
        var exported = new List<Activity>();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            foreach (var (k, v) in AppConfig()) b.UseSetting(k, v);
            b.UseSetting("Telemetry:Enabled", "true");
            b.UseSetting("Telemetry:Exporter", "None");
            b.ConfigureServices(s =>
            {
                s.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(exported));
                s.AddSingleton<Biatec.Chat.Api.Tenancy.ITenantStore, NoTenants>(); // unknown host: 404 without a database
            });
        });
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/contracts?x=secret-q");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.IsType<MeteringPolicyEventSink>(factory.Services.GetRequiredService<IPolicyEventSink>());
        factory.Services.GetRequiredService<TracerProvider>().ForceFlush();
        var server = Assert.Single(exported, a => a.Kind == ActivityKind.Server);
        Assert.Null(server.GetTagItem("client.address"));
        Assert.DoesNotContain(server.TagObjects, t => t.Value?.ToString()?.Contains("secret-q") == true);
    }
}
