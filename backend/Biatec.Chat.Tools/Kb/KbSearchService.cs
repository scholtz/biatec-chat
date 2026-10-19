using System.Text;
using System.Text.RegularExpressions;

namespace Biatec.Chat.Tools.Kb;

/// <summary>One knowledge base page. <see cref="Id"/> is the file name without extension and is what the model cites.</summary>
public sealed record KbPage(string Id, string Title, string Body);

/// <summary>A ranked hit with a short excerpt around the best match.</summary>
public sealed record KbHit(string Id, string Title, string Excerpt, double Score);

/// <summary>
/// Simple ranked keyword search over the markdown files in a directory (docs/kb). No vectors. The pages are loaded
/// once; a missing directory yields an empty knowledge base rather than a failure.
/// </summary>
public sealed partial class KbSearchService
{
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "a","an","the","is","are","was","were","of","to","in","on","for","and","or","what","how","do","does","i","me","my",
        "you","it","this","that","with","about","can","tell","please","at","be","by","from","as",
    };

    private readonly IReadOnlyList<KbPage> _pages;

    public KbSearchService(IEnumerable<KbPage> pages) => _pages = pages.ToList();

    public int PageCount => _pages.Count;

    public static KbSearchService FromDirectory(string directory)
    {
        var pages = new List<KbPage>();
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var text = File.ReadAllText(file, Encoding.UTF8);
                var id = Path.GetFileNameWithoutExtension(file);
                var title = TitleRegex().Match(text) is { Success: true } m ? m.Groups[1].Value.Trim() : id;
                pages.Add(new KbPage(id, title, text));
            }
        }
        return new KbSearchService(pages);
    }

    public IReadOnlyList<KbHit> Search(string query, int max = 3)
    {
        var terms = Tokenize(query).Where(t => !Stop.Contains(t)).Distinct().ToList();
        if (terms.Count == 0) return [];

        var hits = new List<KbHit>();
        foreach (var page in _pages)
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
