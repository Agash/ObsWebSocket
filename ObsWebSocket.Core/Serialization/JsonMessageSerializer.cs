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
    private const int RawTextLimit = 512;

    private readonly ILogger _logger = logger;
    private static readonly JsonSerializerOptions s_options = ObsWebSocketJsonContext
        .Default
        .Options;

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
            _logger.LogJsonSerializationFailedForMessageWithOpcode(ex, message.Op);
            throw new ObsWebSocketSerializationException("Serialization error", ex);
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
            _logger.LogAttemptedToDeserializeAnEmptyMessageStream();
            return null;
        }

        try
        {
            // Deserialize into the generic IncomingMessage with JsonElement as the data type
            JsonTypeInfo<IncomingMessage<JsonElement>> typeInfo =
                (JsonTypeInfo<IncomingMessage<JsonElement>>)
                    s_options.GetTypeInfo(typeof(IncomingMessage<JsonElement>));
            IncomingMessage<JsonElement>? message = await JsonSerializer
                .DeserializeAsync(messageStream, typeInfo, cancellationToken)
                .ConfigureAwait(false);

            if (message is null)
            {
                _logger.LogJsonDeserializationResultedInNull();
            }
            else
            {
                // The payload is read after the document it was parsed from is gone, so it has
                // to own its data rather than point into that document.
                message = new IncomingMessage<JsonElement>(message.Op, message.D.Clone());

                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogDeserializedJsonMessageOp(message.Op);
                } // Avoid logging potentially large payload
            }

            return message;
        }
        catch (JsonException ex)
        {
            messageStream.Position = 0;
            using StreamReader reader = new(messageStream, Encoding.UTF8, leaveOpen: true);
            string rawJson = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogJsonDeserializationFailedRawJson(
                ex,
                rawJson.Length > 1024 ? rawJson[..1024] + "..." : rawJson
            );
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
        where TPayload : class => DeserializePayloadCore<TPayload>(rawPayloadData);

    /// <inheritdoc/>
    public bool TryDeserializePayload<TPayload>(object? rawPayloadData, out TPayload? payload)
        where TPayload : class
    {
        try
        {
            payload = DeserializePayloadCore<TPayload>(rawPayloadData);
            return payload is not null;
        }
        catch (ObsWebSocketSerializationException ex)
        {
            _logger.LogJsonFailedToDeserializePayloadToRaw(
                ex,
                typeof(TPayload).Name,
                RawTextOf(rawPayloadData)
            );
            payload = default;
            return false;
        }
    }

    private TPayload? DeserializePayloadCore<TPayload>(object? rawPayloadData)
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
                _logger.LogJsonDeserializerExpectedJsonelementPayloadButReceived(
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
                    (JsonTypeInfo<EventPayloadBase<JsonElement>>)
                        s_options.GetTypeInfo(typeof(EventPayloadBase<JsonElement>));
                EventPayloadBase<JsonElement>? eventPayload = jsonElement.Deserialize(
                    eventTypeInfo
                );
                return eventPayload is null
                    ? default
                    : (TPayload)
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
                    (JsonTypeInfo<RequestResponsePayload<JsonElement>>)
                        s_options.GetTypeInfo(typeof(RequestResponsePayload<JsonElement>));
                RequestResponsePayload<JsonElement>? responsePayload = jsonElement.Deserialize(
                    responseTypeInfo
                );
                return responsePayload is null
                    ? default
                    : (TPayload)
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
                // Each result is deserialized on its own, because deserializing the batch in
                // one pass paired every responseData with the following request.
                JsonTypeInfo<RequestResponsePayload<JsonElement>> itemTypeInfo =
                    (JsonTypeInfo<RequestResponsePayload<JsonElement>>)
                        s_options.GetTypeInfo(typeof(RequestResponsePayload<JsonElement>));

                string batchRequestId =
                    jsonElement.TryGetProperty("requestId", out JsonElement idElement)
                    && idElement.ValueKind == JsonValueKind.String
                        ? idElement.GetString() ?? string.Empty
                        : string.Empty;

                List<RequestResponsePayload<object>> mappedResults = [];
                if (
                    jsonElement.TryGetProperty("results", out JsonElement resultsElement)
                    && resultsElement.ValueKind == JsonValueKind.Array
                )
                {
                    foreach (JsonElement itemElement in resultsElement.EnumerateArray())
                    {
                        RequestResponsePayload<JsonElement>? item = itemElement.Deserialize(
                            itemTypeInfo
                        );
                        if (item is null)
                        {
                            continue;
                        }

                        JsonElement data = itemElement.TryGetProperty(
                            "responseData",
                            out JsonElement dataElement
                        )
                            ? dataElement.Clone()
                            : default;

                        mappedResults.Add(
                            new RequestResponsePayload<object>(
                                item.RequestType,
                                item.RequestId,
                                item.RequestStatus,
                                data
                            )
                        );
                    }
                }

                return (TPayload)
                    (object)new RequestBatchResponsePayload<object>(batchRequestId, mappedResults);
            }

            JsonTypeInfo<TPayload> typeInfo =
                (JsonTypeInfo<TPayload>)s_options.GetTypeInfo(typeof(TPayload));
            return jsonElement.Deserialize(typeInfo);
        }
        catch (Exception ex) when (ex is not ObsWebSocketSerializationException)
        {
            throw new ObsWebSocketSerializationException(FailureMessage<TPayload>(jsonElement), ex);
        }
    }

    /// <inheritdoc/>
    public TPayload? DeserializeValuePayload<TPayload>(object? rawPayloadData)
        where TPayload : struct => DeserializeValuePayloadCore<TPayload>(rawPayloadData);

    /// <inheritdoc/>
    public bool TryDeserializeValuePayload<TPayload>(object? rawPayloadData, out TPayload? payload)
        where TPayload : struct
    {
        try
        {
            payload = DeserializeValuePayloadCore<TPayload>(rawPayloadData);
            return payload.HasValue;
        }
        catch (ObsWebSocketSerializationException ex)
        {
            _logger.LogJsonFailedToDeserializePayloadToValue(
                ex,
                typeof(TPayload).Name,
                RawTextOf(rawPayloadData)
            );
            payload = default;
            return false;
        }
    }

    private TPayload? DeserializeValuePayloadCore<TPayload>(object? rawPayloadData)
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
                _logger.LogJsonDeserializerExpectedJsonelementPayloadButReceived2(
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
            JsonTypeInfo<TPayload> typeInfo =
                (JsonTypeInfo<TPayload>)s_options.GetTypeInfo(typeof(TPayload));
            return jsonElement.Deserialize(typeInfo);
        }
        catch (Exception ex) when (ex is not ObsWebSocketSerializationException)
        {
            throw new ObsWebSocketSerializationException(FailureMessage<TPayload>(jsonElement), ex);
        }
    }

    /// <summary>
    /// Builds the message for a payload that could not be read, keeping enough of the raw JSON to
    /// identify it without pasting an entire scene list into an exception.
    /// </summary>
    private static string FailureMessage<TPayload>(JsonElement element)
    {
        string raw = element.GetRawText();
        if (raw.Length > RawTextLimit)
        {
            raw = string.Concat(raw.AsSpan(0, RawTextLimit), "...");
        }

        return $"Failed to deserialize the payload as '{typeof(TPayload).Name}'. Raw JSON: {raw}";
    }

    private static string RawTextOf(object? rawPayloadData) =>
        rawPayloadData is JsonElement element ? element.GetRawText() : string.Empty;
}
