using System.Diagnostics;
using Biatec.Chat.Api.Chat.SignIn;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Evidence;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Biatec.Chat.Api.Telemetry;

public static class TelemetryRegistration
{
    /// <summary>
    /// Probe requests are not traced. The filter sees the path as the client sent it, before a host mount moves its prefix
    /// into PathBase, so <c>/health</c> also counts behind the mount prefix of that very host (<c>/chat/health</c> on the
    /// mounted host), and nothing else.
    /// </summary>
    public static bool IsHealthRequest(PathString path, string? host, IEnumerable<Hosting.HostMountOptions> mounts) =>
        path.StartsWithSegments("/health")
        || (host is not null && mounts.Any(m => m.Host == host && path.StartsWithSegments(m.Prefix + "/health")));

    /// <summary>
    /// OpenTelemetry traces (incoming requests, every outbound HTTP call: Biatec MCP, Biatec OIDC, algod, the
    /// builders; AWS SDK calls: Bedrock, KMS; PostgreSQL) and metrics (ASP.NET Core, HttpClient, AWS SDK and
    /// <see cref="ChatMetrics"/>), exported over OTLP. Product metrics are hooked in by decorating services that are
    /// already registered, so call this last. Does nothing unless <c>Telemetry:Enabled</c> is true.
    /// </summary>
    public static IServiceCollection AddBiatecTelemetry(this IServiceCollection services, IConfiguration config)
    {
        var o = config.GetSection(TelemetryOptions.Section).Get<TelemetryOptions>() ?? new TelemetryOptions();
        if (!o.Enabled) return services;

        services.AddSingleton<ChatMetrics>();
        Decorate<IPolicyEventSink>(services, _ => true, (sp, inner) => new MeteringPolicyEventSink(inner, sp.GetRequiredService<ChatMetrics>()));
        Decorate<IEvidenceLog>(services, _ => true, (sp, inner) => new MeteringEvidenceLog(inner, sp.GetRequiredService<ChatMetrics>()));
        Decorate<IToolExecutor>(services, d => d.ImplementationType == typeof(RequestSignInExecutor),
            (sp, inner) => new MeteringSignInExecutor(inner, sp.GetRequiredService<ChatMetrics>()), all: true);

        // The validated mounts, taken from the options once (static configuration), not resolved per request.
        IReadOnlyList<Hosting.HostMountOptions>? mounts = null;
        var otel = services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(o.ServiceName))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(a => a.Filter = ctx => !IsHealthRequest(ctx.Request.Path, Tenancy.TenantHost.Normalize(ctx.Request.Host),
                    mounts ??= ctx.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<Hosting.HostingOptions>>().Value.Mounts))
                .AddHttpClientInstrumentation()
                .AddAWSInstrumentation()
                .AddSource("Npgsql")
                .AddProcessor(new TelemetryScrubber()))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddAWSInstrumentation()
                .AddMeter(ChatMetrics.MeterName));
        // Traces and metrics only: logs are not exported, because the scrubber only sees spans.
        switch (o.Exporter.Trim().ToLowerInvariant())
        {
            case "console":
                otel.WithTracing(t => t.AddConsoleExporter()).WithMetrics(m => m.AddConsoleExporter());
                break;
            case "none":
                break;
            case "otlp":
                var protocol = Enum.Parse<OtlpExportProtocol>(o.OtlpProtocol, ignoreCase: true);
                void Otlp(OtlpExporterOptions e)
                {
                    e.Protocol = protocol;
                    if (!string.IsNullOrWhiteSpace(o.OtlpEndpoint)) e.Endpoint = new Uri(o.OtlpEndpoint);
                }
                otel.WithTracing(t => t.AddOtlpExporter(Otlp)).WithMetrics(m => m.AddOtlpExporter(Otlp));
                break;
            default:
                throw new InvalidOperationException("Telemetry:Exporter must be Otlp, Console or None.");
        }
        return services;
    }

    /// <summary>Replaces the matching registrations of <typeparamref name="T"/> (the last one, or all) with a decorator of the same lifetime.</summary>
    private static void Decorate<T>(IServiceCollection services, Func<ServiceDescriptor, bool> match, Func<IServiceProvider, T, T> wrap, bool all = false)
        where T : class
    {
        var targets = services.Where(d => d.ServiceType == typeof(T) && !d.IsKeyedService && match(d)).ToList();
        if (!all) targets = targets.TakeLast(1).ToList();
        foreach (var d in targets)
        {
            var i = services.IndexOf(d);
            services[i] = new ServiceDescriptor(typeof(T), sp => wrap(sp, Resolve<T>(sp, d)), d.Lifetime);
        }
    }

    private static T Resolve<T>(IServiceProvider sp, ServiceDescriptor d) where T : class => (T)(
        d.ImplementationInstance
        ?? d.ImplementationFactory?.Invoke(sp)
        ?? ActivatorUtilities.CreateInstance(sp, d.ImplementationType!));
}

/// <summary>
/// Strips attributes that can carry personal data or credentials before a span leaves the process: client and peer
/// addresses, the user agent, query strings, URL credentials and any end-user identity. In URL paths, segments that
/// look like a wallet address or another long identifier (for example <c>/wallet/{network}/{address}/sign</c> at
/// Biatec OIDC, or algod account lookups) are replaced with <c>{id}</c>.
/// </summary>
public sealed class TelemetryScrubber : BaseProcessor<Activity>
{
    public static readonly IReadOnlyList<string> Dropped =
    [
        "client.address", "client.port", "network.peer.address", "network.peer.port", "net.peer.ip", "net.sock.peer.addr",
        "http.client_ip", "user_agent.original", "http.user_agent", "url.query", "enduser.id", "enduser.role", "enduser.scope",
    ];

    public override void OnEnd(Activity activity)
    {
        foreach (var key in Dropped)
            if (activity.GetTagItem(key) is not null)
                activity.SetTag(key, null);
        // url.full keeps the scheme, host and the redacted path only.
        if (activity.GetTagItem("url.full") is string full && Uri.TryCreate(full, UriKind.Absolute, out var u))
            activity.SetTag("url.full", $"{u.Scheme}://{u.Authority}{RedactPath(u.AbsolutePath)}");
        if (activity.GetTagItem("url.path") is string path)
            activity.SetTag("url.path", RedactPath(path));
    }

    /// <summary>Algorand addresses are 58 characters and Bitcoin ones 26 to 62; no route segment of ours is that long.</summary>
    public static string RedactPath(string path) =>
        string.Join('/', path.Split('/').Select(seg => seg.Length >= 25 && seg.All(char.IsAsciiLetterOrDigit) ? "{id}" : seg));
}
