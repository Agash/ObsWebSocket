namespace ObsWebSocket.Core;

/// <summary>
/// Specifies the serialization format to use for WebSocket communication.
/// </summary>
public enum SerializationFormat
{
    /// <summary>
    /// Use the JSON format (default). Text-based and human-readable.
    /// Corresponds to the "obswebsocket.json" sub-protocol.
    /// </summary>
    Json,

    /// <summary>
    /// Use the MessagePack format. Binary format, potentially more efficient.
    /// Corresponds to the "obswebsocket.msgpack" sub-protocol.
    /// Requires the `MessagePack-CSharp` package.
    /// </summary>
    MsgPack,
}
