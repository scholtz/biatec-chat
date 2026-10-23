using Biatec.Chat.Api.Vault;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace Biatec.Chat.Tests.Vault;

/// <summary>BCHAT-012: the Dev vault key provider is refused outside Development unless explicitly allowed.</summary>
public sealed class VaultKeyProviderGateTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static IKeyProvider Resolve(string environment, Dictionary<string, string?> config)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new Env(environment));
        services.AddOptions<Biatec.Chat.Api.Config.RedisOptions>();
        services.AddOptions<Biatec.Chat.Api.Config.KmsOptions>();
        services.AddTokenVault(cfg);
        var sp = services.BuildServiceProvider();
        _ = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<VaultOptions>>().Value; // what ValidateOnStart does at host start
        return sp.GetRequiredService<IKeyProvider>();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void The_Dev_provider_outside_Development_is_refused_at_start_by_default(string environment)
    {
        var e = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => Resolve(environment, new() { ["Vault:KeyProvider"] = "Dev" }));
        Assert.Contains("only allowed in Development", e.Message);
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => Resolve(environment, []));
    }

    [Fact]
    public void The_Dev_provider_is_allowed_in_Development() =>
        Assert.IsType<DevKeyProvider>(Resolve("Development", new() { ["Vault:KeyProvider"] = "Dev" }));

    [Fact]
    public void A_malformed_DevKey_fails_at_start()
    {
        var e = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => Resolve("Development", new() { ["Vault:KeyProvider"] = "Dev", ["Vault:DevKey"] = "not-base64" }));
        Assert.Contains("DevKey", e.Message);
    }

    [Fact]
    public void The_Dev_provider_outside_Development_works_only_with_the_explicit_flag() =>
        Assert.IsType<DevKeyProvider>(Resolve("Production", new() { ["Vault:KeyProvider"] = "Dev", ["Vault:AllowEphemeralKeys"] = "true" }));

    // The real host: a Production host with the default Dev provider and no flag does not start (ValidateOnStart), instead of failing on a later request.
    [Fact]
    public async Task A_production_host_with_the_default_Dev_provider_does_not_start()
    {
        await using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Oidc:Issuer"] = "https://oidc.example.test",
                ["Oidc:McpResource"] = "https://mcp.example.test",
                ["Mcp:Endpoint"] = "https://mcp.example.test/mcp",
                ["Redis:ConnectionString"] = "127.0.0.1:1",
                ["Postgres:ConnectionString"] = "Host=127.0.0.1;Database=none;Username=x;Password=x",
                ["Algod:Networks:testnet:Url"] = "https://algod.example.test",
                ["Kms:KeyId"] = "alias/test",
                ["Evidence:AllowEphemeralKeys"] = "true",
                ["Vault:KeyProvider"] = "Dev",
                ["Vault:AllowEphemeralKeys"] = "false", // the test process sets the opt-in as an environment variable; this overrides it
            }));
        });
        var e = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Vault:KeyProvider=Dev is only allowed in Development", e.ToString());
    }

    [Fact]
    public void The_Secret_provider_is_unaffected_in_Production() =>
        Assert.IsType<SecretKeyProvider>(Resolve("Production", new() { ["Vault:KeyProvider"] = "Secret", ["Vault:MasterKey"] = Convert.ToBase64String(new byte[32]) }));
}
