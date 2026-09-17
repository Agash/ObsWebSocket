using ObsWebSocket.Core.Protocol.Generated;

namespace ObsWebSocket.Core.Networking;

/// <summary>
/// The options that are fixed for the life of one connection, captured when it is established.
/// </summary>
/// <remarks>
/// The sub-protocol is agreed during the handshake, so endpoint, credentials, format and
/// subscriptions cannot change underneath a live connection. Everything else stays on the options
/// monitor and is read per call.
/// </remarks>
internal sealed record ObsConnectionSettings
{
    private ObsConnectionSettings(ObsWebSocketClientOptions options)
    {
        ServerUri = options.ServerUri!;
        Password = options.Password;
        Format = options.Format;
        EventSubscriptions = options.EventSubscriptions ?? EventSubscription.All;
        HandshakeTimeoutMs = options.HandshakeTimeoutMs;
        AutoReconnectEnabled = options.AutoReconnectEnabled;
        MaxReconnectAttempts = options.MaxReconnectAttempts;
        InitialReconnectDelayMs = options.InitialReconnectDelayMs;
        MaxReconnectDelayMs = options.MaxReconnectDelayMs;

        // Clamped on the copy: a directly constructed client has no validator, and writing back
        // would change what every other reader of the monitored instance sees.
        ReconnectBackoffMultiplier = Math.Max(1.0, options.ReconnectBackoffMultiplier);
    }

    /// <summary>Where this connection was established to.</summary>
    public Uri ServerUri { get; }

    /// <summary>The password presented during this connection's handshake.</summary>
    public string? Password { get; }

    /// <summary>The wire format this connection negotiated.</summary>
    public SerializationFormat Format { get; }

    /// <summary>The subscriptions requested when identifying.</summary>
    public EventSubscription EventSubscriptions { get; }

    /// <summary>Timeout for the Hello/Identified exchange.</summary>
    public int HandshakeTimeoutMs { get; }

    /// <summary>Whether a lost connection is retried.</summary>
    public bool AutoReconnectEnabled { get; }

    /// <summary>Retry ceiling; negative means unlimited.</summary>
    public int MaxReconnectAttempts { get; }

    /// <summary>Delay before the first retry.</summary>
    public int InitialReconnectDelayMs { get; }

    /// <summary>Ceiling on the backoff delay.</summary>
    public int MaxReconnectDelayMs { get; }

    /// <summary>Backoff growth per attempt, never below 1.0.</summary>
    public double ReconnectBackoffMultiplier { get; }

    /// <summary>
    /// Captures the values a connection is built from.
    /// </summary>
    /// <param name="options">The options to read, normally the current monitored value.</param>
    /// <returns>An immutable snapshot.</returns>
    /// <exception cref="ArgumentNullException">Thrown if no server URI is configured.</exception>
    public static ObsConnectionSettings Capture(ObsWebSocketClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ServerUri, nameof(options.ServerUri));
        return new ObsConnectionSettings(options);
    }

    /// <summary>
    /// Reports whether a configuration change requires a new connection.
    /// </summary>
    /// <remarks>Only the values the socket is built from count.</remarks>
    /// <param name="options">The updated options.</param>
    /// <returns><see langword="true"/> when the live connection no longer matches.</returns>
    public bool RequiresNewConnection(ObsWebSocketClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ServerUri != options.ServerUri
            || Password != options.Password
            || Format != options.Format
            || EventSubscriptions != (options.EventSubscriptions ?? EventSubscription.All);
    }
}
