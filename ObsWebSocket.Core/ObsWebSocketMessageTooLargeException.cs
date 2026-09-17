namespace ObsWebSocket.Core;

/// <summary>
/// Thrown when an inbound message exceeds
/// <see cref="ObsWebSocketClientOptions.MaxIncomingMessageBytes"/>.
/// </summary>
/// <remarks>
/// Ends the connection rather than the single message. By the time the limit is reached the
/// remaining fragments of that message cannot be skipped without also losing track of where the
/// next message begins, and a peer that sends one is not one to keep reading from.
/// </remarks>
public sealed class ObsWebSocketMessageTooLargeException : ObsWebSocketException
{
    /// <summary>Initializes the exception for a message that crossed the limit.</summary>
    /// <param name="maxBytes">The configured ceiling.</param>
    /// <param name="attemptedBytes">The size the message had reached when it was stopped.</param>
    public ObsWebSocketMessageTooLargeException(int maxBytes, long attemptedBytes)
        : base(
            $"An incoming message reached {attemptedBytes} bytes, past the "
                + $"{maxBytes} byte limit set by {nameof(ObsWebSocketClientOptions)}."
                + $"{nameof(ObsWebSocketClientOptions.MaxIncomingMessageBytes)}. "
                + "Raise the limit if this endpoint legitimately sends messages this large."
        )
    {
        MaxBytes = maxBytes;
        AttemptedBytes = attemptedBytes;
    }

    /// <summary>Initializes the exception with a message.</summary>
    /// <param name="message">The message describing the failure.</param>
    public ObsWebSocketMessageTooLargeException(string message)
        : base(message) { }

    /// <summary>Initializes the exception with a message and inner exception.</summary>
    /// <param name="message">The message describing the failure.</param>
    /// <param name="innerException">The underlying cause.</param>
    public ObsWebSocketMessageTooLargeException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Initializes the exception.</summary>
    public ObsWebSocketMessageTooLargeException()
        : base("An incoming message exceeded the configured size limit.") { }

    /// <summary>The configured ceiling, in bytes.</summary>
    public int MaxBytes { get; }

    /// <summary>How large the message had grown when it was stopped, in bytes.</summary>
    public long AttemptedBytes { get; }
}
