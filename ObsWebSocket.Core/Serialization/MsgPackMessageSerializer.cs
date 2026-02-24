using System.Buffers;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.Extensions.Logging;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Generated;

namespace ObsWebSocket.Core.Serialization;

/// <summary>
/// Serializes and deserializes WebSocket messages using MessagePack-CSharp.
/// </summary>
/// <param name="logger">The logger instance.</param>
public class MsgPackMessageSerializer(ILogger<MsgPackMessageSerializer> logger)
    : IWebSocketMessageSerializer
{
    private readonly ILogger _logger = logger;

    private static readonly MessagePackSerializerOptions s_msgPackOptions =
        MessagePackSerializerOptions
            .Standard.WithResolver(
                CompositeResolver.Create(
                    MsgPackJsonElementResolver.Instance,
                    MsgPackStubExtensionDataResolver.Instance,
                    MessagePack.GeneratedMessagePackResolver.Instance,
                    StandardResolver.Instance
                )
            )
            .WithSecurity(MessagePackSecurity.UntrustedData);

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
            await using MemoryStream buffer = new();
            await messageStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            IncomingMessage<ReadOnlyMemory<byte>> message = DeserializeIncomingEnvelope(
                buffer.ToArray()
            );

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Deserialized MessagePack message: Op={Op}", message.Op);
            }

            return message;
        }
        catch (MessagePackSerializationException ex)
        {
            _logger.LogError(ex, "MessagePack deserialization failed.");
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
        if (rawPayloadData is not ReadOnlyMemory<byte> raw)
        {
            return default;
        }

        try
        {
            return MessagePackSerializer.Deserialize<TPayload>(raw, s_msgPackOptions);
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
        if (rawPayloadData is not ReadOnlyMemory<byte> raw)
        {
            return default;
        }

        try
        {
            return MessagePackSerializer.Deserialize<TPayload>(raw, s_msgPackOptions);
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

    private static IncomingMessage<ReadOnlyMemory<byte>> DeserializeIncomingEnvelope(
        ReadOnlyMemory<byte> payload
    )
    {
        MessagePackReader reader = new(payload);
        int count = reader.ReadMapHeader();
        WebSocketOpCode op = default;
        ReadOnlyMemory<byte> data = default;

        for (int i = 0; i < count; i++)
        {
            string? key = reader.ReadString();
            if (key == "op")
            {
                op = (WebSocketOpCode)reader.ReadInt32();
                continue;
            }

            if (key == "d")
            {
                data = ReadRawValue(ref reader);
                continue;
            }

            reader.Skip();
        }

        return new IncomingMessage<ReadOnlyMemory<byte>>(op, data);
    }

    private static ReadOnlyMemory<byte> ReadRawValue(ref MessagePackReader reader)
    {
        MessagePackReader clone = reader;
        clone.Skip();
        ReadOnlySequence<byte> sequence = reader.Sequence.Slice(reader.Position, clone.Position);
        byte[] raw = new byte[checked((int)sequence.Length)];
        sequence.CopyTo(raw);
        reader = clone;
        return raw;
    }
}
