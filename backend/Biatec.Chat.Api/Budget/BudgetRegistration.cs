using Biatec.Chat.Api.Model;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Biatec.Chat.Api.Budget;

public static class BudgetRegistration
{
    /// <summary>
    /// Wraps the registered <see cref="IModelClient"/> in <see cref="BudgetedModelClient"/>. Call it after
    /// AddBedrockModel; the Redis multiplexer comes from AddTokenVault.
    /// </summary>
    public static IServiceCollection AddBedrockBudget(this IServiceCollection services)
    {
        services.AddOptions<BudgetOptions>()
            .BindConfiguration(BudgetOptions.Section)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IBudgetTenantSource, HttpBudgetTenantSource>();
        services.TryAddSingleton<IBudgetLedger>(sp => new RedisBudgetLedger(sp.GetRequiredService<IConnectionMultiplexer>(), sp.GetRequiredService<IOptions<BudgetOptions>>()));
        services.TryAddSingleton<IAnswerCache>(sp => new RedisAnswerCache(sp.GetRequiredService<IConnectionMultiplexer>(), sp.GetRequiredService<IOptions<BudgetOptions>>()));

        var inner = services.LastOrDefault(d => d.ServiceType == typeof(IModelClient))
            ?? throw new InvalidOperationException("AddBedrockBudget needs an IModelClient; call AddBedrockModel first.");
        services.Remove(inner);
        services.Add(new ServiceDescriptor(typeof(IModelClient), sp => new BudgetedModelClient(
            Resolve(sp, inner),
            sp.GetRequiredService<IBudgetLedger>(),
            sp.GetRequiredService<IAnswerCache>(),
            sp.GetRequiredService<IBudgetTenantSource>(),
            sp.GetRequiredService<IOptions<BudgetOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<BudgetedModelClient>>(),
            // Registered by AddBiatecTelemetry only when telemetry is on.
            sp.GetService<Biatec.Chat.Api.Telemetry.ChatMetrics>()), inner.Lifetime));
        return services;
    }

    private static IModelClient Resolve(IServiceProvider sp, ServiceDescriptor d) => (IModelClient)(
        d.ImplementationInstance
        ?? d.ImplementationFactory?.Invoke(sp)
        ?? ActivatorUtilities.CreateInstance(sp, d.ImplementationType!));
}
