namespace ObsWebSocket.Core.Events;

/// <summary>
/// Generic base class for OBS WebSocket event arguments containing specific event data.
/// </summary>
/// <typeparam name="TData">The type of the specific event data payload.</typeparam>
/// <param name="eventData">The deserialized event data payload.</param>
public class ObsEventEventArgs<TData>(TData eventData) : ObsEventArgs
{
    /// <summary>
    /// Gets the specific data payload for this event.
    /// </summary>
    public TData EventData { get; } = eventData;
}
