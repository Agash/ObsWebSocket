namespace ObsWebSocket.Core;

/// <summary>
/// Timeout helpers for cancellation sources built from a <see cref="TimeProvider"/>.
/// </summary>
internal static class TimeProviderCancellationExtensions
{
    /// <summary>
    /// Cancels <paramref name="source"/> after <paramref name="delay"/>, measured by
    /// <paramref name="timeProvider"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> always uses the system clock,
    /// and the <see cref="TimeProvider"/>-aware constructor cannot be used on a linked source,
    /// so the timeout is driven by a timer that the source disposes with itself.
    /// </remarks>
    public static void CancelAfterUsing(
        this CancellationTokenSource source,
        TimeProvider timeProvider,
        TimeSpan delay
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(timeProvider);

        ITimer timer = timeProvider.CreateTimer(
            static state =>
            {
                try
                {
                    ((CancellationTokenSource)state!).Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The operation finished and disposed its source before this timer fired.
                    // Nothing to cancel, and throwing here would reach the thread pool unhandled.
                }
            },
            source,
            delay,
            Timeout.InfiniteTimeSpan
        );

        _ = source.Token.Register(static state => ((ITimer)state!).Dispose(), timer);
    }
}
