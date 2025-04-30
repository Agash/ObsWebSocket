namespace ObsWebSocket.Core;

/// <summary>
/// Represents errors specific to the ObsWebSocket.Core library or the OBS WebSocket protocol interaction.
/// </summary>
[Serializable]
public class ObsWebSocketException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ObsWebSocketException"/> class.
    /// </summary>
    public ObsWebSocketException() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="ObsWebSocketException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public ObsWebSocketException(string message)
        : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="ObsWebSocketException"/> class with a specified error message
    /// and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="innerException">The exception that is the cause of the current exception, or a null reference if no inner exception is specified.</param>
    public ObsWebSocketException(string message, Exception? innerException)
        : base(message, innerException) { }
}
