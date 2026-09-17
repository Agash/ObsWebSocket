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
        (
            RequestsSent,
            RequestsFailed,
            RequestDuration,
            EventsReceived,
            Reconnects,
            EventsDropped,
            MessagesDropped
        ) = Create(_meter);
    }

    private ObsWebSocketMetrics()
    {
        _meter = new Meter(ObsWebSocketDiagnostics.MeterName);
        _ownsMeter = true;
        (
            RequestsSent,
            RequestsFailed,
            RequestDuration,
            EventsReceived,
            Reconnects,
            EventsDropped,
            MessagesDropped
        ) = Create(_meter);
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

    /// <summary>
    /// Events dropped because a stream's consumer fell behind, tagged by event type.
    /// </summary>
    /// <remarks>
    /// Streams drop the oldest event when full so a slow consumer cannot stall the receive loop.
    /// Without this counter the only evidence is an event that never arrived.
    /// </remarks>
    public Counter<long> EventsDropped { get; }

    /// <summary>
    /// Inbound messages discarded without being dispatched, tagged by reason.
    /// </summary>
    /// <remarks>
    /// The receive loop tolerates a message it cannot read, so a newer OBS cannot tear the
    /// connection down. A malformed frame is indistinguishable from a forward-compatible one, so
    /// this separates a quiet connection from one that is discarding traffic.
    /// </remarks>
    public Counter<long> MessagesDropped { get; }

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
        Counter<long> Reconnects,
        Counter<long> EventsDropped,
        Counter<long> MessagesDropped
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
            ),
            meter.CreateCounter<long>(
                "obsws.events.dropped",
                unit: "{event}",
                description: "Events dropped because an event stream's consumer fell behind."
            ),
            meter.CreateCounter<long>(
                "obsws.messages.dropped",
                unit: "{message}",
                description: "Inbound messages discarded without being dispatched."
            )
        );
}
