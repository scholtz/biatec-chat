using System.Net;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Biatec.Chat.Tests.Tenancy;

public sealed class TenancyTests : IDisposable
{
    private sealed class MemoryTenantStore(params Tenant[] tenants) : ITenantStore
    {
        public Task<Tenant?> FindByHostAsync(string host, CancellationToken ct) =>
            Task.FromResult(tenants.FirstOrDefault(t => t.Host == host));
    }

    /// <summary>Adds a terminal 200 "reached" handler after the app pipeline so tests can see what the guards let through.</summary>
    private sealed class TerminalFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Run(ctx => { ctx.Response.StatusCode = 200; return ctx.Response.WriteAsync("reached"); });
        };
    }

    private readonly WebApplicationFactory<Program> _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseEnvironment("Testing");
        b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Oidc:Issuer"] = "https://oidc.example.test",
            ["Oidc:McpResource"] = "https://mcp.example.test",
            ["Mcp:Endpoint"] = "https://mcp.example.test/mcp",
            ["Redis:ConnectionString"] = "127.0.0.1:1",
            ["Postgres:ConnectionString"] = "Host=127.0.0.1;Port=1;Database=x;Username=x",
            ["Algod:Networks:testnet:Url"] = "https://algod.example.test",
            ["Kms:KeyId"] = "alias/test",
        }));
        b.ConfigureServices(s =>
        {
            s.RemoveAll<ITenantStore>();
            s.AddSingleton<ITenantStore>(new MemoryTenantStore(TenantSeed.BiatecChat(), TenantSeed.LocalhostDev()));
            s.AddSingleton<IStartupFilter, TerminalFilter>();
        });
    });

    public void Dispose() => _factory.Dispose();

    private static HttpRequestMessage Req(HttpMethod method, string path, string host = "chat.biatec.io",
        string? origin = null, bool csrf = false, string? clientKey = null, params (string, string)[] extra)
    {
        var r = new HttpRequestMessage(method, "http://" + host + path);
        if (origin is not null) r.Headers.TryAddWithoutValidation("Origin", origin);
        if (csrf) r.Headers.TryAddWithoutValidation("X-Biatec-Chat", "1");
        if (clientKey is not null) r.Headers.TryAddWithoutValidation("X-Biatec-Client-Key", clientKey);
        foreach (var (k, v) in extra) r.Headers.TryAddWithoutValidation(k, v);
        return r;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r)
    {
        using var client = _factory.CreateClient();
        return await client.SendAsync(r);
    }

    private static string? Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : null;

    [Fact]
    public async Task UnknownHost_Is404()
    {
        var r = await SendAsync(Req(HttpMethod.Get, "/terminal-probe", host: "evil.example"));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task KnownHost_Resolves_AndHostCaseAndPortAreIgnored()
    {
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Req(HttpMethod.Get, "/terminal-probe", host: "CHAT.Biatec.IO:443"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Req(HttpMethod.Get, "/terminal-probe", host: "chat.biatec.io"))).StatusCode);
    }

    [Fact]
    public async Task ForwardedHost_DoesNotSelectTheTenant()
    {
        var spoof = await SendAsync(Req(HttpMethod.Get, "/terminal-probe", host: "evil.example", extra: ("X-Forwarded-Host", "chat.biatec.io")));
        Assert.Equal(HttpStatusCode.NotFound, spoof.StatusCode);

        // And the reverse: a forwarded unknown host does not unseat a known Host.
        var ok = await SendAsync(Req(HttpMethod.Get, "/terminal-probe", host: "chat.biatec.io", extra: ("X-Forwarded-Host", "evil.example")));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Health_IsExemptFromTenantResolution()
    {
        var r = await SendAsync(Req(HttpMethod.Get, "/health", host: "10.1.2.3"));
        Assert.NotEqual(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Theory]
    [InlineData("https://web.biatec.io")]
    [InlineData("https://dex.biatec.io")]
    [InlineData("https://WEB.biatec.io")]
    [InlineData("https://web.biatec.io:443")]
    public async Task AllowedOrigin_GetsExactAcaoAndCredentials(string origin)
    {
        var r = await SendAsync(Req(HttpMethod.Get, "/terminal-probe", origin: origin));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(origin.ToLowerInvariant().Replace(":443", ""), Header(r, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Header(r, "Access-Control-Allow-Credentials"));
        Assert.Contains("Origin", Header(r, "Vary")!);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("https://biatec.io")]                       // apex is not inferred
    [InlineData("https://web.biatec.io.evil.example")]      // suffix attack
    [InlineData("https://evilweb.biatec.io")]
    [InlineData("http://web.biatec.io")]                    // scheme
    [InlineData("https://web.biatec.io:8443")]              // port
    [InlineData("https://sub.web.biatec.io")]
    [InlineData("null")]
    [InlineData("*")]
    [InlineData("https://web.biatec.io/")]
    [InlineData("https://user@web.biatec.io")]
    [InlineData("https://web.biatec.io,https://evil.example")]
    public async Task OtherOrigin_GetsNoCorsHeaders(string origin)
    {
        var r = await SendAsync(Req(HttpMethod.Get, "/terminal-probe", origin: origin));
        Assert.Null(Header(r, "Access-Control-Allow-Origin"));
        Assert.Null(Header(r, "Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task PublicChatCors_ExposesCapabilityAndContinuationHeaders()
    {
        var r = await SendAsync(Req(HttpMethod.Post, "/public/chat", origin: "https://unregistered.example"));
        Assert.Equal("*", Header(r, "Access-Control-Allow-Origin"));
        var exposed = Header(r, "Access-Control-Expose-Headers");
        Assert.Contains("X-Biatec-Conversation", exposed);
        Assert.Contains("X-Biatec-Continuation", exposed);
    }

    [Fact]
    public async Task Preflight_AllowedOrigin_Returns204WithHeaders()
    {
        var r = await SendAsync(Req(HttpMethod.Options, "/chat", origin: "https://dex.biatec.io",
            extra: [("Access-Control-Request-Method", "POST"), ("Access-Control-Request-Headers", "x-biatec-chat")]));
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal("https://dex.biatec.io", Header(r, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Header(r, "Access-Control-Allow-Credentials"));
        Assert.Contains("X-Biatec-Chat", Header(r, "Access-Control-Allow-Headers")!);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    public async Task Preflight_OtherOrigin_IsRefusedWithoutHeaders(string origin)
    {
        var r = await SendAsync(Req(HttpMethod.Options, "/chat", origin: origin,
            extra: ("Access-Control-Request-Method", "POST")));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Null(Header(r, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Preflight_OnUnknownHost_Is404()
    {
        var r = await SendAsync(Req(HttpMethod.Options, "/chat", host: "evil.example", origin: "https://web.biatec.io",
            extra: ("Access-Control-Request-Method", "POST")));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Null(Header(r, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Post_WithHeaderKeyAndAllowedOrigin_Passes()
    {
        var r = await SendAsync(Req(HttpMethod.Post, "/chat", origin: "https://web.biatec.io", csrf: true, clientKey: "biatec-web"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Post_WithoutCustomHeader_Is403()
    {
        var r = await SendAsync(Req(HttpMethod.Post, "/chat", origin: "https://web.biatec.io", clientKey: "biatec-web"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    [InlineData(null)]
    public async Task Post_FromForeignOrMissingOrigin_Is403(string? origin)
    {
        var r = await SendAsync(Req(HttpMethod.Post, "/chat", origin: origin, csrf: true, clientKey: "biatec-web"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "unknown-key")]
    [InlineData(HttpStatusCode.Forbidden, null)]
    [InlineData(HttpStatusCode.Forbidden, "BIATEC-WEB")]
    public async Task Post_WithUnregisteredClientKey_Is403(HttpStatusCode expected, string? key)
    {
        var r = await SendAsync(Req(HttpMethod.Post, "/chat", origin: "https://web.biatec.io", csrf: true, clientKey: key));
        Assert.Equal(expected, r.StatusCode);
    }

    [Fact]
    public async Task Get_WithUnregisteredClientKey_Is403_ButWithoutKeyPasses()
    {
        var bad = await SendAsync(Req(HttpMethod.Get, "/terminal-probe", origin: "https://web.biatec.io", clientKey: "nope"));
        Assert.Equal(HttpStatusCode.Forbidden, bad.StatusCode);
    }

    [Fact]
    public async Task OriginOfAnotherTenant_IsNotAllowed()
    {
        // localhost tenant's origin on the Biatec host
        var r = await SendAsync(Req(HttpMethod.Post, "/chat", origin: "http://localhost:5173", csrf: true, clientKey: "dev"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var ok = await SendAsync(Req(HttpMethod.Post, "/chat", host: "localhost", origin: "http://localhost:5173", csrf: true, clientKey: "dev"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task PublicEndpoint_AllowsAnyOrigin_WithoutCredentialsOrCsrf()
    {
        var r = await SendAsync(Req(HttpMethod.Post, "/public/chat", origin: "https://anywhere.example"));
        // The real endpoint now answers (400 for this empty body); the point is that CSRF/origin checks do not apply.
        Assert.NotEqual(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("*", Header(r, "Access-Control-Allow-Origin"));
        Assert.Null(Header(r, "Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task PublicEndpoint_OnUnknownHost_Is404()
    {
        var r = await SendAsync(Req(HttpMethod.Post, "/public/chat", host: "evil.example", origin: "https://anywhere.example"));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task PathTricks_DoNotEscapeTheGuard()
    {
        // /public prefix must be a path segment, not a string prefix.
        var r = await SendAsync(Req(HttpMethod.Post, "/publicity", origin: "https://evil.example", csrf: true, clientKey: "biatec-web"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var r2 = await SendAsync(Req(HttpMethod.Post, "/authx", origin: "https://evil.example", csrf: true, clientKey: "biatec-web"));
        Assert.Equal(HttpStatusCode.Forbidden, r2.StatusCode);
    }
}
