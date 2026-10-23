using Biatec.Chat.Api.Config;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;

namespace Biatec.Chat.Api.Health;

public static class HealthRegistration
{
    public static IServiceCollection AddBiatecHealth(this IServiceCollection services)
    {
        services.AddSingleton<RedisHealthCheck>();
        // Both checks sit behind the result cache (Limits:HealthCacheSeconds): /health is unauthenticated, so what it costs must not depend on its callers.
        services.AddHealthChecks()
            .Add(Security.RequestLimits.Cached("redis", sp => sp.GetRequiredService<RedisHealthCheck>()))
            .Add(Security.RequestLimits.Cached("postgres", sp => ActivatorUtilities.CreateInstance<PostgresHealthCheck>(sp)));
        return services;
    }
}

public sealed class RedisHealthCheck(IOptions<RedisOptions> options) : IHealthCheck, IAsyncDisposable
{
    private readonly object _gate = new();

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var mux = await GetAsync(cancellationToken);
            await mux.GetDatabase().PingAsync().WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Redis is unreachable.", ex);
        }
    }

    // The connect task is created once and reused, so a slow first connect is never abandoned and duplicated.
    private Task<ConnectionMultiplexer>? _connect;

    private Task<ConnectionMultiplexer> GetAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            _connect ??= ConnectAsync();
            return _connect.WaitAsync(TimeSpan.FromSeconds(3), ct);
        }
    }

    private Task<ConnectionMultiplexer> ConnectAsync()
    {
        var cfg = ConfigurationOptions.Parse(options.Value.ConnectionString);
        cfg.AbortOnConnectFail = false; // keep retrying in the background; health reflects current state
        cfg.ConnectTimeout = 2000;
        cfg.SyncTimeout = 2000;
        cfg.AsyncTimeout = 2000;
        return ConnectionMultiplexer.ConnectAsync(cfg);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connect is { IsCompletedSuccessfully: true }) await (await _connect).DisposeAsync();
    }
}

public sealed class PostgresHealthCheck(IOptions<PostgresOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var csb = new NpgsqlConnectionStringBuilder(options.Value.ConnectionString) { Timeout = 3, CommandTimeout = 3 };
            await using var conn = new NpgsqlConnection(csb.ConnectionString);
            await conn.OpenAsync(cancellationToken);
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is unreachable.", ex);
        }
    }
}
