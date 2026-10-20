using Biatec.Chat.Api.Data;
using Biatec.Chat.Core.Data;
using Biatec.Chat.Tools.Caps;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Biatec.Chat.Tests.Caps;

/// <summary>Tenant soft cap and safe caps (T-025) against the docker-compose postgres.</summary>
public sealed class SpendCapsTests : IAsyncLifetime
{
    private static readonly string AdminCs = Environment.GetEnvironmentVariable("Postgres__AdminConnectionString")
        ?? "Host=127.0.0.1;Port=5432;Database=postgres;Username=biatec;Password=biatec-dev-only";

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly string _dbName = "biatec_test_" + Guid.NewGuid().ToString("N");
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly Tenant _tenant = Biatec.Chat.Api.Tenancy.TenantSeed.BiatecChat();
    private const string Sub = "pairwise-sub-1";

    private ChatDbContext NewContext()
    {
        var cs = new NpgsqlConnectionStringBuilder(AdminCs) { Database = _dbName, Pooling = false }.ConnectionString;
        return new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().ConfigureNpgsql(cs).Options);
    }

    private static async Task AdminAsync(string sql)
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
    }

    public async Task DisposeAsync() => await AdminAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");

    private async Task<CapDecision> ReserveAsync(decimal? value, string capability = "wallet.transfer", Guid? stepId = null, string sub = Sub)
    {
        await using var db = NewContext();
        return await new SpendCaps(db, _clock).ReserveAsync(new CapRequest(_tenant, sub, stepId ?? Guid.NewGuid(), capability, value));
    }

    private async Task WithCapsAsync(Func<SpendCaps, Task> f)
    {
        await using var db = NewContext();
        await f(new SpendCaps(db, _clock));
    }

    private async Task<List<SoftCapSpend>> SpendsAsync()
    {
        await using var db = NewContext();
        return await db.SoftCapSpends.AsNoTracking().ToListAsync();
    }

    private async Task<List<SafeApprovalCount>> SafesAsync()
    {
        await using var db = NewContext();
        return await db.SafeApprovalCounts.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task Caps_concurrent_reservations_cannot_exceed_the_cap()
    {
        // 20 parallel 400 EUR requests against 1500 EUR/h: at most three fit, the rest need phone consent.
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => ReserveAsync(400m))));

        Assert.Equal(3, results.Count(r => r.Reserved == CapKind.SoftCap && !r.RequireConsent));
        Assert.Equal(17, results.Count(r => r.RequireConsent && r.Reserved == CapKind.None && r.Reason == "soft_cap"));
        var rows = await SpendsAsync();
        Assert.Equal(3, rows.Count);
        Assert.True(rows.Sum(r => r.AmountEur) <= 1500m);
        Assert.All(rows, r => Assert.Equal((SoftCapStatus.Reserved, Sub, _tenant.Id, "predicted"), (r.Status, r.OidcSub, r.TenantId, r.ValueSource)));
    }

    [Fact]
    public async Task Caps_require_consent_while_the_window_total_plus_the_action_exceeds_the_cap_per_action_not_sticky()
    {
        Assert.False((await ReserveAsync(1000m)).RequireConsent);
        Assert.True((await ReserveAsync(600m)).RequireConsent);   // 1000 + 600 > 1500
        Assert.False((await ReserveAsync(500m)).RequireConsent);  // 1000 + 500 = 1500 fits: not a sticky flag
        Assert.True((await ReserveAsync(0.01m)).RequireConsent);  // the window is full
        // Phone-approved actions are never counted.
        Assert.Equal(1500m, (await SpendsAsync()).Sum(r => r.AmountEur));
    }

    [Fact]
    public async Task Caps_finalize_counts_what_oidc_valued_and_the_window_rolls_after_an_hour()
    {
        var step = Guid.NewGuid();
        await ReserveAsync(100m, stepId: step);
        await WithCapsAsync(c => c.FinalizeAsync(step, "1400.50"));
        var row = Assert.Single(await SpendsAsync());
        Assert.Equal((SoftCapStatus.Final, 1400.50m, "oidc", _clock.Now), (row.Status, row.AmountEur, row.ValueSource, row.SpentAt!.Value));

        Assert.True((await ReserveAsync(100m)).RequireConsent);
        _clock.Now = _clock.Now.AddMinutes(61);
        Assert.False((await ReserveAsync(100m)).RequireConsent);
    }

    [Fact]
    public async Task Caps_finalize_without_an_oidc_value_keeps_the_predicted_amount()
    {
        var step = Guid.NewGuid();
        await ReserveAsync(42m, stepId: step);
        await WithCapsAsync(c => c.FinalizeAsync(step, null));
        var row = Assert.Single(await SpendsAsync());
        Assert.Equal((SoftCapStatus.Final, 42m, "predicted"), (row.Status, row.AmountEur, row.ValueSource));
    }

    [Fact]
    public async Task Caps_release_frees_the_reservation_and_a_retry_of_the_same_step_does_not_double_count()
    {
        var step = Guid.NewGuid();
        await ReserveAsync(1000m, stepId: step);
        await ReserveAsync(1000m, stepId: step); // a re-confirm replaces the unfinished reservation
        Assert.Single(await SpendsAsync());
        await WithCapsAsync(c => c.ReleaseAsync(step));
        Assert.Empty(await SpendsAsync());
        Assert.False((await ReserveAsync(1500m)).RequireConsent);
    }

    [Fact]
    public async Task Caps_are_keyed_on_sub_and_tenant_and_safe_actions_are_not_counted_in_the_soft_cap()
    {
        Assert.False((await ReserveAsync(1500m)).RequireConsent);
        Assert.False((await ReserveAsync(1500m, sub: "someone-else")).RequireConsent);
        // Safe funds are not the user's: approvals add their value only to the safe cap.
        var step = Guid.NewGuid();
        var approve = await ReserveAsync(900m, SpendCaps.SafeApprove, step);
        Assert.Equal((false, CapKind.SafeApproval), (approve.RequireConsent, approve.Reserved));
        // The in-flight approval is a marked reservation that never adds to the soft cap.
        Assert.False((await ReserveAsync(0m)).RequireConsent);
        Assert.Equal(3000m, (await SpendsAsync()).Where(r => r.ValueSource != SpendCaps.SafeApproveReserved).Sum(r => r.AmountEur));
        await WithCapsAsync(c => c.FinalizeAsync(step, null));
        Assert.Equal(900m, Assert.Single(await SafesAsync()).ValueEur);
        Assert.DoesNotContain(await SpendsAsync(), r => r.Id == step);
    }

    [Fact]
    public async Task Caps_safe_approvals_are_capped_at_five_per_hour()
    {
        for (var i = 0; i < 5; i++)
        {
            // Signed approvals and in-flight ones both count.
            var step = Guid.NewGuid();
            Assert.False((await ReserveAsync(1m, SpendCaps.SafeApprove, step)).RequireConsent);
            if (i % 2 == 0) await WithCapsAsync(c => c.FinalizeAsync(step, null));
        }
        await WithCapsAsync(c => c.PruneAsync()); // in-flight ones whose step is gone are released...
        for (var i = 0; i < 2; i++) Assert.False((await ReserveAsync(1m, SpendCaps.SafeApprove)).RequireConsent); // ...so two fit again
        var sixth = await ReserveAsync(1m, SpendCaps.SafeApprove);
        Assert.Equal((true, "safe_approval_cap"), (sixth.RequireConsent, sixth.Reason));
        // Executions are their own kind.
        Assert.False((await ReserveAsync(0m, SpendCaps.SafeExecute)).RequireConsent);
        _clock.Now = _clock.Now.AddMinutes(61);
        Assert.False((await ReserveAsync(1m, SpendCaps.SafeApprove)).RequireConsent);
    }

    [Fact]
    public async Task Caps_safe_approvals_are_capped_at_2000_eur_per_hour()
    {
        Assert.False((await ReserveAsync(1500m, SpendCaps.SafeApprove)).RequireConsent);
        Assert.True((await ReserveAsync(600m, SpendCaps.SafeApprove)).RequireConsent);
        Assert.False((await ReserveAsync(500m, SpendCaps.SafeApprove)).RequireConsent);
    }

    [Fact]
    public async Task Caps_safe_executions_are_capped_at_ten_per_hour_and_tenant_caps_override_defaults()
    {
        for (var i = 0; i < 10; i++) Assert.False((await ReserveAsync(0m, SpendCaps.SafeExecute)).RequireConsent);
        var eleventh = await ReserveAsync(0m, SpendCaps.SafeExecute);
        Assert.Equal((true, "safe_execution_cap"), (eleventh.RequireConsent, eleventh.Reason));
        Assert.All(await SafesAsync(), r => Assert.Equal((SafeApprovalKind.Execute, 0m), (r.Kind, r.ValueEur)));

        _tenant.SafeApprovalCaps = """{"approvalsPerHour":1}""";
        Assert.False((await ReserveAsync(1m, SpendCaps.SafeApprove)).RequireConsent);
        Assert.True((await ReserveAsync(1m, SpendCaps.SafeApprove)).RequireConsent);
    }

    [Fact]
    public async Task Caps_concurrent_safe_approvals_cannot_exceed_the_count_cap()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => ReserveAsync(1m, SpendCaps.SafeApprove))));
        Assert.Equal(5, results.Count(r => !r.RequireConsent));
        Assert.Equal(5, (await SpendsAsync()).Count(r => r.Status == SoftCapStatus.Reserved && r.ValueSource == SpendCaps.SafeApproveReserved));
    }

    [Fact]
    public async Task Caps_safe_approval_value_accrues_from_the_oidc_valued_eur_when_signed()
    {
        // No predicted payload value: the valuedEur Biatec OIDC returns is what accrues toward 2000 EUR/h.
        var direct = Guid.NewGuid();
        await ReserveAsync(null, SpendCaps.SafeApprove, direct);
        await WithCapsAsync(c => c.FinalizeAsync(direct, "1200"));
        await WithCapsAsync(c => c.RecordConsentedAsync(new CapRequest(_tenant, Sub, Guid.NewGuid(), SpendCaps.SafeApprove, null), "700"));
        Assert.Equal(1900m, (await SafesAsync()).Sum(r => r.ValueEur));
        Assert.True((await ReserveAsync(200m, SpendCaps.SafeApprove)).RequireConsent);
        Assert.False((await ReserveAsync(100m, SpendCaps.SafeApprove)).RequireConsent);
    }

    [Fact]
    public async Task Caps_an_aborted_safe_reservation_is_released_and_revoke_removes_a_final_row()
    {
        var aborted = Guid.NewGuid();
        await ReserveAsync(1m, SpendCaps.SafeApprove, aborted);
        await WithCapsAsync(c => c.PruneAsync()); // its step is gone
        Assert.Empty(await SpendsAsync());

        var notMoved = Guid.NewGuid();
        await ReserveAsync(1m, SpendCaps.SafeApprove, notMoved);
        await WithCapsAsync(c => c.FinalizeAsync(notMoved, "1"));
        var spend = Guid.NewGuid();
        await ReserveAsync(10m, stepId: spend);
        await WithCapsAsync(c => c.FinalizeAsync(spend, "10"));
        await WithCapsAsync(c => c.RevokeAsync(notMoved));
        await WithCapsAsync(c => c.RevokeAsync(spend));
        Assert.Empty(await SafesAsync());
        Assert.Empty(await SpendsAsync());
    }

    [Fact]
    public async Task Caps_a_consented_safe_action_is_still_recorded_but_a_consented_spend_is_not()
    {
        var step = Guid.NewGuid();
        await ReserveAsync(10m, SpendCaps.SafeApprove, step);
        await WithCapsAsync(c => c.ReleaseAsync(step)); // step-up
        await WithCapsAsync(c => c.RecordConsentedAsync(new CapRequest(_tenant, Sub, step, SpendCaps.SafeApprove, 10m), null));
        await WithCapsAsync(c => c.RecordConsentedAsync(new CapRequest(_tenant, Sub, step, SpendCaps.SafeApprove, 10m), null)); // idempotent
        await WithCapsAsync(c => c.RecordConsentedAsync(new CapRequest(_tenant, Sub, Guid.NewGuid(), "wallet.transfer", 500m), "500"));
        Assert.Single(await SafesAsync());
        Assert.Empty(await SpendsAsync());
    }

    [Fact]
    public async Task Caps_prune_releases_reservations_of_expired_steps_and_drops_rows_older_than_window_plus_one_hour()
    {
        // A reservation with no live step (expired or gone) is released; one for a live step stays.
        var live = Guid.NewGuid();
        await using (var db = NewContext())
        {
            var conv = new Conversation { Id = Guid.NewGuid(), TenantId = _tenant.Id, CreatedAt = _clock.Now, LastMessageAt = _clock.Now };
            var preview = new ActionPreview { Id = Guid.NewGuid(), ConversationId = conv.Id, Capability = "wallet.transfer", CanonicalJson = "{}", ExpiresAt = _clock.Now.AddMinutes(20) };
            db.Conversations.Add(conv);
            db.ActionPreviews.Add(preview);
            db.ActionSteps.Add(new ActionStep { Id = live, PreviewId = preview.Id, SubjectHash = "aa", ExpiresAt = _clock.Now.AddMinutes(20) });
            await db.SaveChangesAsync();
        }
        await ReserveAsync(100m, stepId: live);
        await ReserveAsync(100m); // no step
        var finalStep = Guid.NewGuid();
        await ReserveAsync(100m, stepId: finalStep);
        await WithCapsAsync(c => c.FinalizeAsync(finalStep, "100"));
        var safeStep = Guid.NewGuid();
        await ReserveAsync(1m, SpendCaps.SafeApprove, safeStep);
        await WithCapsAsync(c => c.FinalizeAsync(safeStep, null));
        await ReserveAsync(1m, SpendCaps.SafeApprove); // in flight, no step: released

        await WithCapsAsync(c => c.PruneAsync());
        Assert.Equal(new[] { live, finalStep }.Order(), (await SpendsAsync()).Select(r => r.Id).Order());
        Assert.Single(await SafesAsync());

        _clock.Now = _clock.Now.AddHours(2).AddMinutes(1);
        await WithCapsAsync(c => c.PruneAsync());
        Assert.Empty(await SpendsAsync());
        Assert.Empty(await SafesAsync());
    }

    [Fact]
    public void Caps_tenant_save_enforces_the_floor_and_the_operator_only_raise()
    {
        Tenant T(decimal cap, bool anchored = false) => new() { ConsentThresholdCeilingEur = 1000m, SoftCapEurPerHour = cap, TrustAnchorVerified = anchored };
        Assert.Null(SoftCapPolicy.ValidateOnSave(T(1500m), byOperator: false));
        Assert.Null(SoftCapPolicy.ValidateOnSave(T(1000m), byOperator: false));            // lowered to the floor
        Assert.NotNull(SoftCapPolicy.ValidateOnSave(T(999m), byOperator: false));          // below the floor
        Assert.NotNull(SoftCapPolicy.ValidateOnSave(T(5000m), byOperator: false));         // self-registered loosening
        Assert.NotNull(SoftCapPolicy.ValidateOnSave(T(5000m), byOperator: true));          // operator, but no trust anchor
        Assert.Null(SoftCapPolicy.ValidateOnSave(T(5000m, anchored: true), byOperator: true));

        // At signing time a stored loosening without a trust anchor is clamped to the platform default.
        Assert.Equal(1500m, SoftCapPolicy.EffectiveCapEur(T(5000m)));
        Assert.Equal(5000m, SoftCapPolicy.EffectiveCapEur(T(5000m, anchored: true)));
        Assert.Equal(1200m, SoftCapPolicy.EffectiveCapEur(T(1200m)));
        Assert.Equal(1500m, SoftCapPolicy.EffectiveCapEur(T(0m)));
    }

    [Fact]
    public void Caps_safe_caps_parse_with_defaults()
    {
        Assert.Equal(SafeCaps.Default, SafeCaps.Parse("{}"));
        Assert.Equal(SafeCaps.Default, SafeCaps.Parse("not json"));
        Assert.Equal(new SafeCaps(3, 100m, 4), SafeCaps.Parse("""{"approvalsPerHour":3,"approvalEurPerHour":100,"executionsPerHour":4}"""));
    }
}
