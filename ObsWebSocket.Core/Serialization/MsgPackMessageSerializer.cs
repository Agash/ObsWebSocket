using MessagePack;
using MessagePack.Resolvers;
using Microsoft.Extensions.Logging;
using ObsWebSocket.Core.Protocol;

namespace ObsWebSocket.Core.Serialization;

/// <summary>
/// Serializes and deserializes WebSocket messages using MessagePack-CSharp.
/// </summary>
/// <param name="logger">The logger instance.</param>
public class MsgPackMessageSerializer(ILogger<MsgPackMessageSerializer> logger)
    : IWebSocketMessageSerializer
{
    private readonly ILogger _logger = logger;

    // Configure MessagePack options.
    // ContractlessStandardResolverAllowPrivate: Allows deserialization without explicit attributes and works with record primary constructors.
    private static readonly MessagePackSerializerOptions s_msgPackOptions =
        ContractlessStandardResolverAllowPrivate.Options;

    // Optional: Consider adding compression if needed and supported by OBS (check compatibility)
    // private static readonly MessagePackSerializerOptions s_msgPackOptions = ContractlessStandardResolverAllowPrivate.Options
    //     .WithCompression(MessagePackCompression.Lz4BlockArray);

    /// <inheritdoc/>
    public string ProtocolSubProtocol => "obswebsocket.msgpack";

    /// <inheritdoc/>
    public Task<byte[]> SerializeAsync<T>(
        OutgoingMessage<T> message,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            byte[] serializedData = MessagePackSerializer.Serialize(
                message,
                s_msgPackOptions,
                cancellationToken
            );
            return Task.FromResult(serializedData);
        }
        catch (MessagePackSerializationException ex)
        {
            _logger.LogError(
                ex,
                "MessagePack serialization failed for message with OpCode {OpCode}",
                message.Op
            );
            throw new ObsWebSocketException("MessagePack serialization error", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error during MessagePack serialization for OpCode {OpCode}",
                message.Op
            );
            throw new ObsWebSocketException("Unexpected serialization error", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<object?> DeserializeAsync(
        Stream messageStream,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(messageStream);
        if (!messageStream.CanRead)
        {
            throw new ArgumentException("Stream must be readable.", nameof(messageStream));
        }

        if (messageStream.Length == 0)
        {
            _logger.LogWarning("Attempted to deserialize an empty message stream.");
            return null;
        }

        try
        {
            // Deserialize using 'object' as the placeholder type for the payload 'd'.
            // With ContractlessStandardResolver, this typically results in nested Dictionary<object, object>, List<object>, or primitive types.
            IncomingMessage<object>? message = await MessagePackSerializer
                .DeserializeAsync<IncomingMessage<object>>(
                    messageStream,
                    s_msgPackOptions,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (message is null)
            {
                _logger.LogWarning("MessagePack deserialization resulted in null.");
            }
            else if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Deserialized MessagePack message: Op={Op}", message.Op);
            }

            return message;
        }
        catch (MessagePackSerializationException ex)
        {
            // Difficult to log problematic MessagePack data directly compared to JSON.
            _logger.LogError(ex, "MessagePack deserialization failed.");
            // Consider reading stream to byte array here for logging if absolutely needed, but can be large.
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize message from stream.");
            return null;
        }
    }

    /// <inheritdoc/>
    public TPayload? DeserializePayload<TPayload>(object? rawPayloadData)
        where TPayload : class
    {
        if (rawPayloadData is null)
        {
            return default;
        }

        try
        {
            // ContractlessStandardResolver should handle converting from the underlying object/dictionary
            // structure back to the target TPayload type if the structure is compatible or TPayload
            // uses attributes recognized by the resolver (like [Key]).
            // If direct conversion fails, serializing then deserializing is a fallback.
            return MessagePackSerializer.Deserialize<TPayload>(
                MessagePackSerializer.Serialize(rawPayloadData, s_msgPackOptions),
                s_msgPackOptions
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "MessagePack failed to deserialize payload object to {TargetType}. Object Type: {ObjectType}",
                typeof(TPayload).Name,
                rawPayloadData.GetType().Name
            );
            return default;
        }
    }

    /// <inheritdoc/>
    public TPayload? DeserializeValuePayload<TPayload>(object? rawPayloadData)
        where TPayload : struct
    {
        if (rawPayloadData is null)
        {
            return default;
        }

        try
        {
            // Similar logic as above for value types
            return MessagePackSerializer.Deserialize<TPayload>(
                MessagePackSerializer.Serialize(rawPayloadData, s_msgPackOptions),
                s_msgPackOptions
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "MessagePack failed to deserialize payload object to value type {TargetType}. Object Type: {ObjectType}",
                typeof(TPayload).Name,
                rawPayloadData.GetType().Name
            );
            return default;
        }
    }
}
