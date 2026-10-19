using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Config;

public static class ConfigurationExtensions
{
    /// <summary>
    /// Binds every options section and validates it when the host starts, so a missing or invalid
    /// value stops the app with a message naming the option.
    /// </summary>
    public static IServiceCollection AddBiatecOptions(this IServiceCollection services, IConfiguration config)
    {
        AddValidated<OidcOptions>(services, config, OidcOptions.Section);
        AddValidated<McpOptions>(services, config, McpOptions.Section);
        AddValidated<BedrockOptions>(services, config, BedrockOptions.Section);
        AddValidated<RedisOptions>(services, config, RedisOptions.Section);
        AddValidated<PostgresOptions>(services, config, PostgresOptions.Section);
        AddValidated<KmsOptions>(services, config, KmsOptions.Section);
        AddValidated<AlgodOptions>(services, config, AlgodOptions.Section);
        services.AddSingleton<IValidateOptions<AlgodOptions>, AlgodOptionsValidator>();
        return services;
    }

    private static void AddValidated<T>(IServiceCollection services, IConfiguration config, string section)
        where T : class
        => services.AddOptions<T>()
            .Bind(config.GetSection(section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
}

/// <summary>Algod needs at least one network, and each network entry must be valid.</summary>
public sealed class AlgodOptionsValidator : IValidateOptions<AlgodOptions>
{
    public ValidateOptionsResult Validate(string? name, AlgodOptions options)
    {
        if (options.Networks.Count == 0)
            return ValidateOptionsResult.Fail("Algod:Networks must define at least one network (for example Algod:Networks:testnet:Url).");

        var failures = new List<string>();
        foreach (var (network, entry) in options.Networks)
        {
            if (entry is null)
            {
                failures.Add($"Algod:Networks:{network} is empty.");
                continue;
            }
            var results = new List<ValidationResult>();
            Validator.TryValidateObject(entry, new ValidationContext(entry), results, validateAllProperties: true);
            failures.AddRange(results.Select(r => $"Algod:Networks:{network}: {r.ErrorMessage}"));
        }
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
