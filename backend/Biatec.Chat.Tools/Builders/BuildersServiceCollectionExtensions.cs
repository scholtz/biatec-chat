using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Biatec.Chat.Tools.Builders;

public static class BuildersServiceCollectionExtensions
{
    /// <summary>Largest sidecar response the backend buffers (a group of up to 16 transactions plus facts is tens of KiB; a pool listing a few hundred). A larger one is refused as a <see cref="BuilderException"/>.</summary>
    public const long MaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>Configuration section: <c>Builders:BaseAddress</c>, <c>Builders:InternalSecret</c>, <c>Builders:TimeoutSeconds</c> (env <c>Builders__*</c>).</summary>
    public const string Section = "Builders";
    public const int DefaultTimeoutSeconds = 15;

    /// <summary>
    /// Registers <see cref="IBuildersClient"/> with a short timeout. Throws at startup when the base address or secret is
    /// missing: there is deliberately no default secret. Supply it from the environment or a secret store, never committed config.
    /// </summary>
    public static IServiceCollection AddBuildersClient(this IServiceCollection services, IConfiguration configuration)
    {
        var s = configuration.GetSection(Section);
        if (!Uri.TryCreate(s["BaseAddress"], UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Builders:BaseAddress (env Builders__BaseAddress) must be an absolute URL.");
        var secret = s["InternalSecret"];
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("Builders:InternalSecret (env Builders__InternalSecret) is required.");
        var timeout = int.TryParse(s["TimeoutSeconds"], out var t) && t > 0 ? t : DefaultTimeoutSeconds;
        services.AddSingleton(new BuildersOptions { BaseAddress = uri, InternalSecret = secret });
        services.AddHttpClient<IBuildersClient, BuildersClient>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(timeout);
            // A sidecar (or anything impersonating it) must not make the backend buffer an unbounded response.
            c.MaxResponseContentBufferSize = MaxResponseBytes;
        });
        return services;
    }
}
