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
        services.AddHealthChecks()
            .AddCheck<RedisHealthCheck>("redis")
            .AddCheck<PostgresHealthCheck>("postgres");
        return services;
    }
}

public sealed class RedisHealthCheck(IOptions<RedisOptions> options) : IHealthCheck, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConnectionMultiplexer? _mux;

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

    private async Task<ConnectionMultiplexer> GetAsync(CancellationToken ct)
    {
        if (_mux is not null) return _mux;
        await _gate.WaitAsync(ct);
        try
        {
            if (_mux is null)
            {
                var cfg = ConfigurationOptions.Parse(options.Value.ConnectionString);
                cfg.AbortOnConnectFail = false; // keep retrying in the background; health reflects current state
                cfg.ConnectTimeout = 2000;
                cfg.SyncTimeout = 2000;
                cfg.AsyncTimeout = 2000;
                _mux = await ConnectionMultiplexer.ConnectAsync(cfg).WaitAsync(TimeSpan.FromSeconds(3), ct);
            }
            return _mux;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_mux is not null) await _mux.DisposeAsync();
        _gate.Dispose();
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
