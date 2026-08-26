namespace ObsWebSocket.Core;

/// <summary>
/// Thrown when authentication against the OBS WebSocket server fails — typically because the
/// configured password is wrong, missing, or otherwise rejected by the server's challenge.
/// </summary>
/// <remarks>
/// Consumers can catch this directly to distinguish auth failures from generic
/// <see cref="ObsWebSocketException"/> connection issues without inspecting the message string.
/// The same condition is also surfaced via the
/// <see cref="ObsWebSocketClient.AuthenticationFailure"/> event for callers that prefer the
/// event-driven path.
/// </remarks>
[Serializable]
public sealed class AuthenticationFailureException : ObsWebSocketException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AuthenticationFailureException"/> class.
    /// </summary>
    public AuthenticationFailureException() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthenticationFailureException"/> class with
    /// a specified error message.
    /// </summary>
    public AuthenticationFailureException(string message)
        : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthenticationFailureException"/> class with
    /// a specified error message and a reference to the inner exception that caused this one.
    /// </summary>
    public AuthenticationFailureException(string message, Exception? innerException)
        : base(
            message,
            innerException
                ?? new InvalidOperationException(
                    "OBS authentication failed without a more specific cause."
                )
        ) { }
}
