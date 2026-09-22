namespace ObsWebSocket.Core;

/// <summary>
/// Supplies the delay before each reconnect attempt.
/// </summary>
/// <remarks>
/// Register an implementation after <c>AddObsWebSocketClient</c> to replace the backoff curve
/// built from <see cref="ObsWebSocketClientOptions"/>. The connection loop still decides how many
/// attempts to make and which failures are fatal.
/// </remarks>
public interface IObsReconnectDelays
{
    /// <summary>
    /// Returns the delay to wait before the retry following <paramref name="retryIndex"/>.
    /// </summary>
    /// <param name="retryIndex">Zero-based index of the retry about to be made.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    ValueTask<TimeSpan> GetDelayAsync(
        int retryIndex,
        CancellationToken cancellationToken = default
    );
}
