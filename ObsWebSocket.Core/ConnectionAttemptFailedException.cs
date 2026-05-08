namespace ObsWebSocket.Core;

/// <summary>
/// Thrown when a single connection attempt to the OBS WebSocket server fails for a non-auth
/// reason — protocol mismatch, transport error, handshake timeout, or a server-rejected
/// configuration. Distinct from <see cref="AuthenticationFailureException"/> so consumers can
/// decide whether to retry or stop.
/// </summary>
[Serializable]
public sealed class ConnectionAttemptFailedException : ObsWebSocketException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionAttemptFailedException"/> class.
    /// </summary>
    public ConnectionAttemptFailedException() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionAttemptFailedException"/> class
    /// with a specified error message.
    /// </summary>
    public ConnectionAttemptFailedException(string message)
        : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionAttemptFailedException"/> class
    /// with a specified error message and a reference to the inner exception that caused this one.
    /// </summary>
    public ConnectionAttemptFailedException(string message, Exception? innerException)
        : base(message, innerException ?? new InvalidOperationException("OBS connection attempt failed without a more specific cause.")) { }
}
