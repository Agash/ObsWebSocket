using ObsWebSocket.Core.Protocol.Generated;

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
    /// Optional event subscriptions. Defaults to all non-high-volume events when null.
    /// </summary>
    public EventSubscription? EventSubscriptions { get; set; }

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

    /// <summary>
    /// Retries requests OBS refuses with <c>NotReady</c> (207), which it does while changing
    /// scene collection or shutting down. Off by default.
    /// </summary>
    public NotReadyRetryOptions NotReadyRetry { get; set; } = new();

    /// <summary>
    /// Gets or sets the largest inbound message the client will assemble, in bytes.
    /// Defaults to <see cref="ObsWebSocketClient.DefaultMaxIncomingMessageBytes"/> (64 MiB).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A WebSocket message arrives as any number of fragments and its size is only known once the
    /// last one has been read, so without a ceiling the client will assemble whatever it is sent.
    /// The receive loop stops before appending the fragment that would cross this limit, and the
    /// connection fails with <see cref="ObsWebSocketMessageTooLargeException"/> instead of growing.
    /// </para>
    /// <para>
    /// The default is generous because legitimate responses are large: a
    /// <c>GetSourceScreenshot</c> of a 4K canvas is a base64 data URI of several megabytes, and a
    /// big scene collection's item list is not small either. Size it to the largest response the
    /// application actually asks OBS for, not to the receive buffer.
    /// </para>
    /// </remarks>
    public int MaxIncomingMessageBytes { get; set; } =
        ObsWebSocketClient.DefaultMaxIncomingMessageBytes;
}

/// <summary>
/// Retry behaviour for requests OBS refuses with <c>NotReady</c>.
/// </summary>
/// <remarks>
/// OBS rejects the request before the handler runs, so nothing is partially applied and a
/// mutation is as safe to resend as a read.
/// </remarks>
public sealed class NotReadyRetryOptions
{
    /// <summary>Whether to retry. Defaults to <see langword="false"/>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Retries after the first refusal. Defaults to 3.</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Delay before the first retry, in milliseconds. Defaults to 250.</summary>
    public int InitialDelayMs { get; set; } = 250;

    /// <summary>Ceiling on the delay, in milliseconds. Defaults to 2000.</summary>
    public int MaxDelayMs { get; set; } = 2000;
}
