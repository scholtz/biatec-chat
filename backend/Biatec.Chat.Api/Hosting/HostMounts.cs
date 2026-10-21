using System.Text.RegularExpressions;
using Biatec.Chat.Api.Tenancy;
using Microsoft.Extensions.Options;

namespace Biatec.Chat.Api.Hosting;

/// <summary>
/// One host that serves this backend under a path prefix of its own site (<c>https://beta.dex.biatec.io/chat/...</c>).
/// The prefix is configuration, never a request value: a header such as X-Forwarded-Prefix is not read.
/// </summary>
public sealed class HostMountOptions
{
    /// <summary>Lower-case DNS name as it arrives in the Host header, without scheme or port.</summary>
    public string Host { get; set; } = "";

    /// <summary>Path the ingress forwards unchanged, for example <c>/chat</c>: one or more segments, no trailing slash.</summary>
    public string Prefix { get; set; } = "";

    // The overlay's ConfigMap also carries Hosting__Mounts__N__ClientKey, the client key of the page on that host. Only the
    // deploy reads it (to provision the mounted host's tenant, and it validates it there before anything is applied), so
    // the backend binds and validates nothing about it: a wrong key can never stop the pods from starting.
}

public sealed class HostingOptions
{
    public const string Section = "Hosting";

    public List<HostMountOptions> Mounts { get; set; } = [];
}

/// <summary>Startup validation: a wrong mount stops the app with a message naming the option.</summary>
public sealed partial class HostingOptionsValidator : IValidateOptions<HostingOptions>
{
    // \z, not $: in .NET $ also matches before a trailing newline, which a multi-line config value could carry.
    [GeneratedRegex("^(/[A-Za-z0-9._~-]+)+\\z")]
    private static partial Regex PrefixPattern();

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*\\z")]
    private static partial Regex HostPattern();

    public ValidateOptionsResult Validate(string? name, HostingOptions options)
    {
        var failures = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < options.Mounts.Count; i++)
        {
            var m = options.Mounts[i];
            var at = $"Hosting:Mounts:{i}";
            if (m.Host.Length is 0 or > 253 || !HostPattern().IsMatch(m.Host))
                failures.Add($"{at}:Host must be a lower-case DNS name without scheme, port or path (got '{m.Host}').");
            else if (!seen.Add(m.Host))
                failures.Add($"{at}:Host '{m.Host}' is mounted twice.");
            if (!PrefixPattern().IsMatch(m.Prefix) || m.Prefix.Split('/').Any(s => s is "." or ".."))
                failures.Add($"{at}:Prefix must be one or more path segments such as /chat, without a trailing slash (got '{m.Prefix}').");
        }
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// For a host listed in <c>Hosting:Mounts</c> the backend is only served under its prefix, which the ingress forwards
/// unchanged. The prefix moves from <c>Request.Path</c> to <c>Request.PathBase</c> before anything else sees the request,
/// so routing, the tenant middleware, the CSRF/origin checks and every endpoint work on the same paths as on an unmounted
/// host. A mounted host without the prefix is a 404. Every other host is untouched. No forwarded header is read: the mount
/// is decided by the Host header and the configuration alone, which is why a stripping ingress (rewrite) is not supported
/// (a stripped <c>/chat/messages</c> cannot be told from the unstripped route of the same name). A mounted host serves
/// the widget on a site's own origin, so tenant onboarding and the operator routes under <c>/onboarding</c> are not
/// reachable there (404); they stay on the chat hosts. A path with an empty segment (<c>/chat//x</c>) is a 404 as well.
/// </summary>
public sealed class HostMountMiddleware(RequestDelegate next, IOptions<HostingOptions> options)
{
    private readonly Dictionary<string, PathString> _mounts =
        options.Value.Mounts.ToDictionary(m => m.Host, m => new PathString(m.Prefix), StringComparer.Ordinal);

    public async Task InvokeAsync(HttpContext ctx)
    {
        var host = _mounts.Count == 0 ? null : TenantHost.Normalize(ctx.Request.Host);
        if (host is null || !_mounts.TryGetValue(host, out var prefix))
        {
            await next(ctx);
            return;
        }

        if (!ctx.Request.Path.StartsWithSegments(prefix, out var rest))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // No route has an empty segment, and "//onboarding" must not slip past the segment match below.
        if (rest.StartsWithSegments("/onboarding") || (rest.Value?.Contains("//", StringComparison.Ordinal) ?? false))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var originalPath = ctx.Request.Path;
        var originalBase = ctx.Request.PathBase;
        ctx.Request.PathBase = originalBase.Add(prefix);
        ctx.Request.Path = rest.HasValue ? rest : new PathString("/");
        try
        {
            await next(ctx);
        }
        finally
        {
            ctx.Request.Path = originalPath;
            ctx.Request.PathBase = originalBase;
        }
    }
}

public static class HostMountRegistration
{
    public static IServiceCollection AddHostMounts(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<HostingOptions>().Bind(config.GetSection(HostingOptions.Section)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<HostingOptions>, HostingOptionsValidator>();
        return services;
    }

    /// <summary>Before <c>UseTenancy</c>: the tenant is resolved from the host and the path without the mount.</summary>
    public static IApplicationBuilder UseHostMounts(this IApplicationBuilder app) => app.UseMiddleware<HostMountMiddleware>();
}
