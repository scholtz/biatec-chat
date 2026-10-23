using Biatec.Chat.Api.Vault;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        return services.BuildServiceProvider().GetRequiredService<IKeyProvider>();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("")]
    public void The_Dev_provider_outside_Development_is_refused_by_default(string environment)
    {
        var e = Assert.Throws<InvalidOperationException>(() => Resolve(environment, new() { ["Vault:KeyProvider"] = "Dev" }));
        Assert.Contains("only allowed in Development", e.Message);
        Assert.Throws<InvalidOperationException>(() => Resolve(environment, []));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void The_Dev_provider_is_allowed_in_Development_and_Testing(string environment) =>
        Assert.IsType<DevKeyProvider>(Resolve(environment, new() { ["Vault:KeyProvider"] = "Dev" }));

    [Fact]
    public void The_Dev_provider_outside_Development_works_only_with_the_explicit_flag() =>
        Assert.IsType<DevKeyProvider>(Resolve("Production", new() { ["Vault:KeyProvider"] = "Dev", ["Vault:AllowEphemeralKeys"] = "true" }));

    [Fact]
    public void The_Secret_provider_is_unaffected_in_Production() =>
        Assert.IsType<SecretKeyProvider>(Resolve("Production", new() { ["Vault:KeyProvider"] = "Secret", ["Vault:MasterKey"] = Convert.ToBase64String(new byte[32]) }));
}
