using ObsWebSocket.Core.Protocol;

namespace ObsWebSocket.Core.Serialization;

/// <summary>
/// Interface for serializing and deserializing WebSocket messages.
/// </summary>
public interface IWebSocketMessageSerializer
{
    /// <summary>
    /// Gets the WebSocket sub-protocol identifier for this serializer (e.g., "obswebsocket.json").
    /// </summary>
    string ProtocolSubProtocol { get; }

    /// <summary>
    /// Serializes an outgoing message object into a byte array.
    /// </summary>
    /// <typeparam name="T">The type of the payload object.</typeparam>
    /// <param name="message">The outgoing message object (including OpCode and Payload).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A byte array representing the serialized message.</returns>
    Task<byte[]> SerializeAsync<T>(
        OutgoingMessage<T> message,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Deserializes an incoming message from a stream into its base structure.
    /// </summary>
    /// <remarks>
    /// The returned object should be castable to `IncomingMessage&lt;TData&gt;` where `TData`
    /// depends on the serializer implementation (e.g., `JsonElement` for JSON, `object` for MessagePack).
    /// </remarks>
    /// <param name="messageStream">The stream containing the message data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task yielding the deserialized message as an object (to be cast by the caller), or null if deserialization fails.</returns>
    Task<object?> DeserializeAsync(
        Stream messageStream,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Deserializes the raw payload data (e.g., JsonElement, object from MessagePack) into a specific target type.
    /// </summary>
    /// <typeparam name="TPayload">The target type to deserialize into.</typeparam>
    /// <param name="rawPayloadData">The raw payload data object received within an IncomingMessage&lt;TData&gt;.D field.</param>
    /// <returns>The deserialized payload object, or default if null or deserialization fails.</returns>
    TPayload? DeserializePayload<TPayload>(object? rawPayloadData)
        where TPayload : class;

    /// <summary>
    /// Deserializes the raw payload data into a specific target value type.
    /// </summary>
    /// <typeparam name="TPayload">The target value type to deserialize into.</typeparam>
    /// <param name="rawPayloadData">The raw payload data object received within an IncomingMessage&lt;TData&gt;.D field.</param>
    /// <returns>The deserialized payload value, or default if null or deserialization fails.</returns>
    TPayload? DeserializeValuePayload<TPayload>(object? rawPayloadData)
        where TPayload : struct;
}
