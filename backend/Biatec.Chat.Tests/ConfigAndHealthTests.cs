using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Tests;

public class ConfigAndHealthTests
{
    private static Dictionary<string, string?> ValidConfig() => new()
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

    private static WebApplicationFactory<Program> Factory(Action<Dictionary<string, string?>>? mutate = null)
    {
        var cfg = ValidConfig();
        mutate?.Invoke(cfg);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(cfg));
        });
    }

    [Fact]
    public async Task Health_IsUnhealthy_WhenDependenciesAreDown()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Oidc:Issuer")]
    [InlineData("Oidc:McpResource")]
    [InlineData("Mcp:Endpoint")]
    [InlineData("Redis:ConnectionString")]
    [InlineData("Postgres:ConnectionString")]
    [InlineData("Algod:Networks:testnet:Url")]
    [InlineData("Kms:KeyId")]
    public void MissingRequiredOption_FailsStartup_NamingTheOption(string key)
    {
        using var factory = Factory(c => c.Remove(key));

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var validation = Flatten(ex).OfType<OptionsValidationException>().FirstOrDefault();
        Assert.NotNull(validation);
        Assert.Contains(validation!.Failures, f => f.Contains(key.Split(':')[^1], StringComparison.OrdinalIgnoreCase)
                                                    || f.Contains(key.Split(':')[0], StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NonEuCentralRegion_FailsStartup()
    {
        using var factory = Factory(c => c["Bedrock:Region"] = "us-east-1");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(Flatten(ex).OfType<OptionsValidationException>(),
            v => v.Failures.Any(f => f.Contains("eu-central-1")));
    }

    [Fact]
    public void NoAlgodNetwork_FailsStartup()
    {
        using var factory = Factory(c => c.Remove("Algod:Networks:testnet:Url"));

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(Flatten(ex).OfType<OptionsValidationException>(),
            v => v.Failures.Any(f => f.Contains("Algod:Networks")));
    }

    [Fact]
    public void CommittedAppsettings_ContainNoSecrets()
    {
        var dir = AppContext.BaseDirectory;
        foreach (var file in new[] { "appsettings.json" })
        {
            var text = File.ReadAllText(Path.Combine(dir, file));
            Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"Token\"", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Secret", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static IEnumerable<Exception> Flatten(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException) yield return ex;
    }
}
