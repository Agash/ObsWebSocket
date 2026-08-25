using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace ObsWebSocket.Core.Events;

/// <summary>
/// Bridges the client's classic <see cref="EventHandler{TEventArgs}"/> events onto
/// <see cref="IAsyncEnumerable{T}"/>, so callers can consume them with <c>await foreach</c>
/// instead of managing subscribe and unsubscribe by hand.
/// </summary>
/// <remarks>
/// The events themselves are unchanged. This is an additional way to observe them, not a
/// replacement, and several streams may run over the same event at once.
/// </remarks>
public static class EventStream
{
    /// <summary>
    /// Default number of events buffered per stream before the oldest is dropped.
    /// </summary>
    public const int DefaultCapacity = 64;

    /// <summary>
    /// Subscribes to an event for the lifetime of the enumeration and yields each occurrence.
    /// </summary>
    /// <typeparam name="TEventArgs">The event args type carried by the event.</typeparam>
    /// <param name="subscribe">Attaches the supplied handler to the event.</param>
    /// <param name="unsubscribe">Detaches the supplied handler from the event.</param>
    /// <param name="capacity">
    /// How many events to buffer when the consumer falls behind. Once full, the oldest buffered
    /// event is dropped so a slow consumer cannot stall the receive loop.
    /// </param>
    /// <param name="cancellationToken">Ends the enumeration and unsubscribes.</param>
    /// <returns>An async sequence of events, running until canceled.</returns>
    /// <remarks>
    /// The subscription is attached before the first item is awaited, so a caller can start
    /// enumerating and then trigger the action that produces the event without racing it.
    /// </remarks>
    public static async IAsyncEnumerable<TEventArgs> Create<TEventArgs>(
        Action<EventHandler<TEventArgs>> subscribe,
        Action<EventHandler<TEventArgs>> unsubscribe,
        int capacity = DefaultCapacity,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
        where TEventArgs : ObsEventArgs
    {
        ArgumentNullException.ThrowIfNull(subscribe);
        ArgumentNullException.ThrowIfNull(unsubscribe);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        Channel<TEventArgs> channel = Channel.CreateBounded<TEventArgs>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            }
        );

        void Handler(object? sender, TEventArgs e) => channel.Writer.TryWrite(e);

        subscribe(Handler);
        try
        {
            await foreach (
                TEventArgs item in channel
                    .Reader.ReadAllAsync(cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                yield return item;
            }
        }
        finally
        {
            unsubscribe(Handler);
            _ = channel.Writer.TryComplete();
        }
    }
}
