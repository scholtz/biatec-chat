using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Tools.Mcp;

/// <summary>Logs the alert when a required Biatec MCP tool is missing. Replace it with a paging sink when one exists.</summary>
public sealed class LoggingMcpAlertSink(ILogger<LoggingMcpAlertSink> log) : IMcpAlertSink
{
    public void RequiredToolMissing(McpCallContext context, string toolName) =>
        log.LogCritical("ALERT: Biatec MCP tool {Tool} is missing for tenant {Tenant}, scopes {Scopes}.", toolName, context.TenantId, context.ScopeKey);
}

public static class McpRegistration
{
    /// <summary>
    /// Binds the "Mcp" section and registers the client, tool cache, connection factory and a logging alert sink.
    /// An <see cref="IMcpBearerSource"/> must be registered by the host (the API registers the vault-backed one).
    /// </summary>
    public static IServiceCollection AddBiatecMcp(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<McpOptions>().Bind(configuration.GetSection(McpOptions.Section));
        services.TryAddSingleton<McpToolCache>();
        services.TryAddSingleton<IMcpAlertSink, LoggingMcpAlertSink>();
        services.TryAddSingleton<IMcpConnectionFactory>(sp => new SdkMcpConnectionFactory(sp.GetRequiredService<IOptions<McpOptions>>().Value));
        services.TryAddSingleton<IBiatecMcpClient, BiatecMcpClient>();
        return services;
    }
}
