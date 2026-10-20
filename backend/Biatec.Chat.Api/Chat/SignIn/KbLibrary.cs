using Biatec.Chat.Core.Data;
using Biatec.Chat.Tools.Kb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Chat.SignIn;

/// <summary>Knowledge-base settings (section <c>Kb</c>).</summary>
public sealed class KbOptions
{
    public const string Section = "Kb";

    /// <summary>Seed directory of <c>*.md</c> pages (default: <c>kb/</c> next to the binary).</summary>
    public string? Path { get; set; }

    /// <summary>Largest uploaded page in UTF-8 bytes (default 16 KiB). The onboarding body limit (32 KiB) also applies.</summary>
    public int MaxPageBytes { get; set; } = 16 * 1024;

    /// <summary>Most uploaded pages kept in the database (default 200).</summary>
    public int MaxPages { get; set; } = 200;

    /// <summary>How often each replica checks the database for changed pages, in seconds (default 30; 0 turns polling off).</summary>
    public int RefreshSeconds { get; set; } = 30;

    public string ResolvedPath => string.IsNullOrWhiteSpace(Path) ? System.IO.Path.Combine(AppContext.BaseDirectory, "kb") : Path;
}

/// <summary>Outcome of a reload: pages now served, and the database error when the uploaded pages could not be read.</summary>
public sealed record KbReloadResult(int Served, string? Error = null)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Keeps this replica's <see cref="KbSearchService"/> in line with the shared store: the seed pages from
/// <c>Kb:Path</c> overlaid with the <c>KbPages</c> rows in PostgreSQL. A reload builds a new immutable page set and
/// swaps it in one step. If the database cannot be read, the last uploaded pages read stay (at start: none), still
/// overlaid on the current seed, and the reload reports the error instead of throwing.
/// </summary>
public sealed class KbLibrary(KbSearchService kb, IServiceScopeFactory scopes, IOptions<KbOptions> options, ILogger<KbLibrary> log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    // The seed the service was built from at start-up; replaced when a reload re-reads the directory.
    private IReadOnlyList<KbPage> _seed = kb.Pages;
    // The uploaded pages last read from the database.
    private IReadOnlyList<KbPage> _uploaded = [];
    private volatile string? _fingerprint;

    /// <summary>
    /// Re-reads the uploaded pages (and the seed directory when <paramref name="reseed"/>) and swaps them in. A
    /// database failure keeps the last uploaded pages and is returned in <see cref="KbReloadResult.Error"/>.
    /// </summary>
    public async Task<KbReloadResult> ReloadAsync(bool reseed, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (reseed) _seed = LoadSeed(options.Value.ResolvedPath, log);
            string? error = null;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
                var rows = await db.KbPages.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct);
                _uploaded = rows.Select(r => KbSearchService.Parse(r.Id, r.Body, KbPage.UploadSource)).ToList();
                _fingerprint = Fingerprint(rows.Select(r => (r.Id, r.Sha256)));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                error = Describe(ex);
                log.LogWarning("Kb: could not read uploaded pages, keeping the last ones read: {Error}", error);
            }
            kb.Replace(KbSearchService.Merge(_seed, _uploaded));
            log.LogInformation("Kb: serving {Count} pages ({Uploaded} uploaded)", kb.PageCount, _uploaded.Count);
            return new KbReloadResult(kb.PageCount, error);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reloads when the stored pages differ from the ones served (compares ids and hashes only, not bodies). Throws when the database cannot be read.</summary>
    public async Task<bool> RefreshIfChangedAsync(CancellationToken ct)
    {
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var keys = await db.KbPages.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.Sha256 }).ToListAsync(ct);
            if (Fingerprint(keys.Select(k => (k.Id, k.Sha256))) == _fingerprint) return false;
        }
        var r = await ReloadAsync(reseed: false, ct);
        if (!r.Ok) throw new InvalidOperationException(r.Error);
        return true;
    }

    /// <summary>The seed pages of a directory; a file name used twice is logged and only its first file is served.</summary>
    public static IReadOnlyList<KbPage> LoadSeed(string directory, ILogger log)
    {
        var pages = KbSearchService.LoadDirectory(directory, out var duplicates);
        foreach (var d in duplicates)
            log.LogWarning("Kb: seed file {File} is skipped: another seed file has the same name, and page ids are file names", d);
        return pages;
    }

    /// <summary>
    /// A short, secret-free diagnosis: the PostgreSQL SQL state and message (42P01: the table is missing, so the
    /// KbPages migration is not applied), else the exception type and message.
    /// </summary>
    public static string Describe(Exception ex)
    {
        var pg = ex as Npgsql.PostgresException ?? ex.InnerException as Npgsql.PostgresException;
        var text = pg is not null ? $"{pg.SqlState} {pg.MessageText}" : $"{ex.GetType().Name}: {ex.Message}";
        return Biatec.Chat.Api.Onboarding.OperatorAuth.Clean(text);
    }

    private static string Fingerprint(IEnumerable<(string Id, string Sha256)> rows) =>
        string.Join('\n', rows.Select(r => r.Id + ' ' + r.Sha256));
}

/// <summary>Loads the uploaded pages at start, then polls every <c>Kb:RefreshSeconds</c> so all replicas converge.</summary>
public sealed class KbRefreshService(KbLibrary library, IOptions<KbOptions> options, ILogger<KbRefreshService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var failing = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await library.RefreshIfChangedAsync(stoppingToken);
                failing = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Logged once per outage; the pages already served stay.
                if (!failing) log.LogWarning("Kb: could not read uploaded pages, keeping the current ones: {Error}", KbLibrary.Describe(ex));
                failing = true;
            }

            var seconds = options.Value.RefreshSeconds;
            if (seconds <= 0) return;
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
