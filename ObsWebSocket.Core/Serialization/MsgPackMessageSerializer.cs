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

    internal static readonly MessagePackSerializerOptions s_msgPackOptions =
        MessagePackSerializerOptions
            .Standard.WithResolver(CompositeResolver.Create(CreateResolverChain()))
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
            byte[] serializedData = SerializeOutgoingEnvelope(message, cancellationToken);
            return Task.FromResult(serializedData);
        }
        catch (MessagePackSerializationException ex)
        {
            _logger.LogMessagepackSerializationFailedForMessageWithOpcode(ex, message.Op);
            throw new ObsWebSocketSerializationException("MessagePack serialization error", ex);
        }
        catch (Exception ex)
        {
            _logger.LogUnexpectedErrorDuringMessagepackSerializationForOpcode(ex, message.Op);
            throw new ObsWebSocketSerializationException("Unexpected serialization error", ex);
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
            _logger.LogAttemptedToDeserializeAnEmptyMessageStream();
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
                _logger.LogDeserializedMessagepackMessageOp(message.Op);
            }

            return message;
        }
        catch (MessagePackSerializationException ex)
        {
            _logger.LogMessagepackDeserializationFailed(ex);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogFailedToDeserializeMessageFromStream(ex);
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
            if (typeof(TPayload) == typeof(EventPayloadBase<object>))
            {
                return (TPayload)(object)DeserializeEventPayloadBase(raw);
            }

            if (typeof(TPayload) == typeof(RequestResponsePayload<object>))
            {
                MessagePackReader wrapperReader = new(raw);
                return (TPayload)(object)DeserializeRequestResponsePayload(ref wrapperReader);
            }

            return typeof(TPayload) == typeof(RequestBatchResponsePayload<object>)
                ? (TPayload)(object)DeserializeRequestBatchResponsePayload(raw)
                : MessagePackSerializer.Deserialize<TPayload>(raw, s_msgPackOptions);
        }
        catch (Exception ex)
        {
            _logger.LogMessagepackFailedToDeserializePayloadObjectTo(ex, typeof(TPayload).Name, rawPayloadData.GetType().Name);
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
            _logger.LogMessagepackFailedToDeserializePayloadObjectTo2(ex, typeof(TPayload).Name, rawPayloadData.GetType().Name);
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

    private static byte[] SerializeOutgoingEnvelope<T>(
        OutgoingMessage<T> message,
        CancellationToken cancellationToken
    )
    {
        ArrayBufferWriter<byte> buffer = new();
        MessagePackWriter writer = new(buffer);

        writer.WriteMapHeader(2);
        writer.Write("op");
        writer.Write((int)message.Op);
        writer.Write("d");

        if (message.D is RequestBatchPayload batchPayload)
        {
            SerializeRequestBatchPayload(ref writer, batchPayload, cancellationToken);
            writer.Flush();
            return [.. buffer.WrittenSpan];
        }

        byte[] payloadBytes = MessagePackSerializer.Serialize(message.D, s_msgPackOptions, cancellationToken);
        writer.WriteRaw(payloadBytes);
        writer.Flush();

        return [.. buffer.WrittenSpan];
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

    private static EventPayloadBase<object> DeserializeEventPayloadBase(ReadOnlyMemory<byte> raw)
    {
        MessagePackReader reader = new(raw);
        int count = reader.ReadMapHeader();

        string? eventType = null;
        int eventIntent = 0;
        object? eventData = null;

        for (int i = 0; i < count; i++)
        {
            string? key = reader.ReadString();
            switch (key)
            {
                case "eventType":
                    eventType = reader.ReadString();
                    break;
                case "eventIntent":
                    eventIntent = reader.ReadInt32();
                    break;
                case "eventData":
                    eventData = ReadRawValue(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new EventPayloadBase<object>(eventType ?? string.Empty, eventIntent, eventData);
    }

    private static IFormatterResolver[] CreateResolverChain() => [
            MsgPackJsonElementResolver.Instance,
            MsgPackStubExtensionDataResolver.Instance,
            ObsWebSocketMsgPackResolver.Instance,
            BuiltinResolver.Instance,
            SourceGeneratedFormatterResolver.Instance,
            PrimitiveObjectResolver.Instance,
        ];

    private static RequestBatchResponsePayload<object> DeserializeRequestBatchResponsePayload(
        ReadOnlyMemory<byte> raw
    )
    {
        MessagePackReader reader = new(raw);
        int count = reader.ReadMapHeader();

        string? requestId = null;
        List<RequestResponsePayload<object>> results = [];

        for (int i = 0; i < count; i++)
        {
            string? key = reader.ReadString();
            switch (key)
            {
                case "requestId":
                    requestId = reader.ReadString();
                    break;
                case "results":
                {
                    int resultCount = reader.ReadArrayHeader();
                    for (int r = 0; r < resultCount; r++)
                    {
                        // Each result is sliced out and parsed on its own reader. Reading them
                        // from the shared reader let one result's payload slice run on into the
                        // next, which paired every response with the following request.
                        ReadOnlyMemory<byte> resultRaw = ReadRawValue(ref reader);
                        MessagePackReader resultReader = new(resultRaw);
                        results.Add(DeserializeRequestResponsePayload(ref resultReader));
                    }

                    break;
                }
                default:
                    reader.Skip();
                    break;
            }
        }

        return new RequestBatchResponsePayload<object>(requestId ?? string.Empty, results);
    }

    private static void SerializeRequestBatchPayload(
        ref MessagePackWriter writer,
        RequestBatchPayload payload,
        CancellationToken cancellationToken
    )
    {
        writer.WriteMapHeader(4);
        writer.Write("requestId");
        writer.Write(payload.RequestId);
        writer.Write("haltOnFailure");
        if (payload.HaltOnFailure.HasValue)
        {
            writer.Write(payload.HaltOnFailure.Value);
        }
        else
        {
            writer.WriteNil();
        }

        writer.Write("executionType");
        if (payload.ExecutionType.HasValue)
        {
            writer.Write((int)payload.ExecutionType.Value);
        }
        else
        {
            writer.WriteNil();
        }

        writer.Write("requests");
        writer.WriteArrayHeader(payload.Requests.Count);
        foreach (RequestPayload request in payload.Requests)
        {
            SerializeRequestPayload(ref writer, request, cancellationToken);
        }
    }

    private static void SerializeRequestPayload(
        ref MessagePackWriter writer,
        RequestPayload request,
        CancellationToken cancellationToken
    )
    {
        writer.WriteMapHeader(3);
        writer.Write("requestType");
        writer.Write(request.RequestType);
        writer.Write("requestId");
        writer.Write(request.RequestId);
        writer.Write("requestData");
        if (request.RequestData.HasValue)
        {
            byte[] raw = MessagePackSerializer.Serialize(
                request.RequestData.Value,
                s_msgPackOptions,
                cancellationToken
            );
            writer.WriteRaw(raw);
        }
        else
        {
            writer.WriteNil();
        }
    }

    private static RequestResponsePayload<object> DeserializeRequestResponsePayload(
        ref MessagePackReader reader
    )
    {
        int count = reader.ReadMapHeader();

        string? requestType = null;
        string? requestId = null;
        Protocol.RequestStatus requestStatus = new(false, 0, "Missing requestStatus");
        object? responseData = null;

        for (int i = 0; i < count; i++)
        {
            string? key = reader.ReadString();
            switch (key)
            {
                case "requestType":
                    requestType = reader.ReadString();
                    break;
                case "requestId":
                    requestId = reader.ReadString();
                    break;
                case "requestStatus":
                {
                    ReadOnlyMemory<byte> statusRaw = ReadRawValue(ref reader);
                    requestStatus = MessagePackSerializer.Deserialize<Protocol.RequestStatus>(
                        statusRaw,
                        s_msgPackOptions
                    );
                    break;
                }
                case "responseData":
                    responseData = ReadRawValue(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new RequestResponsePayload<object>(
            requestType ?? string.Empty,
            requestId ?? string.Empty,
            requestStatus,
            responseData
        );
    }
}
