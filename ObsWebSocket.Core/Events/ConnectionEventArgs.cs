namespace ObsWebSocket.Core.Events;

/// <summary>
/// Provides data for the <see cref="ObsWebSocketClient.Connecting"/> event.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ConnectingEventArgs"/> class.
/// </remarks>
/// <param name="serverUri">The URI the client is attempting to connect to.</param>
/// <param name="attemptNumber">The current connection attempt number.</param>
public sealed class ConnectingEventArgs(Uri serverUri, int attemptNumber) : EventArgs
{
    /// <summary>
    /// Gets the URI the client is attempting to connect to.
    /// </summary>
    public Uri ServerUri { get; } = serverUri;

    /// <summary>
    /// Gets the current connection attempt number (1 for the initial attempt).
    /// </summary>
    public int AttemptNumber { get; } = attemptNumber;
}

/// <summary>
/// Provides data for the <see cref="ObsWebSocketClient.Disconnected"/> event.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DisconnectedEventArgs"/> class.
/// </remarks>
/// <param name="reasonException">The exception that caused the disconnection, or <c>null</c> for a graceful disconnection.</param>
public sealed class DisconnectedEventArgs(Exception? reasonException) : EventArgs
{
    /// <summary>
    /// Gets the exception that caused the disconnection, or <c>null</c> if disconnected gracefully via <see cref="ObsWebSocketClient.DisconnectAsync"/>.
    /// </summary>
    public Exception? ReasonException { get; } = reasonException;
}

/// <summary>
/// Provides data for the <see cref="ObsWebSocketClient.ConnectionFailed"/> event.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ConnectionFailedEventArgs"/> class.
/// </remarks>
/// <param name="serverUri">The URI the client attempted to connect to.</param>
/// <param name="attemptNumber">The connection attempt number that failed.</param>
/// <param name="errorException">The exception that occurred.</param>
public sealed class ConnectionFailedEventArgs(
    Uri serverUri,
    int attemptNumber,
    Exception errorException
) : EventArgs
{
    /// <summary>
    /// Gets the URI the client attempted to connect to.
    /// </summary>
    public Uri ServerUri { get; } = serverUri;

    /// <summary>
    /// Gets the connection attempt number that failed.
    /// </summary>
    public int AttemptNumber { get; } = attemptNumber;

    /// <summary>
    /// Gets the exception that occurred during the connection attempt.
    /// </summary>
    public Exception ErrorException { get; } = errorException;
}

/// <summary>
/// Provides data for the <see cref="ObsWebSocketClient.AuthenticationFailure"/> event.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="AuthenticationFailureEventArgs"/> class.
/// </remarks>
/// <param name="serverUri">The URI the client attempted to connect to.</param>
/// <param name="attemptNumber">The connection attempt number that failed authentication.</param>
/// <param name="errorException">The exception indicating the failure.</param>
public sealed class AuthenticationFailureEventArgs(
    Uri serverUri,
    int attemptNumber,
    Exception errorException
) : EventArgs
{
    /// <summary>
    /// Gets the URI the client attempted to connect to.
    /// </summary>
    public Uri ServerUri { get; } = serverUri;

    /// <summary>
    /// Gets the connection attempt number during which authentication failed.
    /// </summary>
    public int AttemptNumber { get; } = attemptNumber;

    /// <summary>
    /// Gets the exception indicating the authentication failure.
    /// </summary>
    public Exception ErrorException { get; } = errorException;
}
