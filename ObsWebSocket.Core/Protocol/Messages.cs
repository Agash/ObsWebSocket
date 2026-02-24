using System.Text.Json;
using System.Text.Json.Serialization;
using MessagePack;
using ObsWebSocket.Core.Protocol.Generated;

namespace ObsWebSocket.Core.Protocol;

/// <summary>
/// Represents the structure of an outgoing message sent to the OBS WebSocket server.
/// </summary>
/// <typeparam name="T">The type of the data payload.</typeparam>
/// <param name="Op">The operation code.</param>
/// <param name="D">The data payload.</param>
[MessagePackObject(SuppressSourceGeneration = true)]
public record OutgoingMessage<T>(
    [property: JsonPropertyName("op"), Key("op")] WebSocketOpCode Op,
    [property: JsonPropertyName("d"), Key("d")] T D
);

/// <summary>
/// Represents the base structure of an incoming message received from the OBS WebSocket server.
/// The payload type 'TData' depends on the serializer (e.g., JsonElement for JSON, object/Dictionary for MessagePack).
/// </summary>
/// <typeparam name="TData">The type representing the raw data payload 'd'.</typeparam>
/// <param name="Op">The operation code.</param>
/// <param name="D">The raw data payload.</param>
[MessagePackObject(SuppressSourceGeneration = true)]
public record IncomingMessage<TData>(
    [property: JsonPropertyName("op"), Key("op")] WebSocketOpCode Op,
    [property: JsonPropertyName("d"), Key("d")] TData D
);

// --- Handshake Payloads (Internal) ---
[MessagePackObject(AllowPrivate = true)]
internal record HelloPayload(
    [property: JsonPropertyName("obsWebSocketVersion"), Key("obsWebSocketVersion")]
        string ObsWebSocketVersion,
    [property: JsonPropertyName("rpcVersion"), Key("rpcVersion")] int RpcVersion,
    [property: JsonPropertyName("authentication"), Key("authentication")]
        AuthenticationData? Authentication
);

[MessagePackObject(AllowPrivate = true)]
internal record AuthenticationData(
    [property: JsonPropertyName("challenge"), Key("challenge")] string Challenge,
    [property: JsonPropertyName("salt"), Key("salt")] string Salt
);

[MessagePackObject(AllowPrivate = true)]
internal record IdentifyPayload(
    [property: JsonPropertyName("rpcVersion"), Key("rpcVersion")] int RpcVersion,
    [property: JsonPropertyName("authentication"), Key("authentication")]
        string? Authentication = null,
    [property: JsonPropertyName("eventSubscriptions"), Key("eventSubscriptions")]
        uint EventSubscriptions = 0
);

[MessagePackObject(AllowPrivate = true)]
internal record IdentifiedPayload(
    [property: JsonPropertyName("negotiatedRpcVersion"), Key("negotiatedRpcVersion")]
        int NegotiatedRpcVersion
);

[MessagePackObject(AllowPrivate = true)]
internal record ReidentifyPayload(
    [property:
        JsonPropertyName("eventSubscriptions"),
        Key("eventSubscriptions"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)
    ]
        uint? EventSubscriptions = null
);

// --- Request/Response Payloads ---
[MessagePackObject(AllowPrivate = true)]
internal record RequestPayload(
    [property: JsonPropertyName("requestType"), Key("requestType")] string RequestType,
    [property: JsonPropertyName("requestId"), Key("requestId")] string RequestId,
    [property:
        JsonPropertyName("requestData"),
        Key("requestData"),
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
[MessagePackObject(SuppressSourceGeneration = true)]
public record RequestResponsePayload<TData>(
    [property: JsonPropertyName("requestType"), Key("requestType")] string RequestType,
    [property: JsonPropertyName("requestId"), Key("requestId")] string RequestId,
    [property: JsonPropertyName("requestStatus"), Key("requestStatus")] RequestStatus RequestStatus,
    [property:
        JsonPropertyName("responseData"),
        Key("responseData"),
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
[MessagePackObject]
public record RequestStatus(
    [property: JsonPropertyName("result"), Key("result")] bool Result,
    [property: JsonPropertyName("code"), Key("code")] int Code,
    [property:
        JsonPropertyName("comment"),
        Key("comment"),
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
[MessagePackObject(SuppressSourceGeneration = true)]
public record EventPayloadBase<TData>(
    [property: JsonPropertyName("eventType"), Key("eventType")] string EventType,
    [property: JsonPropertyName("eventIntent"), Key("eventIntent")] int EventIntent,
    [property:
        JsonPropertyName("eventData"),
        Key("eventData"),
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
[MessagePackObject]
public record BatchRequestItem(
    [property: Key("requestType")] string RequestType,
    [property: Key("requestData")] object? RequestData = null
);

[MessagePackObject(AllowPrivate = true)]
internal record RequestBatchPayload(
    [property: JsonPropertyName("requestId"), Key("requestId")] string RequestId,
    [property:
        JsonPropertyName("haltOnFailure"),
        Key("haltOnFailure"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)
    ]
        bool? HaltOnFailure,
    [property:
        JsonPropertyName("executionType"),
        Key("executionType"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)
    ]
        RequestBatchExecutionType? ExecutionType,
    [property: JsonPropertyName("requests"), Key("requests")] List<RequestPayload> Requests
);

[MessagePackObject(AllowPrivate = true, SuppressSourceGeneration = true)]
internal record RequestBatchResponsePayload<TData>(
    [property: JsonPropertyName("requestId"), Key("requestId")] string RequestId,
    [property: JsonPropertyName("results"), Key("results")] List<RequestResponsePayload<TData>> Results
);
