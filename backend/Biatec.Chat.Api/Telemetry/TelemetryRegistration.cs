using System.Diagnostics;
using Biatec.Chat.Api.Chat.SignIn;
using Biatec.Chat.Api.Orchestration;
using Biatec.Chat.Core.Capabilities;
using Biatec.Chat.Core.Evidence;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Biatec.Chat.Api.Telemetry;

public static class TelemetryRegistration
{
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

        var otel = services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(o.ServiceName))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(a => a.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddAWSInstrumentation()
                .AddSource("Npgsql")
                .AddProcessor(new TelemetryScrubber()))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddAWSInstrumentation()
                .AddMeter(ChatMetrics.MeterName));
        if (string.Equals(o.Exporter, "Console", StringComparison.OrdinalIgnoreCase))
        {
            services.ConfigureOpenTelemetryTracerProvider(t => t.AddConsoleExporter());
            services.ConfigureOpenTelemetryMeterProvider(m => m.AddConsoleExporter());
        }
        else if (string.IsNullOrWhiteSpace(o.OtlpEndpoint))
        {
            if (!string.Equals(o.Exporter, "None", StringComparison.OrdinalIgnoreCase)) otel.UseOtlpExporter();
        }
        else if (!string.Equals(o.Exporter, "None", StringComparison.OrdinalIgnoreCase))
            otel.UseOtlpExporter(OpenTelemetry.Exporter.OtlpExportProtocol.Grpc, new Uri(o.OtlpEndpoint));
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
/// addresses, the user agent, query strings and any end-user identity. Request paths stay (they hold no secrets:
/// tokens never travel in URLs here).
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
        // url.full keeps the scheme, host and path only.
        if (activity.GetTagItem("url.full") is string full && Uri.TryCreate(full, UriKind.Absolute, out var u) && (u.Query.Length > 0 || u.UserInfo.Length > 0))
            activity.SetTag("url.full", u.GetLeftPart(UriPartial.Path).Replace(u.UserInfo + "@", ""));
    }
}
