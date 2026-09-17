using System.Diagnostics;
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
    public static IAsyncEnumerable<TEventArgs> Create<TEventArgs>(
        Action<EventHandler<TEventArgs>> subscribe,
        Action<EventHandler<TEventArgs>> unsubscribe,
        int capacity = DefaultCapacity,
        CancellationToken cancellationToken = default
    )
        where TEventArgs : ObsEventArgs =>
        Create(subscribe, unsubscribe, capacity, metrics: null, cancellationToken);

    /// <summary>
    /// Subscribes to an event, recording drops to the supplied instruments.
    /// </summary>
    /// <typeparam name="TEventArgs">The event args type carried by the event.</typeparam>
    /// <param name="subscribe">Attaches the supplied handler to the event.</param>
    /// <param name="unsubscribe">Detaches the supplied handler from the event.</param>
    /// <param name="capacity">How many events to buffer when the consumer falls behind.</param>
    /// <param name="metrics">
    /// Instruments to count drops on, or <see langword="null"/> to use the shared ones.
    /// </param>
    /// <param name="cancellationToken">Ends the enumeration and unsubscribes.</param>
    /// <returns>An async sequence of events, running until canceled.</returns>
    public static IAsyncEnumerable<TEventArgs> Create<TEventArgs>(
        Action<EventHandler<TEventArgs>> subscribe,
        Action<EventHandler<TEventArgs>> unsubscribe,
        int capacity,
        ObsWebSocketMetrics? metrics,
        CancellationToken cancellationToken = default
    )
        where TEventArgs : ObsEventArgs
    {
        // Validate here rather than in the iterator, so bad arguments throw at the call site
        // instead of being deferred until the caller starts enumerating.
        ArgumentNullException.ThrowIfNull(subscribe);
        ArgumentNullException.ThrowIfNull(unsubscribe);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        return Iterate(subscribe, unsubscribe, capacity, metrics, cancellationToken);
    }

    private static async IAsyncEnumerable<TEventArgs> Iterate<TEventArgs>(
        Action<EventHandler<TEventArgs>> subscribe,
        Action<EventHandler<TEventArgs>> unsubscribe,
        int capacity,
        ObsWebSocketMetrics? metrics,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
        where TEventArgs : ObsEventArgs
    {
        Channel<TEventArgs> channel = Channel.CreateBounded<TEventArgs>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            }
        );

        ObsWebSocketMetrics instruments = metrics ?? ObsWebSocketMetrics.Shared;
        TagList dropTags = new() { { "obsws.event_type", typeof(TEventArgs).Name } };

        // Counted here rather than inferred from TryWrite, which reports success even when it
        // evicted an older event to make room. Tracking the depth is the only way to tell the
        // two apart, and an eviction is exactly what a consumer needs to be told about.
        int buffered = 0;

        void Handler(object? sender, TEventArgs e)
        {
            if (channel.Writer.TryWrite(e) && Interlocked.Increment(ref buffered) > capacity)
            {
                _ = Interlocked.Decrement(ref buffered);
                instruments.EventsDropped.Add(1, dropTags);
            }
        }

        subscribe(Handler);
        try
        {
            await foreach (
                TEventArgs item in channel
                    .Reader.ReadAllAsync(cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                _ = Interlocked.Decrement(ref buffered);
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
