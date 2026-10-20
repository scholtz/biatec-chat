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

/// <summary>
/// Keeps this replica's <see cref="KbSearchService"/> in line with the shared store: the seed pages from
/// <c>Kb:Path</c> overlaid with the <c>KbPages</c> rows in PostgreSQL. A reload builds a new immutable page set and
/// swaps it in one step. If the database cannot be read, the pages already served stay (at start: the seed alone).
/// </summary>
public sealed class KbLibrary(KbSearchService kb, IServiceScopeFactory scopes, IOptions<KbOptions> options, ILogger<KbLibrary> log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    // The seed the service was built from at start-up; replaced when a reload re-reads the directory.
    private IReadOnlyList<KbPage> _seed = kb.Pages;
    private string? _fingerprint;

    /// <summary>
    /// Re-reads the uploaded pages (and the seed directory when <paramref name="reseed"/>) and swaps them in.
    /// Returns the number of pages now served.
    /// </summary>
    public async Task<int> ReloadAsync(bool reseed, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (reseed) _seed = KbSearchService.LoadDirectory(options.Value.ResolvedPath);
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var rows = await db.KbPages.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct);
            kb.Replace(KbSearchService.Merge(_seed, rows.Select(r => KbSearchService.Parse(r.Id, r.Body))));
            _fingerprint = Fingerprint(rows.Select(r => (r.Id, r.Sha256)));
            log.LogInformation("Kb: serving {Count} pages ({Uploaded} uploaded)", kb.PageCount, rows.Count);
            return kb.PageCount;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reloads when the stored pages differ from the ones served (compares ids and hashes only, not bodies).</summary>
    public async Task<bool> RefreshIfChangedAsync(CancellationToken ct)
    {
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var keys = await db.KbPages.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.Sha256 }).ToListAsync(ct);
            if (Fingerprint(keys.Select(k => (k.Id, k.Sha256))) == _fingerprint) return false;
        }
        await ReloadAsync(reseed: false, ct);
        return true;
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
                if (!failing) log.LogWarning("Kb: could not read uploaded pages, keeping the current ones: {Error}", ex.GetType().Name);
                failing = true;
            }

            var seconds = options.Value.RefreshSeconds;
            if (seconds <= 0) return;
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
