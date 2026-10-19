namespace Biatec.Chat.Api.Telemetry;

/// <summary>
/// OpenTelemetry settings, bound from the <c>Telemetry</c> section. Off by default (and so in tests); when on,
/// traces and metrics go to an OTLP collector. The endpoint can also come from the standard
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> environment variable.
/// </summary>
public sealed class TelemetryOptions
{
    public const string Section = "Telemetry";

    public bool Enabled { get; set; }

    public string ServiceName { get; set; } = "biatec-chat-api";

    /// <summary>Empty uses the OTLP exporter's own default (or the environment variable).</summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary><c>Grpc</c> (default, port 4317) or <c>HttpProtobuf</c> (port 4318).</summary>
    public string OtlpProtocol { get; set; } = "Grpc";

    /// <summary><c>Otlp</c> (default), <c>Console</c> (local checks) or <c>None</c>, which keeps instrumentation on without exporting (tests).</summary>
    public string Exporter { get; set; } = "Otlp";
}
