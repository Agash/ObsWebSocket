using System.Diagnostics.Metrics;

namespace ObsWebSocket.Core;

/// <summary>
/// The client's metric instruments.
/// </summary>
/// <remarks>
/// Built from an <see cref="IMeterFactory"/> when one is available, so the meter belongs to the
/// container that created it and a test can read the instruments back. Falls back to a meter of
/// its own when the client is constructed outside dependency injection.
/// </remarks>
public sealed class ObsWebSocketMetrics : IDisposable
{
    private readonly Meter _meter;
    private readonly bool _ownsMeter;

    /// <summary>Creates the instruments from a factory.</summary>
    /// <param name="meterFactory">The factory to create the meter from.</param>
    public ObsWebSocketMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _meter = meterFactory.Create(ObsWebSocketDiagnostics.MeterName);
        _ownsMeter = false;
        (RequestsSent, RequestsFailed, RequestDuration, EventsReceived, Reconnects) = Create(
            _meter
        );
    }

    private ObsWebSocketMetrics()
    {
        _meter = new Meter(ObsWebSocketDiagnostics.MeterName);
        _ownsMeter = true;
        (RequestsSent, RequestsFailed, RequestDuration, EventsReceived, Reconnects) = Create(
            _meter
        );
    }

    /// <summary>Instruments for a client built outside dependency injection.</summary>
    public static ObsWebSocketMetrics Shared { get; } = new();

    /// <summary>Requests sent to OBS.</summary>
    public Counter<long> RequestsSent { get; }

    /// <summary>Requests that OBS rejected or that timed out.</summary>
    public Counter<long> RequestsFailed { get; }

    /// <summary>Time from sending a request to receiving its response.</summary>
    public Histogram<double> RequestDuration { get; }

    /// <summary>Events received from OBS.</summary>
    public Counter<long> EventsReceived { get; }

    /// <summary>Reconnection attempts.</summary>
    public Counter<long> Reconnects { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsMeter)
        {
            _meter.Dispose();
        }
    }

    private static (
        Counter<long> Sent,
        Counter<long> Failed,
        Histogram<double> Duration,
        Counter<long> Events,
        Counter<long> Reconnects
    ) Create(Meter meter) =>
        (
            meter.CreateCounter<long>(
                "obsws.requests.sent",
                unit: "{request}",
                description: "Requests sent to OBS."
            ),
            meter.CreateCounter<long>(
                "obsws.requests.failed",
                unit: "{request}",
                description: "Requests that OBS rejected or that timed out."
            ),
            meter.CreateHistogram<double>(
                "obsws.request.duration",
                unit: "ms",
                description: "Time from sending a request to receiving its response."
            ),
            meter.CreateCounter<long>(
                "obsws.events.received",
                unit: "{event}",
                description: "Events received from OBS."
            ),
            meter.CreateCounter<long>(
                "obsws.reconnects",
                unit: "{attempt}",
                description: "Reconnection attempts."
            )
        );
}
