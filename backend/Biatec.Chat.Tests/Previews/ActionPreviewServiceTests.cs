using Biatec.Chat.Api.Data;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Core.Previews;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Biatec.Chat.Tests.Previews;

/// <summary>Needs the docker-compose postgres. Each run migrates a fresh, uniquely named database.</summary>
public sealed class ActionPreviewServiceTests : IAsyncLifetime
{
    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    private Guid _conversation;

    private ChatDbContext NewContext()
    {
        var cs = new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;
        return new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(cs).Options);
    }

    private async Task AdminAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(AdminCs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InitializeAsync()
    {
        await AdminAsync($"CREATE DATABASE \"{_dbName}\"");
        await using var db = NewContext();
        await db.Database.MigrateAsync();
        _conversation = Guid.NewGuid();
        db.Conversations.Add(new Conversation { Id = _conversation, TenantId = "t1", CreatedAt = _clock.Now, LastMessageAt = _clock.Now });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");

    private NewStepSpec Step(string hash, int expiresInSeconds) =>
        new(ActionStepKind.Sign, hash, _clock.Now.AddSeconds(expiresInSeconds), UnsignedGroup: "[\"AAAA\"]", Network: "algorand-testnet", SenderAddress: "ADDR");

    private NewPreviewSpec Spec(params NewStepSpec[] steps) =>
        new(_conversation, "wallet.transfer", "{\"intent\":\"x\"}", steps, 12.5m, true);

    [Fact]
    public async Task Create_CarriesEarliestExpiry_AndCountdown()
    {
        await using var db = NewContext();
        var svc = new ActionPreviewService(db, _clock);
        var snap = await svc.CreateAsync(Spec(Step("h1", 2000), Step("h2", 1500)));

        Assert.Equal(_clock.Now.AddSeconds(1500), snap.Preview.ExpiresAt);
        Assert.Equal(1500, snap.Countdown.RemainingSeconds);
        Assert.True(snap.Countdown.CanConfirm);
        Assert.Equal(ActionPreviewStatus.Proposed, snap.Preview.Status);
        Assert.Equal([0, 1], snap.Steps.Select(s => s.Seq).ToArray());
    }

    [Fact]
    public async Task Confirm_IsRefusedUnder600Seconds_AndAllowedAtExactly600()
    {
        await using var db = NewContext();
        var svc = new ActionPreviewService(db, _clock);
        var snap = await svc.CreateAsync(Spec(Step("h1", 700)));

        _clock.Now = _clock.Now.AddSeconds(101); // 599 s left
        Assert.False((await svc.GetAsync(snap.Preview.Id)).Countdown.CanConfirm);
        var ex = await Assert.ThrowsAsync<PreviewException>(() => svc.ConfirmAsync(snap.Preview.Id, "h1"));
        Assert.Equal(PreviewError.TooCloseToExpiry, ex.Error);
        Assert.Equal(ActionPreviewStatus.Proposed, (await svc.GetAsync(snap.Preview.Id)).Preview.Status);

        _clock.Now = _clock.Now.AddSeconds(-1); // 600 s left
        var ok = await svc.ConfirmAsync(snap.Preview.Id, "h1");
        Assert.Equal(ActionPreviewStatus.Confirmed, ok.Preview.Status);
    }

    [Fact]
    public async Task Confirm_IsLimitedByTheSessionsAbsoluteLifetime()
    {
        await using var db = NewContext();
        var svc = new ActionPreviewService(db, _clock);
        var snap = await svc.CreateAsync(Spec(Step("h1", 2000)));
        var sessionEnds = _clock.Now.AddSeconds(300);

        Assert.False((await svc.GetAsync(snap.Preview.Id, sessionEnds)).Countdown.CanConfirm);
        var ex = await Assert.ThrowsAsync<PreviewException>(() => svc.ConfirmAsync(snap.Preview.Id, "h1", sessionEnds));
        Assert.Equal(PreviewError.TooCloseToExpiry, ex.Error);
    }

    [Fact]
    public async Task Confirm_RefusesAWrongHash()
    {
        await using var db = NewContext();
        var svc = new ActionPreviewService(db, _clock);
        var snap = await svc.CreateAsync(Spec(Step("h1", 2000)));
        var ex = await Assert.ThrowsAsync<PreviewException>(() => svc.ConfirmAsync(snap.Preview.Id, "other"));
        Assert.Equal(PreviewError.HashMismatch, ex.Error);
    }

    [Fact]
    public async Task Confirm_OnAnExpiredGroup_MarksItExpired_AndOnlyRebuildCanReplaceIt()
    {
        await using var db = NewContext();
        var svc = new ActionPreviewService(db, _clock);
        var snap = await svc.CreateAsync(Spec(Step("h1", 1000)));

        _clock.Now = _clock.Now.AddSeconds(1001);
        var ex = await Assert.ThrowsAsync<PreviewException>(() => svc.ConfirmAsync(snap.Preview.Id, "h1"));
        Assert.Equal(PreviewError.Expired, ex.Error);

        var read = await svc.GetAsync(snap.Preview.Id);
        Assert.Equal(ActionPreviewStatus.Expired, read.Preview.Status);
        Assert.All(read.Steps, s => Assert.Equal(StepStatus.Expired, s.Status));
        Assert.False(read.Countdown.CanConfirm);
        var again = await Assert.ThrowsAsync<PreviewException>(() => svc.ConfirmAsync(snap.Preview.Id, "h1"));
        Assert.Equal(PreviewError.Expired, again.Error);
    }

    [Fact]
    public async Task Rebuild_ProducesANewLinkedPreview_WithANewHash()
    {
        await using var db = NewContext();
        var svc = new ActionPreviewService(db, _clock);
        var old = await svc.CreateAsync(Spec(Step("h1", 1000)));
        _clock.Now = _clock.Now.AddSeconds(1001);

        var rebuilt = await svc.RebuildAsync(old.Preview.Id, "{\"intent\":\"x2\"}", [Step("h1-new", 2000)]);

        Assert.NotEqual(old.Preview.Id, rebuilt.Preview.Id);
        Assert.Equal("h1-new", Assert.Single(rebuilt.Steps).SubjectHash);
        Assert.Equal(ActionPreviewStatus.Proposed, rebuilt.Preview.Status);
        Assert.True(rebuilt.Countdown.CanConfirm);
        Assert.Equal("wallet.transfer", rebuilt.Preview.Capability);

        await using var read = NewContext();
        var oldRow = await read.ActionPreviews.SingleAsync(p => p.Id == old.Preview.Id);
        Assert.Equal(rebuilt.Preview.Id, oldRow.RebuiltAsPreviewId);
        Assert.Equal(ActionPreviewStatus.Expired, oldRow.Status);
    }

    [Fact]
    public async Task Rebuild_WithTheSameHash_Refused_AndSecondRebuildRefused()
    {
        await using var db = NewContext();
        var svc = new ActionPreviewService(db, _clock);
        var old = await svc.CreateAsync(Spec(Step("h1", 1000)));

        var same = await Assert.ThrowsAsync<PreviewException>(() => svc.RebuildAsync(old.Preview.Id, "{}", [Step("h1", 2000)]));
        Assert.Equal(PreviewError.RebuildReusedHash, same.Error);

        await svc.RebuildAsync(old.Preview.Id, "{}", [Step("h2", 2000)]);
        var twice = await Assert.ThrowsAsync<PreviewException>(() => svc.RebuildAsync(old.Preview.Id, "{}", [Step("h3", 2000)]));
        Assert.Equal(PreviewError.AlreadyRebuilt, twice.Error);
        Assert.Single(await NewContext().ActionPreviews.Where(p => p.RebuiltAsPreviewId == null && p.ConversationId == _conversation && p.Status == ActionPreviewStatus.Proposed).ToListAsync());
    }

    [Fact]
    public async Task MultiStep_LaterStepIsBuiltOnlyAfterTheEarlierOneLanded()
    {
        await using var db = NewContext();
        var svc = new ActionPreviewService(db, _clock);
        var first = await svc.CreateAsync(Spec(Step("s0", 1000)));

        var early = await Assert.ThrowsAsync<PreviewException>(() => svc.AddNextStepAsync(first.Preview.Id, Step("s1", 3000)));
        Assert.Equal(PreviewError.PreviousStepNotLanded, early.Error);

        await svc.MarkStepLandedAsync(first.Preview.Id, 0, ["TX0"]);
        _clock.Now = _clock.Now.AddSeconds(500);
        var second = await svc.AddNextStepAsync(first.Preview.Id, Step("s1", 3000));

        Assert.Equal(2, second.Steps.Count);
        Assert.Equal(StepStatus.Confirmed, second.Steps[0].Status);
        Assert.Equal(StepStatus.Pending, second.Steps[1].Status);
        Assert.Equal(_clock.Now.AddSeconds(3000), second.Preview.ExpiresAt); // fresh window for the new step
        Assert.Equal(3000, second.Countdown.RemainingSeconds);
    }

    [Fact]
    public async Task Rebuild_KeepsLandedStepsAsCompleted_AndRebuildsOnlyTheRest()
    {
        await using var db = NewContext();
        var svc = new ActionPreviewService(db, _clock);
        var p = await svc.CreateAsync(Spec(Step("s0", 5000)));
        await svc.MarkStepLandedAsync(p.Preview.Id, 0, ["TX0", "TX0b"]);
        await svc.AddNextStepAsync(p.Preview.Id, Step("s1", 1000));
        _clock.Now = _clock.Now.AddSeconds(1001);

        var rebuilt = await svc.RebuildAsync(p.Preview.Id, "{}", [Step("s1-new", 2000)]);

        Assert.Equal(2, rebuilt.Steps.Count);
        Assert.Equal("s0", rebuilt.Steps[0].SubjectHash);
        Assert.Equal(StepStatus.Confirmed, rebuilt.Steps[0].Status);
        Assert.Equal(["TX0", "TX0b"], rebuilt.Steps[0].TxIds);
        Assert.Equal("s1-new", rebuilt.Steps[1].SubjectHash);
        Assert.Equal(StepStatus.Pending, rebuilt.Steps[1].Status);
        Assert.Equal(_clock.Now.AddSeconds(2000), rebuilt.Preview.ExpiresAt);
        Assert.NotEqual(rebuilt.Steps[0].Id, (await svc.GetAsync(p.Preview.Id)).Steps[0].Id);
    }

    [Fact]
    public async Task Cancelled_OrCompleted_CannotBeRebuilt_AndUnknownIsNotFound()
    {
        await using var db = NewContext();
        var svc = new ActionPreviewService(db, _clock);
        var p = await svc.CreateAsync(Spec(Step("s0", 5000)));
        await db.ActionPreviews.Where(x => x.Id == p.Preview.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, ActionPreviewStatus.Cancelled));
        db.ChangeTracker.Clear();

        var ex = await Assert.ThrowsAsync<PreviewException>(() => svc.RebuildAsync(p.Preview.Id, "{}", [Step("n", 100)]));
        Assert.Equal(PreviewError.InvalidState, ex.Error);
        var nf = await Assert.ThrowsAsync<PreviewException>(() => svc.GetAsync(Guid.NewGuid()));
        Assert.Equal(PreviewError.NotFound, nf.Error);
    }
}
