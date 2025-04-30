using System.Text.Json;
using System.Text.Json.Serialization;
using ObsWebSocket.Core.Protocol.Generated;

namespace ObsWebSocket.Core.Protocol;

/// <summary>
/// Represents the structure of an outgoing message sent to the OBS WebSocket server.
/// </summary>
/// <typeparam name="T">The type of the data payload.</typeparam>
/// <param name="Op">The operation code.</param>
/// <param name="D">The data payload.</param>
public record OutgoingMessage<T>(
    [property: JsonPropertyName("op")] WebSocketOpCode Op,
    [property: JsonPropertyName("d")] T D
);

/// <summary>
/// Represents the base structure of an incoming message received from the OBS WebSocket server.
/// The payload type 'TData' depends on the serializer (e.g., JsonElement for JSON, object/Dictionary for MessagePack).
/// </summary>
/// <typeparam name="TData">The type representing the raw data payload 'd'.</typeparam>
/// <param name="Op">The operation code.</param>
/// <param name="D">The raw data payload.</param>
public record IncomingMessage<TData>(
    [property: JsonPropertyName("op")] WebSocketOpCode Op,
    [property: JsonPropertyName("d")] TData D
);

// --- Handshake Payloads (Internal) ---
internal record HelloPayload(
    [property: JsonPropertyName("obsWebSocketVersion")] string ObsWebSocketVersion,
    [property: JsonPropertyName("rpcVersion")] int RpcVersion,
    [property: JsonPropertyName("authentication")] AuthenticationData? Authentication
);

internal record AuthenticationData(
    [property: JsonPropertyName("challenge")] string Challenge,
    [property: JsonPropertyName("salt")] string Salt
);

internal record IdentifyPayload(
    [property: JsonPropertyName("rpcVersion")] int RpcVersion,
    [property: JsonPropertyName("authentication")] string? Authentication = null,
    [property: JsonPropertyName("eventSubscriptions")] uint EventSubscriptions = 0
);

internal record IdentifiedPayload(
    [property: JsonPropertyName("negotiatedRpcVersion")] int NegotiatedRpcVersion
);

internal record ReidentifyPayload(
    [property:
        JsonPropertyName("eventSubscriptions"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)
    ]
        uint? EventSubscriptions = null
);

// --- Request/Response Payloads ---
internal record RequestPayload(
    [property: JsonPropertyName("requestType")] string RequestType,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property:
        JsonPropertyName("requestData"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)
    ]
        JsonElement? RequestData = null // Still JsonElement for outgoing JSON prep
);

/// <summary>
/// Payload data for a RequestResponse message (OpCode 7) and used as items within RequestBatchResponse (OpCode 9).
/// Needs to be public because it's returned by CallBatchAsync. TData depends on the serializer.
/// </summary>
/// <typeparam name="TData">The type representing the raw response data payload 'd' (depends on serializer).</typeparam>
/// <param name="RequestType">The original request type string.</param>
/// <param name="RequestId">The original request identifier string.</param>
/// <param name="RequestStatus">The status of the request processing.</param>
/// <param name="ResponseData">The raw response data payload, or default if none.</param>
public record RequestResponsePayload<TData>(
    [property: JsonPropertyName("requestType")] string RequestType,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("requestStatus")] RequestStatus RequestStatus,
    [property:
        JsonPropertyName("responseData"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)
    ]
        TData? ResponseData = default
);

/// <summary>
/// Represents the status of an individual request processed by the OBS WebSocket server.
/// </summary>
/// <param name="Result">Indicates whether the request succeeded.</param>
/// <param name="Code">The OBS WebSocket status code (<see cref="RequestStatus"/> enum). </param>
/// <param name="Comment">Optional comment provided by OBS for context, especially on failure.</param>
public record RequestStatus(
    [property: JsonPropertyName("result")] bool Result,
    [property: JsonPropertyName("code")] int Code,
    [property:
        JsonPropertyName("comment"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)
    ]
        string? Comment = null
);

// --- Event Payloads (Internal) ---

/// <summary>
/// Represents the base structure of an incoming event message's data payload 'd'.
/// </summary>
/// <typeparam name="TData">The type representing the specific event data payload (depends on serializer).</typeparam>
/// <param name="EventType">The unique event type string.</param>
/// <param name="EventIntent">Bitmask indicating the subscription intents that triggered this event.</param>
/// <param name="EventData">The specific data associated with the event, or default if none.</param>
public record EventPayloadBase<TData>(
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("eventIntent")] int EventIntent,
    [property:
        JsonPropertyName("eventData"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)
    ]
        TData? EventData = default
);

// --- Batch Request/Response Payloads ---

/// <summary>
/// Represents a single request item within a batch request sent via <see cref="ObsWebSocketClient.CallBatchAsync"/>.
/// </summary>
/// <param name="RequestType">The OBS WebSocket request type string (e.g., "GetVersion", "SetCurrentProgramScene"). Cannot be null or empty.</param>
/// <param name="RequestData">Optional data payload for this specific request within the batch. The object provided should be serializable to the format expected by OBS for the <paramref name="RequestType"/>.</param>
public record BatchRequestItem(string RequestType, object? RequestData = null);

internal record RequestBatchPayload(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property:
        JsonPropertyName("haltOnFailure"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)
    ]
        bool? HaltOnFailure,
    [property:
        JsonPropertyName("executionType"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)
    ]
        RequestBatchExecutionType? ExecutionType,
    [property: JsonPropertyName("requests")] List<RequestPayload> Requests
);

internal record RequestBatchResponsePayload<TData>(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("results")] List<RequestResponsePayload<TData>> Results
);
