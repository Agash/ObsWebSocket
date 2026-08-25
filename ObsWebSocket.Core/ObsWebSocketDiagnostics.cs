using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ObsWebSocket.Core;

/// <summary>
/// Activity and metric sources for the client. Both are inert until something subscribes.
/// </summary>
public static class ObsWebSocketDiagnostics
{
    /// <summary>Name of the <see cref="System.Diagnostics.ActivitySource"/> for requests.</summary>
    public const string ActivitySourceName = "ObsWebSocket.Core";

    /// <summary>Name of the <see cref="System.Diagnostics.Metrics.Meter"/> for client metrics.</summary>
    public const string MeterName = "ObsWebSocket.Core";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private static readonly Meter s_meter = new(MeterName);

    internal static readonly Counter<long> RequestsSent = s_meter.CreateCounter<long>(
        "obsws.requests.sent",
        unit: "{request}",
        description: "Requests sent to OBS."
    );

    internal static readonly Counter<long> RequestsFailed = s_meter.CreateCounter<long>(
        "obsws.requests.failed",
        unit: "{request}",
        description: "Requests that OBS rejected or that timed out."
    );

    internal static readonly Histogram<double> RequestDuration = s_meter.CreateHistogram<double>(
        "obsws.request.duration",
        unit: "ms",
        description: "Time from sending a request to receiving its response."
    );

    internal static readonly Counter<long> EventsReceived = s_meter.CreateCounter<long>(
        "obsws.events.received",
        unit: "{event}",
        description: "Events received from OBS."
    );

    internal static readonly Counter<long> Reconnects = s_meter.CreateCounter<long>(
        "obsws.reconnects",
        unit: "{attempt}",
        description: "Reconnection attempts."
    );
}
