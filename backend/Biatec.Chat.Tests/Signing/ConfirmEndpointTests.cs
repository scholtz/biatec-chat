using System.Net;
using System.Net.Http.Json;
using Biatec.Chat.Api.Tenancy;
using Biatec.Chat.Core.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Biatec.Chat.Tests.Signing;

/// <summary>The confirm route is guarded by the global CSRF middleware and needs a signed-in session (T-022).</summary>
public sealed class ConfirmEndpointTests : IDisposable
{
    private sealed class MemoryTenantStore(params Tenant[] tenants) : ITenantStore
    {
        public Task<Tenant?> FindByHostAsync(string host, CancellationToken ct) => Task.FromResult(tenants.FirstOrDefault(t => t.Host == host));
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
            ["Postgres:ConnectionString"] = "Host=127.0.0.1;Port=1;Database=none;Username=x;Password=x;Timeout=1",
            ["Algod:Networks:testnet:Url"] = "https://algod.example.test",
            ["Kms:KeyId"] = "alias/test",
            ["Evidence:AllowEphemeralKeys"] = "true",
        }));
        b.ConfigureServices(s =>
        {
            s.RemoveAll<ITenantStore>();
            s.AddSingleton<ITenantStore>(new MemoryTenantStore(TenantSeed.BiatecChat()));
        });
    });

    public void Dispose() => _factory.Dispose();

    private Task<HttpResponseMessage> PostAsync(bool csrf, string? cookie = null)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var r = new HttpRequestMessage(HttpMethod.Post, $"http://chat.biatec.io/chat/previews/{Guid.NewGuid()}/confirm")
        {
            Content = JsonContent.Create(new { groupHash = new string('a', 64) }),
        };
        if (csrf)
        {
            r.Headers.TryAddWithoutValidation("Origin", "https://web.biatec.io");
            r.Headers.TryAddWithoutValidation("X-Biatec-Chat", "1");
            r.Headers.TryAddWithoutValidation("X-Biatec-Client-Key", "biatec-web");
        }
        if (cookie is not null) r.Headers.TryAddWithoutValidation("Cookie", "__Host-biatec_session=" + cookie);
        return client.SendAsync(r);
    }

    [Fact]
    public async Task Confirm_WithoutTheCsrfHeaders_IsForbidden() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(csrf: false)).StatusCode);

    [Fact]
    public async Task Confirm_WithoutASession_IsUnauthorized() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(csrf: true)).StatusCode);
}
