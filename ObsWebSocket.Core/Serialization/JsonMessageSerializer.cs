using System.Text;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc/>
    public string ProtocolSubProtocol => "obswebsocket.json";

    /// <inheritdoc/>
    public async Task<byte[]> SerializeAsync<T>(
        OutgoingMessage<T> message,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(message);
        using MemoryStream memoryStream = new();
        try
        {
            await JsonSerializer
                .SerializeAsync(memoryStream, message, s_jsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return memoryStream.ToArray();
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
            IncomingMessage<JsonElement>? message = await JsonSerializer
                .DeserializeAsync<IncomingMessage<JsonElement>>(
                    messageStream,
                    s_jsonOptions,
                    cancellationToken
                )
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
            return jsonElement.Deserialize<TPayload>(s_jsonOptions);
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
            return jsonElement.Deserialize<TPayload>(s_jsonOptions);
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
