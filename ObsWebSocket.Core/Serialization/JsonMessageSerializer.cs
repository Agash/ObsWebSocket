using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using ObsWebSocket.Core.Protocol;

namespace ObsWebSocket.Core.Serialization;

/// <summary>
/// Serializes and deserializes WebSocket messages using System.Text.Json.
/// </summary>
/// <param name="logger">The logger instance.</param>
public class JsonMessageSerializer(ILogger<JsonMessageSerializer> logger)
    : IWebSocketMessageSerializer
{
    private readonly ILogger _logger = logger;
    private static readonly JsonSerializerOptions s_options = ObsWebSocketJsonContext.Default.Options;

    /// <inheritdoc/>
    public string ProtocolSubProtocol => "obswebsocket.json";

    /// <inheritdoc/>
    public Task<byte[]> SerializeAsync<T>(
        OutgoingMessage<T> message,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        using MemoryStream memoryStream = new();
        try
        {
            JsonTypeInfo<OutgoingMessage<T>> typeInfo =
                (JsonTypeInfo<OutgoingMessage<T>>)s_options.GetTypeInfo(typeof(OutgoingMessage<T>));
            JsonSerializer.Serialize(memoryStream, message, typeInfo);
            return Task.FromResult(memoryStream.ToArray());
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            _logger.LogError(
                ex,
                "JSON serialization failed for message with OpCode {OpCode}",
                message.Op
            );
            throw new ObsWebSocketException("Serialization error", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<object?> DeserializeAsync(
        Stream messageStream,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(messageStream);
        if (messageStream.Length == 0)
        {
            _logger.LogWarning("Attempted to deserialize an empty message stream.");
            return null;
        }

        try
        {
            // Deserialize into the generic IncomingMessage with JsonElement as the data type
            JsonTypeInfo<IncomingMessage<JsonElement>> typeInfo =
                (JsonTypeInfo<IncomingMessage<JsonElement>>)s_options.GetTypeInfo(
                    typeof(IncomingMessage<JsonElement>)
                );
            IncomingMessage<JsonElement>? message = await JsonSerializer
                .DeserializeAsync(messageStream, typeInfo, cancellationToken)
                .ConfigureAwait(false);

            if (message is null)
            {
                _logger.LogWarning("JSON deserialization resulted in null.");
            }
            else if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Deserialized JSON message: Op={Op}", message.Op);
            } // Avoid logging potentially large payload

            return message;
        }
        catch (JsonException ex)
        {
            messageStream.Position = 0;
            using StreamReader reader = new(messageStream, Encoding.UTF8, leaveOpen: true);
            string rawJson = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError(
                ex,
                "JSON deserialization failed. Raw JSON: {RawJson}",
                rawJson.Length > 1024 ? rawJson[..1024] + "..." : rawJson
            );
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
        if (
            rawPayloadData is not JsonElement jsonElement
            || jsonElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        )
        {
            if (
                rawPayloadData
                is not null
                    and not JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }
            )
            {
                _logger.LogWarning(
                    "JSON Deserializer expected JsonElement payload but received {DataType} for {TargetType}.",
                    rawPayloadData?.GetType().Name,
                    typeof(TPayload).Name
                );
            }

            return default;
        }

        try
        {
            if (typeof(TPayload) == typeof(EventPayloadBase<object>))
            {
                JsonTypeInfo<EventPayloadBase<JsonElement>> eventTypeInfo =
                    (JsonTypeInfo<EventPayloadBase<JsonElement>>)s_options.GetTypeInfo(
                        typeof(EventPayloadBase<JsonElement>)
                    );
                EventPayloadBase<JsonElement>? eventPayload = jsonElement.Deserialize(eventTypeInfo);
                if (eventPayload is null)
                {
                    return default;
                }

                return (TPayload)
                    (object)
                        new EventPayloadBase<object>(
                            eventPayload.EventType,
                            eventPayload.EventIntent,
                            eventPayload.EventData
                        );
            }

            if (typeof(TPayload) == typeof(RequestResponsePayload<object>))
            {
                JsonTypeInfo<RequestResponsePayload<JsonElement>> responseTypeInfo =
                    (JsonTypeInfo<RequestResponsePayload<JsonElement>>)s_options.GetTypeInfo(
                        typeof(RequestResponsePayload<JsonElement>)
                    );
                RequestResponsePayload<JsonElement>? responsePayload =
                    jsonElement.Deserialize(responseTypeInfo);
                if (responsePayload is null)
                {
                    return default;
                }

                return (TPayload)
                    (object)
                        new RequestResponsePayload<object>(
                            responsePayload.RequestType,
                            responsePayload.RequestId,
                            responsePayload.RequestStatus,
                            responsePayload.ResponseData
                        );
            }

            if (typeof(TPayload) == typeof(RequestBatchResponsePayload<object>))
            {
                JsonTypeInfo<RequestBatchResponsePayload<JsonElement>> batchTypeInfo =
                    (JsonTypeInfo<RequestBatchResponsePayload<JsonElement>>)s_options.GetTypeInfo(
                        typeof(RequestBatchResponsePayload<JsonElement>)
                    );
                RequestBatchResponsePayload<JsonElement>? batchPayload =
                    jsonElement.Deserialize(batchTypeInfo);
                if (batchPayload is null)
                {
                    return default;
                }

                List<RequestResponsePayload<object>> mappedResults =
                [
                    .. batchPayload.Results.Select(result => new RequestResponsePayload<object>(
                        result.RequestType,
                        result.RequestId,
                        result.RequestStatus,
                        result.ResponseData
                    )),
                ];

                return (TPayload)
                    (object)new RequestBatchResponsePayload<object>(
                        batchPayload.RequestId,
                        mappedResults
                    );
            }

            JsonTypeInfo<TPayload> typeInfo = (JsonTypeInfo<TPayload>)s_options.GetTypeInfo(
                typeof(TPayload)
            );
            return jsonElement.Deserialize(typeInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "JSON failed to deserialize payload to {TargetType}. Raw JSON: {Json}",
                typeof(TPayload).Name,
                jsonElement.GetRawText()
            );
            return default;
        }
    }

    /// <inheritdoc/>
    public TPayload? DeserializeValuePayload<TPayload>(object? rawPayloadData)
        where TPayload : struct
    {
        if (
            rawPayloadData is not JsonElement jsonElement
            || jsonElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        )
        {
            if (
                rawPayloadData
                is not null
                    and not JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }
            )
            {
                _logger.LogWarning(
                    "JSON Deserializer expected JsonElement payload but received {DataType} for value type {TargetType}.",
                    rawPayloadData?.GetType().Name,
                    typeof(TPayload).Name
                );
            }

            return default;
        }

        try
        {
            // Deserialize will return default(TPayload) if JSON is null, which is valid for nullable structs,
            // but might be undesirable for non-nullable ones (though caught earlier if JSON is explicitly null).
            JsonTypeInfo<TPayload> typeInfo = (JsonTypeInfo<TPayload>)s_options.GetTypeInfo(
                typeof(TPayload)
            );
            return jsonElement.Deserialize(typeInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "JSON failed to deserialize payload to value type {TargetType}. Raw JSON: {Json}",
                typeof(TPayload).Name,
                jsonElement.GetRawText()
            );
            return default;
        }
    }
}
