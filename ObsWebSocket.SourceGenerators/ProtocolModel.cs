using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObsWebSocket.SourceGenerators;

/// <summary>
/// Represents the definition of a single event type in the protocol.
/// Used to generate event payload DTOs and EventArgs classes.
/// </summary>
/// <param name="Description">Description of the event.</param>
/// <param name="EventType">The unique name of the event type (e.g., "CurrentProgramSceneChanged").</param>
/// <param name="EventSubscription">The subscription intent required to receive this event.</param>
/// <param name="Complexity">Estimated complexity value from the protocol.</param>
/// <param name="RpcVersion">RPC version this event was added/modified.</param>
/// <param name="Deprecated">Whether the event is deprecated.</param>
/// <param name="InitialVersion">OBS WebSocket version this event was added.</param>
/// <param name="Category">Category the event belongs to (e.g., "Scenes", "General").</param>
/// <param name="DataFields">List of fields in the event data payload. Can be null/empty.</param>
internal sealed record OBSEvent(
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("eventSubscription")] string EventSubscription,
    [property: JsonPropertyName("complexity")] int Complexity,
    [property: JsonPropertyName("rpcVersion")] string RpcVersion,
    [property: JsonPropertyName("deprecated")] bool Deprecated,
    [property: JsonPropertyName("initialVersion")] string InitialVersion,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("dataFields")] List<FieldDefinition>? DataFields
);

/// <summary>
/// Represents the top-level structure of the protocol.json file,
/// deserialized to provide definitions for generating C# code.
/// </summary>
/// <param name="Enums">List of enum definitions from the protocol.</param>
/// <param name="Requests">List of request definitions from the protocol.</param>
/// <param name="Events">Placeholder for event definitions (currently deserialized as object).</param>
internal sealed record ProtocolDefinition(
    [property: JsonPropertyName("enums")] List<EnumDefinition> Enums,
    [property: JsonPropertyName("requests")] List<RequestDefinition> Requests,
    [property: JsonPropertyName("events")] List<OBSEvent> Events
);

/// <summary>
/// Represents the definition of a single enum in the protocol.
/// Used to generate C# enum types.
/// </summary>
/// <param name="EnumType">The name of the enum type (e.g., "WebSocketOpCode").</param>
/// <param name="EnumIdentifiers">The list of individual enum members.</param>
internal sealed record EnumDefinition(
    [property: JsonPropertyName("enumType")] string EnumType,
    [property: JsonPropertyName("enumIdentifiers")] List<EnumIdentifier> EnumIdentifiers
);

/// <summary>
/// Represents a single member (identifier) within an enum definition.
/// Used to generate C# enum members with values and documentation.
/// </summary>
/// <param name="IdentifierName">The C#-friendly name of the enum member (e.g., "Hello").</param>
/// <param name="Description">The description text for documentation.</param>
/// <param name="RpcVersion">The RPC version this member was added.</param>
/// <param name="Deprecated">Whether this member is deprecated.</param>
/// <param name="InitialVersion">The OBS WebSocket version this member was added.</param>
/// <param name="EnumValue">The actual value (captured as JsonElement to handle number/string).</param>
internal sealed record EnumIdentifier(
    [property: JsonPropertyName("enumIdentifier")] string IdentifierName,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("rpcVersion")] string RpcVersion,
    [property: JsonPropertyName("deprecated")] bool Deprecated,
    [property: JsonPropertyName("initialVersion")] string InitialVersion,
    // Use JsonElement to handle potential number or string values initially
    [property: JsonPropertyName("enumValue")] JsonElement EnumValue
);

/// <summary>
/// Represents the definition of a single request type in the protocol.
/// Used to generate request/response DTO records.
/// </summary>
/// <param name="Description">Description of the request.</param>
/// <param name="RequestType">The unique name of the request type (e.g., "GetVersion").</param>
/// <param name="Complexity">Estimated complexity value from the protocol.</param>
/// <param name="RpcVersion">RPC version this request was added/modified.</param>
/// <param name="Deprecated">Whether the request is deprecated.</param>
/// <param name="InitialVersion">OBS WebSocket version this request was added.</param>
/// <param name="Category">Category the request belongs to (e.g., "General", "Inputs").</param>
/// <param name="RequestFields">List of fields required for the request data payload. Can be null/empty.</param>
/// <param name="ResponseFields">List of fields expected in the response data payload. Can be null/empty.</param>
internal sealed record RequestDefinition(
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("requestType")] string RequestType,
    [property: JsonPropertyName("complexity")] int Complexity,
    [property: JsonPropertyName("rpcVersion")] string RpcVersion,
    [property: JsonPropertyName("deprecated")] bool Deprecated,
    [property: JsonPropertyName("initialVersion")] string InitialVersion,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("requestFields")] List<FieldDefinition>? RequestFields,
    [property: JsonPropertyName("responseFields")] List<FieldDefinition>? ResponseFields
);

/// <summary>
/// Represents a single field within a request, response, or event data structure.
/// Used to generate properties within DTO records.
/// </summary>
/// <param name="ValueName">Name of the field (e.g., "sceneName", "inputSettings.volume").</param>
/// <param name="ValueType">Protocol type string (e.g., "String", "Number", "Boolean", "Object", "Array<String>").</param>
/// <param name="ValueDescription">Description for documentation comments.</param>
/// <param name="ValueRestrictions">Optional restrictions (e.g., range for numbers).</param>
/// <param name="ValueOptional">Whether the field is optional in the request data (nullable bool for safety).</param>
/// <param name="ValueOptionalBehavior">Description of behavior when the optional value is omitted.</param>
/// <param name="ValueUuid">Specific UUID mapping if applicable (not standard, added for future flexibility if needed).</param>
internal sealed record FieldDefinition(
    [property: JsonPropertyName("valueName")] string ValueName,
    [property: JsonPropertyName("valueType")] string ValueType,
    [property: JsonPropertyName("valueDescription")] string ValueDescription,
    // Request fields only - may be null for response/event fields
    [property: JsonPropertyName("valueRestrictions")] string? ValueRestrictions,
    [property: JsonPropertyName("valueOptional")] bool? ValueOptional, // Nullable bool for safety
    [property: JsonPropertyName("valueOptionalBehavior")] string? ValueOptionalBehavior,
    // Custom field added for potential future use, ignore if not present in JSON
    [property:
        JsonPropertyName("valueUuid"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)
    ] string? ValueUuid = null
);
