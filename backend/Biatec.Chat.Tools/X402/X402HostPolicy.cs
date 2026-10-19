using System.Net;
using System.Net.Sockets;

namespace Biatec.Chat.Tools.X402;

public interface IHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed class DnsHostResolver : IHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}

public sealed class X402PolicyException(string code, string message) : Exception(message)
{
    /// <summary>Machine-readable reason, safe to show.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// SSRF policy for the resource URLs the model can ask the backend to fetch. The URL is untrusted data. It must be https
/// with no credentials, on an allowed port and an allowlisted host name (never an IP literal), and every address the host
/// resolves to must be public. The same address check runs again when the socket connects, which defeats DNS rebinding.
/// </summary>
public sealed class X402HostPolicy(X402Options options, IHostResolver resolver)
{
    /// <summary>Checks everything that needs no DNS. Returns the normalised URI.</summary>
    public Uri CheckSyntax(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 || url.Any(char.IsControl) || url.Any(char.IsWhiteSpace))
            throw new X402PolicyException("invalid_url", "The resource URL is not valid.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new X402PolicyException("not_https", "The resource URL must be https.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new X402PolicyException("credentials_in_url", "The resource URL must not contain credentials.");
        if (!string.IsNullOrEmpty(uri.Fragment))
            throw new X402PolicyException("invalid_url", "The resource URL must not contain a fragment.");
        if (!options.AllowedPorts.Contains(uri.Port))
            throw new X402PolicyException("port_not_allowed", "The resource URL uses a port that is not allowed.");
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (uri.HostNameType != UriHostNameType.Dns || IPAddress.TryParse(host.Trim('[', ']'), out _) || !host.Contains('.'))
            throw new X402PolicyException("host_not_allowed", "The resource host must be a public DNS name.");
        if (host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".internal", StringComparison.Ordinal)
            || host.EndsWith(".localhost", StringComparison.Ordinal))
            throw new X402PolicyException("host_not_allowed", "The resource host is not public.");
        if (!HostAllowed(host))
            throw new X402PolicyException("host_not_allowed", "The resource host is not on the allowed list.");
        return uri;
    }

    /// <summary>Syntax checks plus DNS: every resolved address must be public.</summary>
    public async Task<Uri> CheckAsync(string? url, CancellationToken ct)
    {
        var uri = CheckSyntax(url);
        await RequirePublicAsync(uri.IdnHost, ct);
        return uri;
    }

    /// <summary>Resolves the host and returns its addresses, throwing unless all of them are public.</summary>
    public async Task<IPAddress[]> RequirePublicAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses;
        try { addresses = await resolver.ResolveAsync(host, ct); }
        catch (SocketException) { throw new X402PolicyException("dns_failed", "The resource host could not be resolved."); }
        if (addresses.Length == 0) throw new X402PolicyException("dns_failed", "The resource host could not be resolved.");
        if (addresses.Any(a => !IsPublic(a)))
            throw new X402PolicyException("host_not_public", "The resource host resolves to a non-public address.");
        return addresses;
    }

    private bool HostAllowed(string host)
    {
        foreach (var raw in options.AllowedHosts)
        {
            var pattern = raw.Trim().ToLowerInvariant();
            if (pattern.Length == 0) continue;
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                // "*.example.com" matches shop.example.com but not example.com or evilexample.com.
                if (host.EndsWith(pattern[1..], StringComparison.Ordinal) && host.Length > pattern.Length - 1) return true;
            }
            else if (host == pattern) return true;
        }
        return false;
    }

    /// <summary>True only for globally routable unicast addresses.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None))
            return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 0 || b[0] == 10 || b[0] >= 224
                || (b[0] == 100 && b[1] is >= 64 and <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 192 && b[1] == 0 && b[2] == 2)
                || (b[0] == 198 && b[1] is 18 or 19)
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                || (b[0] == 203 && b[1] == 0 && b[2] == 113));
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal) return false;
            // NAT64, documentation, 6to4 and the discard prefix are not public destinations.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b) return false;
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false;
            if (b[0] == 0x20 && b[1] == 0x02) return false;
            if (b[0] == 0x01 && b[1] == 0x00 && b[2..8].All(x => x == 0)) return false;
            return true;
        }
        return false;
    }
}
