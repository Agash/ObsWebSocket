namespace ObsWebSocket.Core;

/// <summary>
/// Options for configuring the <see cref="ObsWebSocketClient"/>.
/// </summary>
public sealed class ObsWebSocketClientOptions
{
    /// <summary>
    /// The URI of the OBS WebSocket server (e.g., ws://localhost:4455).
    /// This must be provided either directly or via configuration.
    /// </summary>
    public Uri? ServerUri { get; set; }

    /// <summary>
    /// The password for authentication, if required by the server.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Optional bitmask of event subscriptions flags. Consult OBS WebSocket documentation for specific values.
    /// Defaults to subscribing to all non-high-volume events if null.
    /// See <see cref="Protocol.Generated.EventSubscription"/>.
    /// </summary>
    public uint? EventSubscriptions { get; set; }

    /// <summary>
    /// Timeout in milliseconds for the initial Hello/Identified handshake phase.
    /// Defaults to <see cref="ObsWebSocketClient.DefaultHandshakeTimeoutMs"/>.
    /// </summary>
    public int HandshakeTimeoutMs { get; set; } = ObsWebSocketClient.DefaultHandshakeTimeoutMs;

    /// <summary>
    /// Default timeout in milliseconds for awaiting individual request responses.
    /// Defaults to <see cref="ObsWebSocketClient.DefaultRequestTimeoutMs"/>.
    /// </summary>
    public int RequestTimeoutMs { get; set; } = ObsWebSocketClient.DefaultRequestTimeoutMs;

    /// <summary>
    /// Specifies the serialization format (JSON or MessagePack) to use for communication.
    /// Defaults to <see cref="SerializationFormat.Json"/>.
    /// </summary>
    public SerializationFormat Format { get; set; } = SerializationFormat.Json;

    /// <summary>
    /// Gets or sets whether the client should automatically attempt to reconnect if the connection is lost unexpectedly.
    /// Defaults to <c>true</c>.
    /// </summary>
    public bool AutoReconnectEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the initial delay in milliseconds before the first reconnection attempt.
    /// Defaults to 5000ms (5 seconds).
    /// </summary>
    public int InitialReconnectDelayMs { get; set; } = 5000;

    /// <summary>
    /// Gets or sets the maximum number of consecutive reconnection attempts before giving up.
    /// Set to 0 to disable retries even if <see cref="AutoReconnectEnabled"/> is true.
    /// Set to a negative value (e.g., -1) for infinite retry attempts.
    /// Defaults to 5 attempts.
    /// </summary>
    public int MaxReconnectAttempts { get; set; } = 5;

    /// <summary>
    /// Gets or sets the multiplier applied to the reconnect delay for exponential backoff.
    /// A value of 1.0 means fixed delay. Must be >= 1.0.
    /// Defaults to 2.0.
    /// </summary>
    public double ReconnectBackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Gets or sets the maximum delay in milliseconds between reconnection attempts, capping the exponential backoff.
    /// Defaults to 60000ms (1 minute).
    /// </summary>
    public int MaxReconnectDelayMs { get; set; } = 60000;
}
