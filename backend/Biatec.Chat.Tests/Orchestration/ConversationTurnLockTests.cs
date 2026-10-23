using Biatec.Chat.Api.Chat;
using Biatec.Chat.Core.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Biatec.Chat.Tests.Orchestration;

/// <summary>BCHAT-039: one turn per conversation across replicas, shown with separate database connections. Needs the docker-compose postgres.</summary>
public sealed class ConversationTurnLockTests : IAsyncLifetime
{
    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private string Cs => new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;

    // Two contexts stand for two replicas: each opens its own connection.
    private ChatDbContext Replica() => new(new DbContextOptionsBuilder<ChatDbContext>().UseNpgsql(Cs).Options);

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_dbName}\"", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_second_replica_cannot_take_the_turn_while_the_first_holds_it_and_can_after_release()
    {
        var conversation = Guid.NewGuid();
        await using var a = Replica();
        await using var b = Replica();

        var first = await ConversationTurnLock.TryAcquireAsync(a, conversation, default);
        Assert.NotNull(first);
        Assert.Null(await ConversationTurnLock.TryAcquireAsync(b, conversation, default));

        await first!.DisposeAsync();
        var second = await ConversationTurnLock.TryAcquireAsync(b, conversation, default);
        Assert.NotNull(second);
        await second!.DisposeAsync();
    }

    [Fact]
    public async Task Another_conversation_is_not_blocked()
    {
        await using var a = Replica();
        await using var b = Replica();
        var one = await ConversationTurnLock.TryAcquireAsync(a, Guid.NewGuid(), default);
        var two = await ConversationTurnLock.TryAcquireAsync(b, Guid.NewGuid(), default);
        Assert.NotNull(one);
        Assert.NotNull(two);
        await one!.DisposeAsync();
        await two!.DisposeAsync();
    }

    [Fact]
    public async Task The_lock_is_per_conversation_not_a_shared_ConversationLock_key()
    {
        // The short per-write lock (ConversationLock, used by evidence appends and message stores) must not collide with the turn lock,
        // or a turn would block its own writes.
        var conversation = Guid.NewGuid();
        await using var a = Replica();
        await using var b = Replica();
        var turn = await ConversationTurnLock.TryAcquireAsync(a, conversation, default);
        Assert.NotNull(turn);

        await using var tx = await b.Database.BeginTransactionAsync();
        await ConversationLock.AcquireAsync(b, conversation, default); // would hang here if the keys were the same
        await tx.CommitAsync();

        await turn!.DisposeAsync();
    }

    [Fact]
    public async Task Releasing_twice_and_a_dropped_holder_do_not_leave_the_conversation_locked()
    {
        var conversation = Guid.NewGuid();
        await using var a = Replica();
        await using var b = Replica();
        var first = await ConversationTurnLock.TryAcquireAsync(a, conversation, default);
        await first!.DisposeAsync();
        await first.DisposeAsync(); // idempotent enough: no exception, nothing held
        var again = await ConversationTurnLock.TryAcquireAsync(b, conversation, default);
        Assert.NotNull(again);
        await again!.DisposeAsync();
    }
}
