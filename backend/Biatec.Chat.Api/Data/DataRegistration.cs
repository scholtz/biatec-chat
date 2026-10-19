using Biatec.Chat.Api.Config;
using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Data;

public static class DataRegistration
{
    public const string MigrationsAssembly = "Biatec.Chat.Api";

    public static DbContextOptionsBuilder<ChatDbContext> ConfigureNpgsql(this DbContextOptionsBuilder<ChatDbContext> o, string connectionString)
    {
        o.UseNpgsql(connectionString, n => n.MigrationsAssembly(MigrationsAssembly));
        return o;
    }

    public static IServiceCollection AddBiatecData(this IServiceCollection services)
    {
        services.AddDbContext<ChatDbContext>((sp, o) =>
            o.UseNpgsql(sp.GetRequiredService<IOptions<PostgresOptions>>().Value.ConnectionString,
                n => n.MigrationsAssembly(MigrationsAssembly)));
        return services;
    }

    /// <summary>Applies pending migrations. Development only; other environments migrate as a deployment step.</summary>
    public static async Task MigrateDevelopmentDatabaseAsync(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ChatDbContext>().Database.MigrateAsync();
    }
}

/// <summary>Used by `dotnet ef`. Reads POSTGRES__CONNECTIONSTRING, falling back to the docker-compose dev database.</summary>
public sealed class ChatDbContextFactory : IDesignTimeDbContextFactory<ChatDbContext>
{
    public ChatDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("Postgres__ConnectionString")
                 ?? "Host=127.0.0.1;Port=5432;Database=biatec_chat;Username=biatec;Password=biatec-dev-only";
        return new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(cs).Options);
    }
}
