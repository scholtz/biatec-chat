using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Biatec.Chat.Api.Public;

/// <summary>
/// Makes Connection.RemoteIpAddress the real client address behind an ingress. X-Forwarded-For is honoured ONLY from
/// the proxies listed in config (ForwardedHeaders:KnownProxies = IPs, ForwardedHeaders:KnownNetworks = CIDRs);
/// with nothing configured only loopback is trusted, so a visitor cannot spoof the header to pick another bucket.
/// </summary>
public static class ClientAddress
{
    public static IServiceCollection AddClientAddress(IServiceCollection services, IConfiguration config)
    {
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            o.ForwardLimit = 1;
            var proxies = config.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
            var networks = config.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [];
            if (proxies.Length == 0 && networks.Length == 0) return;
            o.KnownProxies.Clear();
            o.KnownIPNetworks.Clear();
            foreach (var p in proxies) o.KnownProxies.Add(IPAddress.Parse(p));
            foreach (var n in networks) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(n));
        });
        return services;
    }

    public static IApplicationBuilder UseClientAddress(IApplicationBuilder app) => app.UseForwardedHeaders();
}
