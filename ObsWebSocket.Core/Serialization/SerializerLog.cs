#nullable enable

using Microsoft.Extensions.Logging;
using ObsWebSocket.Core.Protocol.Generated;

namespace ObsWebSocket.Core.Serialization;

/// <summary>Source-generated log messages for the message serializers.</summary>
internal static partial class SerializerLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "JSON serialization failed for message with OpCode {OpCode}"
    )]
    public static partial void LogJsonSerializationFailedForMessageWithOpcode(
        this ILogger logger,
        Exception exception,
        WebSocketOpCode opCode
    );

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Attempted to deserialize an empty message stream."
    )]
    public static partial void LogAttemptedToDeserializeAnEmptyMessageStream(this ILogger logger);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "JSON deserialization resulted in null."
    )]
    public static partial void LogJsonDeserializationResultedInNull(this ILogger logger);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Trace,
        Message = "Deserialized JSON message: Op={Op}"
    )]
    public static partial void LogDeserializedJsonMessageOp(
        this ILogger logger,
        WebSocketOpCode op
    );

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Error,
        Message = "JSON deserialization failed. Raw JSON: {RawJson}"
    )]
    public static partial void LogJsonDeserializationFailedRawJson(
        this ILogger logger,
        Exception exception,
        string rawJson
    );

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Error,
        Message = "Failed to deserialize message from stream."
    )]
    public static partial void LogFailedToDeserializeMessageFromStream(
        this ILogger logger,
        Exception exception
    );

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Warning,
        Message = "JSON Deserializer expected JsonElement payload but received {DataType} for {TargetType}."
    )]
    public static partial void LogJsonDeserializerExpectedJsonelementPayloadButReceived(
        this ILogger logger,
        string? dataType,
        string targetType
    );

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Error,
        Message = "JSON failed to deserialize payload to {TargetType}. Raw JSON: {Json}"
    )]
    public static partial void LogJsonFailedToDeserializePayloadToRaw(
        this ILogger logger,
        Exception exception,
        string targetType,
        string json
    );

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Warning,
        Message = "JSON Deserializer expected JsonElement payload but received {DataType} for value type {TargetType}."
    )]
    public static partial void LogJsonDeserializerExpectedJsonelementPayloadButReceived2(
        this ILogger logger,
        string? dataType,
        string targetType
    );

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Error,
        Message = "JSON failed to deserialize payload to value type {TargetType}. Raw JSON: {Json}"
    )]
    public static partial void LogJsonFailedToDeserializePayloadToValue(
        this ILogger logger,
        Exception exception,
        string targetType,
        string json
    );

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Error,
        Message = "MessagePack serialization failed for message with OpCode {OpCode}"
    )]
    public static partial void LogMessagepackSerializationFailedForMessageWithOpcode(
        this ILogger logger,
        Exception exception,
        WebSocketOpCode opCode
    );

    [LoggerMessage(
        EventId = 12,
        Level = LogLevel.Error,
        Message = "Unexpected error during MessagePack serialization for OpCode {OpCode}"
    )]
    public static partial void LogUnexpectedErrorDuringMessagepackSerializationForOpcode(
        this ILogger logger,
        Exception exception,
        WebSocketOpCode opCode
    );

    [LoggerMessage(
        EventId = 13,
        Level = LogLevel.Trace,
        Message = "Deserialized MessagePack message: Op={Op}"
    )]
    public static partial void LogDeserializedMessagepackMessageOp(
        this ILogger logger,
        WebSocketOpCode op
    );

    [LoggerMessage(
        EventId = 14,
        Level = LogLevel.Error,
        Message = "MessagePack deserialization failed."
    )]
    public static partial void LogMessagepackDeserializationFailed(
        this ILogger logger,
        Exception exception
    );

    [LoggerMessage(
        EventId = 15,
        Level = LogLevel.Error,
        Message = "MessagePack failed to deserialize payload object to {TargetType}. Object Type: {ObjectType}"
    )]
    public static partial void LogMessagepackFailedToDeserializePayloadObjectTo(
        this ILogger logger,
        Exception exception,
        string targetType,
        string objectType
    );

    [LoggerMessage(
        EventId = 16,
        Level = LogLevel.Error,
        Message = "MessagePack failed to deserialize payload object to value type {TargetType}. Object Type: {ObjectType}"
    )]
    public static partial void LogMessagepackFailedToDeserializePayloadObjectTo2(
        this ILogger logger,
        Exception exception,
        string targetType,
        string objectType
    );
}
