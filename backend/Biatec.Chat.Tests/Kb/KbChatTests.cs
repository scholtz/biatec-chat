using System.Net;
using System.Net.Http.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Biatec.Chat.Api.Data;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Api.Model;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Api.Sessions;
using Biatec.Chat.Api.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using ModelMessage = Amazon.BedrockRuntime.Model.Message;

namespace Biatec.Chat.Tests.Kb;

/// <summary>End to end through POST /chat/messages with the real anonymous executors. Needs docker-compose postgres; Bedrock is scripted.</summary>
public sealed class KbChatTests : IAsyncLifetime
{
    private sealed class MemoryTenantStore(params Biatec.Chat.Core.Data.Tenant[] tenants) : ITenantStore
    {
        public Task<Biatec.Chat.Core.Data.Tenant?> FindByHostAsync(string host, CancellationToken ct) => Task.FromResult(tenants.FirstOrDefault(t => t.Host == host));
    }

    private sealed class ScriptedModel(params Func<ModelRequest, IEnumerable<ModelStreamEvent>>[] scripts) : IModelClient
    {
        public readonly List<ModelRequest> Requests = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            lock (Requests) Requests.Add(request);
            foreach (var e in scripts[Math.Min(Requests.Count, scripts.Length) - 1](request))
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    private sealed class SpyExecutor(string name) : IToolExecutor
    {
        public int Calls;
        public string CapabilityName => name;
        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, System.Text.Json.Nodes.JsonNode arguments, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new ToolExecutionResult("should never run"));
        }
    }

    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private string Cs => new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;

    public async Task InitializeAsync()
    {
        await AdminAsync($"CREATE DATABASE \"{_dbName}\"");
        await using var db = new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(Cs).Options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");

    private static async Task AdminAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private WebApplicationFactory<Program> Factory(IModelClient model, params IToolExecutor[] extra) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Oidc:Issuer"] = "https://oidc.example.test",
                ["Oidc:McpResource"] = "https://mcp.example.test",
                ["Mcp:Endpoint"] = "https://mcp.example.test/mcp",
                ["Redis:ConnectionString"] = "127.0.0.1:1",
                ["Postgres:ConnectionString"] = Cs,
                ["Algod:Networks:testnet:Url"] = "https://algod.example.test",
                ["Kms:KeyId"] = "alias/test",
                ["Kb:Path"] = KbDir(),
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ITenantStore>();
                s.AddSingleton<ITenantStore>(new MemoryTenantStore(TenantSeed.BiatecChat()));
                s.RemoveAll<IModelClient>();
                s.AddSingleton(model);
                foreach (var e in extra) s.AddSingleton(e);
            });
        });

    private static string KbDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "kb"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "docs", "kb");
    }

    private static async Task<string> PostAsync(WebApplicationFactory<Program> f, string message)
    {
        using var client = f.CreateClient();
        var r = new HttpRequestMessage(HttpMethod.Post, "http://chat.biatec.io/chat/messages") { Content = JsonContent.Create(new { message }) };
        r.Headers.TryAddWithoutValidation("Origin", "https://web.biatec.io");
        r.Headers.TryAddWithoutValidation("X-Biatec-Chat", "1");
        r.Headers.TryAddWithoutValidation("X-Biatec-Client-Key", "biatec-web");
        var resp = await client.SendAsync(r);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadAsStringAsync();
    }

    private static IEnumerable<ModelStreamEvent> ToolCall(string id, string wireName, string input)
    {
        yield return new ModelToolUse(id, wireName, input);
        yield return new ModelMessageStop("tool_use", new ModelMessage
        {
            Role = ConversationRole.Assistant,
            Content = [new ContentBlock { ToolUse = new ToolUseBlock { ToolUseId = id, Name = wireName, Input = DocumentJson.FromJson(input) } }],
        });
    }

    private static IEnumerable<ModelStreamEvent> Text(string text)
    {
        yield return new ModelTextDelta(text);
        yield return new ModelMessageStop("end_turn", new ModelMessage { Role = ConversationRole.Assistant, Content = [new ContentBlock { Text = text }] });
    }

    [Fact]
    public async Task Kb_WhatIsBiatec_ToolResultCarriesKbContentForCitation()
    {
        var model = new ScriptedModel(
            _ => ToolCall("tu-1", "kb_search", """{"query":"What is Biatec?"}"""),
            _ => Text("Biatec builds self-custody finance on Algorand [what-is-biatec]."));
        using var f = Factory(model);

        var sse = await PostAsync(f, "What is Biatec?");
        Assert.Contains("Biatec builds self-custody finance", sse);

        var fed = model.Requests[1].Messages.SelectMany(m => m.Content).Where(c => c.ToolResult is not null)
            .SelectMany(c => c.ToolResult.Content).Select(c => c.Text).Single();
        Assert.StartsWith("<untrusted_data source=\"tool.kb.search\">", fed);
        Assert.Contains("[source: what-is-biatec]", fed);
        Assert.Contains("self-custody", fed);
    }

    [Fact]
    public async Task Kb_MoveFunds_YieldsSignInCard_AndNoWalletCall()
    {
        var wallet = new SpyExecutor("wallet.transfer");
        var model = new ScriptedModel(
            _ => ToolCall("tu-1", "wallet_transfer", """{"network":"testnet","from":"A","to":"B","amount":"1"}"""),
            _ => ToolCall("tu-2", "requestSignIn", """{"reason":"Moving funds needs a signed-in account."}"""),
            _ => Text("Please sign in to move funds."));
        using var f = Factory(model, wallet);

        var sse = await PostAsync(f, "Move 100 USDC to my friend");

        Assert.Equal(0, wallet.Calls);
        Assert.Contains("event: sign_in_card", sse);
        Assert.Contains("https://chat.biatec.io/auth/popup", sse);
        Assert.Contains("\"unavailable\":false", sse);
        // Anonymous requests never even list wallet tools.
        Assert.DoesNotContain(model.Requests[0].Tools, t => t.Name.StartsWith("wallet_"));
    }
}
