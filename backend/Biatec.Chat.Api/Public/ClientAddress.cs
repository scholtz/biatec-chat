using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Biatec.Chat.Api.Public;

/// <summary>
/// Makes Connection.RemoteIpAddress the real client address behind an ingress. X-Forwarded-For is honoured ONLY from
/// the proxies listed in config (ForwardedHeaders:KnownProxies = IPs, ForwardedHeaders:KnownNetworks = CIDRs), and so is X-Forwarded-Proto;
/// with nothing configured only loopback is trusted, so a visitor cannot spoof the header to pick another bucket.
/// </summary>
public static class ClientAddress
{
    public static IServiceCollection AddClientAddress(IServiceCollection services, IConfiguration config)
    {
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            // The scheme too: behind the TLS-terminating ingress the request is plain http, and links built from it must say https.
            // Both headers are honoured only from the configured proxies; a visitor cannot set either.
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
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

    private const string OriginalSchemeKey = "Biatec.ClientAddress.OriginalScheme";

    public static IApplicationBuilder UseClientAddress(IApplicationBuilder app)
    {
        app.Use((ctx, next) => { ctx.Items[OriginalSchemeKey] = ctx.Request.Scheme; return next(ctx); });
        app.UseForwardedHeaders();
        // A forwarded scheme is applied only when it is http or https; anything else is dropped and the connection's own scheme stays.
        app.Use((ctx, next) =>
        {
            if (!(ctx.Request.Scheme is "http" or "https") && ctx.Items[OriginalSchemeKey] is string original) ctx.Request.Scheme = original;
            return next(ctx);
        });
        return app;
    }
}
