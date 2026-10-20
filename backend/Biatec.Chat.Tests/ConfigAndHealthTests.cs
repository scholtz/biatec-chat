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

    [Fact]
    public async Task HealthLive_IsHealthy_EvenWhenDependenciesAreDown()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Oidc:Issuer")]
    [InlineData("Oidc:McpResource")]
    [InlineData("Mcp:Endpoint")]
    [InlineData("Redis:ConnectionString")]
    [InlineData("Postgres:ConnectionString")]
    [InlineData("Algod:Networks:testnet:Url")]
    public void MissingRequiredOption_FailsStartup_NamingTheOption(string key)
    {
        var failures = StartupValidationFailures(c => c.Remove(key));

        Assert.Contains(failures, f => f.Contains(key.Split(':')[^1], StringComparison.OrdinalIgnoreCase)
                                       || f.Contains(key.Split(':')[0], StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Kms", null, "Kms:KeyId")]
    [InlineData("Secret", null, "Vault:MasterKey")]
    [InlineData("Secret", "c2hvcnQ=", "Vault:MasterKey")]
    [InlineData("Plain", null, "Vault:KeyProvider")]
    public void VaultKeyProvider_WithoutItsKey_FailsStartup(string provider, string? masterKey, string named)
    {
        var failures = StartupValidationFailures(c =>
        {
            c.Remove("Kms:KeyId");
            c["Vault:KeyProvider"] = provider;
            c["Vault:MasterKey"] = masterKey;
        });

        Assert.Contains(failures, f => f.Contains(named));
    }

    [Fact]
    public void SecretKeyProvider_WithMasterKeyAndNoKms_Starts()
    {
        using var factory = Factory(c =>
        {
            c.Remove("Kms:KeyId");
            c["Vault:KeyProvider"] = "Secret";
            c["Vault:MasterKey"] = Convert.ToBase64String(new byte[32]);
        });

        using var client = factory.CreateClient();
    }

    [Fact]
    public void NonEuCentralRegion_FailsStartup()
    {
        var failures = StartupValidationFailures(c => c["Bedrock:Region"] = "us-east-1");

        Assert.Contains(failures, f => f.Contains("eu-central-1"));
    }

    [Fact]
    public void NoAlgodNetwork_FailsStartup()
    {
        var failures = StartupValidationFailures(c => c.Remove("Algod:Networks:testnet:Url"));

        Assert.Contains(failures, f => f.Contains("Algod:Networks"));
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

    /// <summary>
    /// Starts the app with the mutated config and returns the options validation failures that stopped it. About one run
    /// in fifty a failed start surfaces an ObjectDisposedException from the host teardown (a race in the test host, not
    /// a product behaviour) instead of the validation error, so a start that fails without a validation error is
    /// retried; after three attempts the exceptions are shown in the failure.
    /// </summary>
    private static List<string> StartupValidationFailures(Action<Dictionary<string, string?>> mutate)
    {
        var seen = new List<Exception>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var factory = Factory(mutate);
            try
            {
                factory.CreateClient();
            }
            catch (Exception ex)
            {
                var failures = Flatten(ex).OfType<OptionsValidationException>().SelectMany(v => v.Failures).ToList();
                if (failures.Count > 0) return failures;
                seen.Add(ex);
                continue;
            }
            Assert.Fail("The app started although the configuration is invalid.");
        }
        throw new Xunit.Sdk.XunitException("Startup failed without an options validation error in 3 attempts:" + Environment.NewLine + string.Join(Environment.NewLine + "---" + Environment.NewLine, seen));
    }

    private static IEnumerable<Exception> Flatten(Exception? ex)
    {
        if (ex is null) yield break;
        yield return ex;
        var children = ex is AggregateException agg ? agg.InnerExceptions : (IReadOnlyCollection<Exception>)(ex.InnerException is null ? [] : [ex.InnerException]);
        foreach (var child in children)
            foreach (var e in Flatten(child)) yield return e;
    }
}
