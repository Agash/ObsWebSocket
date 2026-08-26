using Polly;
using Polly.Retry;

namespace ObsWebSocket.Core;

/// <summary>
/// Supplies the delay before each reconnect attempt, from the configured resilience pipeline.
/// </summary>
/// <remarks>
/// The connection loop owns attempt counting and decides which failures are fatal, because a
/// clean disconnect is not an exception and so cannot drive a retry strategy. This type asks the
/// strategy only for the delay, which keeps the backoff curve, its cap, and its jitter in one
/// place that an application can replace.
/// </remarks>
internal sealed class ReconnectDelays
{
    private readonly RetryStrategyOptions? _strategy;
    private readonly TimeSpan _fixedDelay;

    private ReconnectDelays()
    {
        _fixedDelay = TimeSpan.Zero;
    }

    /// <summary>Initializes delays for the supplied options.</summary>
    /// <param name="options">Options describing the reconnect behaviour.</param>
    public ReconnectDelays(ObsWebSocketClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _strategy = ObsWebSocketResilience.CreateRetryOptions(options);
        _fixedDelay = TimeSpan.FromMilliseconds(options.InitialReconnectDelayMs);
    }

    /// <summary>Delays for a client with no configured backoff.</summary>
    public static ReconnectDelays Disabled { get; } = new();

    /// <summary>
    /// Returns the delay to wait before the retry following <paramref name="retryIndex"/>.
    /// </summary>
    /// <param name="retryIndex">Zero-based index of the retry about to be made.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async ValueTask<TimeSpan> GetDelayAsync(
        int retryIndex,
        CancellationToken cancellationToken = default
    )
    {
        if (_strategy?.DelayGenerator is null)
        {
            return _fixedDelay;
        }

        RetryDelayGeneratorArguments<object> args = new(
            ResilienceContextPool.Shared.Get(cancellationToken),
            default,
            Math.Max(retryIndex, 0)
        );

        TimeSpan? generated = await _strategy.DelayGenerator(args).ConfigureAwait(false);
        TimeSpan delay = generated ?? _fixedDelay;

        return _strategy.UseJitter ? ApplyJitter(delay) : delay;
    }

    /// <summary>
    /// Spreads the delay over a window around its nominal value, so that several clients
    /// recovering from one outage do not retry in lockstep.
    /// </summary>
    private static TimeSpan ApplyJitter(TimeSpan delay) =>
        delay <= TimeSpan.Zero ? delay : delay * (0.75 + (Random.Shared.NextDouble() * 0.5));
}
