using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Biatec.Chat.Tools.Dex.Swap;

public static class SwapServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <c>dex.swap</c> executor and its sidecar pool directory. Needs <c>AddBuildersClient</c> (for the
    /// sidecar settings) and the Biatec MCP client to be registered, plus a <see cref="TimeProvider"/>.
    /// </summary>
    public static IServiceCollection AddSwapExecutor(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SwapOptions>(configuration.GetSection(SwapOptions.Section));
        services.AddHttpClient<ISwapPoolDirectory, SidecarSwapPoolDirectory>(c => c.Timeout = TimeSpan.FromSeconds(15));
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(services, TimeProvider.System);
        services.AddScoped<SwapExecutor>();
        return services;
    }
}
