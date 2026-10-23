using System.Net;
using System.Text;
using Biatec.Chat.Api.Security;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Tests.Security;

/// <summary>The global request body limit and the bounded cost of the unauthenticated /health (BCHAT-035). No database is needed.</summary>
public sealed class RequestLimitsTests
{
    private sealed class CountingHealth(HealthStatus status) : HealthCheckService
    {
        public int Runs;
        public override Task<HealthReport> CheckHealthAsync(Func<HealthCheckRegistration, bool>? predicate, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Runs);
            return Task.FromResult(new HealthReport(new Dictionary<string, HealthReportEntry>(), status, TimeSpan.Zero));
        }
    }

    /// <summary>No tenant exists: whatever passes the body limit ends as an unknown host (404), without touching a database.</summary>
    private sealed class NoTenants : ITenantStore
    {
        public Task<Tenant?> FindByHostAsync(string host, CancellationToken ct) => Task.FromResult<Tenant?>(null);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static WebApplicationFactory<Program> Factory(Action<Dictionary<string, string?>>? mutate = null, Action<IServiceCollection>? services = null)
    {
        var cfg = new Dictionary<string, string?>
        {
            ["Oidc:Issuer"] = "https://oidc.example.test",
            ["Oidc:McpResource"] = "https://mcp.example.test",
            ["Mcp:Endpoint"] = "https://mcp.example.test/mcp",
            ["Redis:ConnectionString"] = "127.0.0.1:1",
            ["Postgres:ConnectionString"] = "Host=127.0.0.1;Port=1;Database=x;Username=x",
            ["Algod:Networks:testnet:Url"] = "https://algod.example.test",
            ["Kms:KeyId"] = "alias/test",
            ["Bedrock:Region"] = "eu-central-1",
        };
        mutate?.Invoke(cfg);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(cfg));
            b.ConfigureServices(s => { s.RemoveAll<ITenantStore>(); s.AddSingleton<ITenantStore>(new NoTenants()); });
            if (services is not null) b.ConfigureServices(services);
        });
    }

    private static HttpRequestMessage Post(string path, int bytes) =>
        new(HttpMethod.Post, path) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(new string('x', bytes))) };

    [Fact]
    public async Task A_declared_body_over_the_limit_is_refused_with_413_before_any_handler()
    {
        using var f = Factory();
        var client = f.CreateClient();
        foreach (var path in new[] { "/chat", "/public/chat", "/session/keepalive", "/auth/popup", "/admin/api/logout", "/nowhere" })
        {
            var tooBig = await client.SendAsync(Post(path, 64 * 1024 + 1));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        }
        // At the limit and below, the request reaches the pipeline (an unknown host is a 404 there, not a 413).
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(Post("/chat", 64 * 1024))).StatusCode);
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(Post("/chat", 10))).StatusCode);
    }

    [Fact]
    public async Task The_limit_is_configurable()
    {
        using var f = Factory(c => c["Limits:MaxRequestBodyBytes"] = "100");
        var client = f.CreateClient();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(Post("/chat", 101))).StatusCode);
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(Post("/chat", 100))).StatusCode);
    }

    [Fact]
    public async Task A_body_without_a_declared_length_is_capped_by_the_server_feature_and_a_stricter_route_limit_is_kept()
    {
        var options = Options.Create(new RequestLimitsOptions { MaxRequestBodyBytes = 1000 });
        var services = new ServiceCollection().AddSingleton(options).BuildServiceProvider();

        static HttpContext Context(IServiceProvider sp, MaxBodyFeature feature)
        {
            var ctx = new DefaultHttpContext { RequestServices = sp };
            ctx.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
            return ctx;
        }

        async Task<long?> RunAsync(long? serverDefault)
        {
            var feature = new MaxBodyFeature { MaxRequestBodySize = serverDefault };
            var ctx = Context(services, feature);
            var app = new Microsoft.AspNetCore.Builder.ApplicationBuilder(services);
            app.UseRequestBodyLimit();
            await app.Build()(ctx);
            return feature.MaxRequestBodySize;
        }

        Assert.Equal(1000, await RunAsync(30_000_000));  // Kestrel's default is lowered
        Assert.Equal(1000, await RunAsync(null));        // unlimited is lowered
        Assert.Equal(500, await RunAsync(500));          // a stricter limit stays
    }

    private sealed class MaxBodyFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }

    [Fact]
    public async Task Health_runs_the_dependency_checks_once_per_cache_window_and_keeps_its_answers()
    {
        var health = new CountingHealth(HealthStatus.Healthy);
        var clock = new Clock();
        using var f = Factory(services: s =>
        {
            s.AddSingleton<HealthCheckService>(health);
            s.AddSingleton<TimeProvider>(clock);
        });
        var client = f.CreateClient();
        for (var i = 0; i < 20; i++)
        {
            var r = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("Healthy", await r.Content.ReadAsStringAsync());
        }
        Assert.Equal(1, health.Runs);

        clock.Now = clock.Now.AddSeconds(6); // past the 5 s window
        await client.GetAsync("/health");
        Assert.Equal(2, health.Runs);
    }

    [Theory]
    [InlineData(HealthStatus.Unhealthy, HttpStatusCode.ServiceUnavailable, "Unhealthy")]
    [InlineData(HealthStatus.Degraded, HttpStatusCode.OK, "Degraded")]
    public async Task Health_status_codes_are_unchanged(HealthStatus status, HttpStatusCode code, string body)
    {
        using var f = Factory(services: s => s.AddSingleton<HealthCheckService>(new CountingHealth(status)));
        var r = await f.CreateClient().GetAsync("/health");
        Assert.Equal(code, r.StatusCode);
        Assert.Equal(body, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_is_rate_limited_per_remote_ip_and_health_live_is_not()
    {
        using var f = Factory(c => c["Limits:HealthRateLimitPermits"] = "3", s => s.AddSingleton<HealthCheckService>(new CountingHealth(HealthStatus.Healthy)));
        var client = f.CreateClient();
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/health")).StatusCode);
        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }
}
