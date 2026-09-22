using System.Text.Json.Serialization.Metadata;
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
    /// Deserializes an incoming message that has already been assembled in memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the shape the WebSocket transport actually produces: the receive loop reassembles
    /// a message's fragments into one contiguous buffer before anything can be parsed, because
    /// the envelope is not readable until the last fragment has arrived. Handing that buffer
    /// straight to the serializer avoids wrapping it in a stream only for the serializer to copy
    /// it back out again, which on the MessagePack path meant three full copies of every message
    /// before a single byte was decoded.
    /// </para>
    /// <para>
    /// The default implementation adapts to <see cref="DeserializeAsync(Stream, CancellationToken)"/>,
    /// so an existing serializer outside this library keeps working; the two built in override it.
    /// </para>
    /// </remarks>
    /// <param name="message">The complete message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deserialized message, or <see langword="null"/> if it could not be read.</returns>
    ValueTask<object?> DeserializeAsync(
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken = default
    )
    {
        return Adapt(this, message, cancellationToken);

        static async ValueTask<object?> Adapt(
            IWebSocketMessageSerializer serializer,
            ReadOnlyMemory<byte> message,
            CancellationToken cancellationToken
        )
        {
            using MemoryStream stream = new(message.ToArray(), writable: false);
            return await serializer
                .DeserializeAsync(stream, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deserializes the raw payload data (e.g., JsonElement, object from MessagePack) into a
    /// specific target type, throwing when the payload cannot be read.
    /// </summary>
    /// <remarks>
    /// Use this on paths where a caller is awaiting a result. A payload that is absent still
    /// returns <see langword="null"/>, because many requests answer with no data at all; only a
    /// payload that is present and unreadable raises.
    /// </remarks>
    /// <typeparam name="TPayload">The target type to deserialize into.</typeparam>
    /// <param name="rawPayloadData">The raw payload data object received within an IncomingMessage&lt;TData&gt;.D field.</param>
    /// <returns>The deserialized payload object, or <see langword="null"/> if no payload was present.</returns>
    /// <exception cref="ObsWebSocketSerializationException">
    /// Thrown when a payload is present but cannot be deserialized into <typeparamref name="TPayload"/>.
    /// </exception>
    /// <param name="typeInfo">
    /// Metadata for <typeparamref name="TPayload"/>, for a type this library does not know.
    /// <see langword="null"/> resolves it from this library's own context. A format that does not
    /// read JSON metadata ignores it.
    /// </param>
    TPayload? DeserializePayload<TPayload>(
        object? rawPayloadData,
        JsonTypeInfo<TPayload>? typeInfo = null
    )
        where TPayload : class;

    /// <summary>
    /// Deserializes the raw payload data into a specific target value type, throwing when the
    /// payload cannot be read.
    /// </summary>
    /// <typeparam name="TPayload">The target value type to deserialize into.</typeparam>
    /// <param name="rawPayloadData">The raw payload data object received within an IncomingMessage&lt;TData&gt;.D field.</param>
    /// <returns>The deserialized payload value, or <see langword="null"/> if no payload was present.</returns>
    /// <exception cref="ObsWebSocketSerializationException">
    /// Thrown when a payload is present but cannot be deserialized into <typeparamref name="TPayload"/>.
    /// </exception>
    /// <param name="typeInfo">
    /// Metadata for <typeparamref name="TPayload"/>, for a type this library does not know.
    /// <see langword="null"/> resolves it from this library's own context. A format that does not
    /// read JSON metadata ignores it.
    /// </param>
    TPayload? DeserializeValuePayload<TPayload>(
        object? rawPayloadData,
        JsonTypeInfo<TPayload>? typeInfo = null
    )
        where TPayload : struct;

    /// <summary>
    /// Deserializes the raw payload data, reporting failure instead of throwing.
    /// </summary>
    /// <remarks>
    /// Use this on the receive loop. A newer OBS sending an event this build cannot model, or a
    /// wrapper that arrives malformed, must not tear the connection down, so the failure is
    /// logged and the message dropped.
    /// </remarks>
    /// <typeparam name="TPayload">The target type to deserialize into.</typeparam>
    /// <param name="rawPayloadData">The raw payload data object received within an IncomingMessage&lt;TData&gt;.D field.</param>
    /// <param name="payload">The deserialized payload, or <see langword="null"/> on failure.</param>
    /// <returns><see langword="true"/> when a payload was read; otherwise <see langword="false"/>.</returns>
    bool TryDeserializePayload<TPayload>(object? rawPayloadData, out TPayload? payload)
        where TPayload : class;

    /// <summary>
    /// Deserializes the raw payload data into a value type, reporting failure instead of throwing.
    /// </summary>
    /// <typeparam name="TPayload">The target value type to deserialize into.</typeparam>
    /// <param name="rawPayloadData">The raw payload data object received within an IncomingMessage&lt;TData&gt;.D field.</param>
    /// <param name="payload">The deserialized payload, or <see langword="null"/> on failure.</param>
    /// <returns><see langword="true"/> when a payload was read; otherwise <see langword="false"/>.</returns>
    bool TryDeserializeValuePayload<TPayload>(object? rawPayloadData, out TPayload? payload)
        where TPayload : struct;
}
