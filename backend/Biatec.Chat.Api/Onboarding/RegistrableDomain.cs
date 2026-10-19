namespace Biatec.Chat.Api.Onboarding;

/// <summary>
/// Registrable domain (eTLD+1) for the same-site rule. This is a conservative built-in subset of the Public Suffix
/// List: common multi-label public suffixes are listed, everything else is treated as a single-label TLD. A wrong
/// guess can only make two names look same-site; every origin still needs its own DNS proof.
/// </summary>
public static class RegistrableDomain
{
    private static readonly HashSet<string> MultiLabelSuffixes = new(StringComparer.Ordinal)
    {
        "co.uk", "org.uk", "ac.uk", "gov.uk", "me.uk", "ltd.uk", "plc.uk",
        "com.au", "net.au", "org.au", "edu.au", "gov.au", "co.nz", "org.nz", "net.nz",
        "co.jp", "ne.jp", "or.jp", "ac.jp", "co.kr", "or.kr", "com.cn", "net.cn", "org.cn",
        "com.br", "net.br", "org.br", "com.mx", "com.ar", "com.tr", "com.sg", "com.hk", "com.tw",
        "co.in", "net.in", "org.in", "co.za", "org.za", "co.il", "com.ua", "com.pl",
        "github.io", "gitlab.io", "herokuapp.com", "netlify.app", "vercel.app", "pages.dev", "workers.dev",
        "azurewebsites.net", "cloudfront.net", "appspot.com", "web.app", "firebaseapp.com", "blogspot.com",
    };

    public static string Of(string host)
    {
        var labels = host.Split('.');
        if (labels.Length <= 2) return host;
        var last2 = string.Join('.', labels[^2..]);
        var take = MultiLabelSuffixes.Contains(last2) ? 3 : 2;
        return take >= labels.Length ? host : string.Join('.', labels[^take..]);
    }

    public static bool SameSite(string a, string b) => string.Equals(Of(a), Of(b), StringComparison.Ordinal);
}
