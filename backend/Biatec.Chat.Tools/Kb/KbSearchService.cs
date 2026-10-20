using System.Text;
using System.Text.RegularExpressions;

namespace Biatec.Chat.Tools.Kb;

/// <summary>
/// One knowledge base page. <see cref="Id"/> is the file name without extension (or the uploaded id) and is what the
/// model cites. <see cref="Source"/> is "seed" (a <c>Kb:Path</c> file) or "upload" (the admin API).
/// </summary>
public sealed record KbPage(string Id, string Title, string Body, string Source = KbPage.SeedSource)
{
    public const string SeedSource = "seed";
    public const string UploadSource = "upload";
}

/// <summary>A ranked hit with a short excerpt around the best match.</summary>
public sealed record KbHit(string Id, string Title, string Excerpt, double Score);

/// <summary>
/// Simple ranked keyword search over knowledge base pages: the markdown files in a directory (docs/kb, the seed) plus
/// pages operators upload through the admin API (T-096). No vectors. The page set is an immutable snapshot that
/// <see cref="Replace"/> swaps atomically, so a search always sees one consistent set while a reload runs; a missing
/// directory yields an empty knowledge base rather than a failure. Page content is untrusted data for the model.
/// </summary>
public sealed partial class KbSearchService
{
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "a","an","the","is","are","was","were","of","to","in","on","for","and","or","what","how","do","does","i","me","my",
        "you","it","this","that","with","about","can","tell","please","at","be","by","from","as",
    };

    private volatile IReadOnlyList<KbPage> _pages;

    public KbSearchService(IEnumerable<KbPage> pages) => _pages = pages.ToList();

    public int PageCount => _pages.Count;

    /// <summary>The current snapshot (immutable; a later <see cref="Replace"/> does not change it).</summary>
    public IReadOnlyList<KbPage> Pages => _pages;

    /// <summary>Atomically swaps the served page set. Searches already running finish on the previous snapshot.</summary>
    public void Replace(IEnumerable<KbPage> pages) => _pages = pages.ToList().AsReadOnly();

    public static KbSearchService FromDirectory(string directory) => new(LoadDirectory(directory));

    /// <summary>The <c>*.md</c> pages of a directory (id = file name without extension), or none when it is missing.</summary>
    public static IReadOnlyList<KbPage> LoadDirectory(string directory) => LoadDirectory(directory, out _);

    /// <summary>
    /// As <see cref="LoadDirectory(string)"/>. Ids stay the file name, so two files with one name in different
    /// subdirectories collide: the first in ordinal path order is served and the others are returned in
    /// <paramref name="duplicates"/> (paths relative to the directory) for the caller to log.
    /// </summary>
    public static IReadOnlyList<KbPage> LoadDirectory(string directory, out IReadOnlyList<string> duplicates)
    {
        var pages = new List<KbPage>();
        var skipped = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (seen.Add(id)) pages.Add(Parse(id, File.ReadAllText(file, Encoding.UTF8)));
                else skipped.Add(Path.GetRelativePath(directory, file));
            }
        }
        duplicates = skipped;
        return pages;
    }

    /// <summary>A page from its text: the title is the first <c># heading</c>, else the id.</summary>
    public static KbPage Parse(string id, string text, string source = KbPage.SeedSource)
    {
        var title = TitleRegex().Match(text) is { Success: true } m ? m.Groups[1].Value.Trim() : id;
        return new KbPage(id, title, text, source);
    }

    /// <summary>Seed pages overlaid with uploaded ones: an uploaded page replaces the seed page with the same id.</summary>
    public static IReadOnlyList<KbPage> Merge(IEnumerable<KbPage> seed, IEnumerable<KbPage> uploaded)
    {
        var byId = new Dictionary<string, KbPage>(StringComparer.Ordinal);
        foreach (var p in seed) byId[p.Id] = p;
        foreach (var p in uploaded) byId[p.Id] = p;
        return byId.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<KbHit> Search(string query, int max = 3)
    {
        var terms = Tokenize(query).Where(t => !Stop.Contains(t)).Distinct().ToList();
        if (terms.Count == 0) return [];

        var pages = _pages; // one snapshot for the whole search
        var hits = new List<KbHit>();
        foreach (var page in pages)
        {
            var title = Tokenize(page.Title).ToList();
            var body = Tokenize(page.Body).ToList();
            double score = 0;
            foreach (var t in terms)
            {
                if (title.Contains(t)) score += 5;
                var n = body.Count(b => b == t);
                if (n > 0) score += 1 + Math.Log(n);
            }
            if (score > 0) hits.Add(new KbHit(page.Id, page.Title, Excerpt(page.Body, terms), score));
        }
        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Id, StringComparer.Ordinal).Take(max).ToList();
    }

    private static IEnumerable<string> Tokenize(string text) =>
        WordRegex().Matches(text.ToLowerInvariant()).Select(m => m.Value);

    private static string Excerpt(string body, List<string> terms)
    {
        // The paragraph that contains the most query terms.
        var paragraphs = body.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var content = paragraphs.Where(p => !p.StartsWith('#')).ToArray();
        var best = (content.Length > 0 ? content : paragraphs)
            .Select(p => (p, n: terms.Count(t => p.Contains(t, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(x => x.n).First().p;
        return best.Length <= 600 ? best : best[..600] + "...";
    }

    [GeneratedRegex(@"^#\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();
}
