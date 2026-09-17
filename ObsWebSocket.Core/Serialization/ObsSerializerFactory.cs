namespace ObsWebSocket.Core.Serialization;

/// <summary>
/// Supplies the serializer for a wire format, each time a connection is established.
/// </summary>
/// <remarks>
/// <see cref="ObsWebSocketClientOptions.Format"/> can change at runtime, so the serializer is
/// resolved per connection rather than injected once.
/// </remarks>
/// <param name="format">The format the connection will negotiate.</param>
/// <returns>The serializer speaking that format.</returns>
public delegate IWebSocketMessageSerializer ObsSerializerFactory(SerializationFormat format);
