using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace ObsWebSocket.SourceGenerators;

/// <summary>
/// Contains DiagnosticDescriptor constants for reporting errors and warnings during source generation.
/// </summary>
[SuppressMessage(
    "StyleCop.CSharp.OrderingRules",
    "SA1202:Elements must be ordered by access",
    Justification = "Constants grouped logically."
)]
internal static class Diagnostics
{
    private const string Category = "ObsWebSocketGenerator";

    // --- General ---
    public static readonly DiagnosticDescriptor ProtocolFileNotFound = new(
        id: "OBSWSGEN001",
        title: "Protocol file not found",
        messageFormat: "Could not find the OBS WebSocket protocol.json file. Ensure it's included as AdditionalText in the project.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor ProtocolFileReadError = new(
        id: "OBSWSGEN002",
        title: "Protocol file read error",
        messageFormat: "Failed to read the OBS WebSocket protocol.json file: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor ProtocolJsonParseError = new(
        id: "OBSWSGEN003",
        title: "Protocol JSON parse error",
        messageFormat: "Failed to parse the OBS WebSocket protocol.json file: {0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor IdentifierGenerationError = new(
        id: "OBSWSGEN006",
        title: "Identifier Generation Error",
        messageFormat: "Failed to generate identifier for '{0}' in '{1}': {2}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    // --- Enums ---
    public static readonly DiagnosticDescriptor UnsupportedEnumValueType = new(
        id: "OBSWSGEN004",
        title: "Unsupported enum value type",
        messageFormat: "Enum '{0}' member '{1}' has an unsupported JSON value kind '{2}'. Only numbers are currently supported for generation.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor MixedEnumValueTypes = new(
        id: "OBSWSGEN005",
        title: "Mixed enum value types",
        messageFormat: "Enum '{0}' contains members with mixed underlying types (e.g., integer and string). Generation currently requires a consistent numeric type.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor EnumUnderlyingTypeInferenceWarning = new(
        id: "OBSWSGEN007",
        title: "Enum Underlying Type Inference",
        messageFormat: "Could not determine specific underlying type (byte/int) for enum '{0}'. Defaulting to 'int'. Values: {1}.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    // --- DTOs (New) ---
    /// <summary>
    /// Reported when a type string from protocol.json (e.g., "Uuid") cannot be mapped to a known C# type.
    /// </summary>
    public static readonly DiagnosticDescriptor UnmappableTypeError = new(
        id: "OBSWSGEN008",
        title: "Unmappable protocol type",
        messageFormat: "Cannot map OBS WebSocket protocol type '{0}' to a C# type for field '{1}' in '{2}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error, // Error because code won't compile without a type
        isEnabledByDefault: true
    );

    /// <summary>
    /// Reported when a field type is 'Object' or 'Any', indicating a potentially complex structure.
    /// The generator maps this to JsonElement? as a fallback, which requires manual handling by the user.
    /// </summary>
    public static readonly DiagnosticDescriptor NestedObjectNotSupportedWarning = new(
        id: "OBSWSGEN009",
        title: "Nested object mapping limited",
        messageFormat: "Field '{0}' in '{1}' is of type 'Object' or 'Any'. Mapping to 'System.Text.Json.JsonElement?'. Manual handling or further generation required for strong typing.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning, // Warning as JsonElement is usable but not ideal
        isEnabledByDefault: true
    );

    /// <summary>
    /// Reported when an 'Array<T>' type is encountered, but the inner type 'T' cannot be determined.
    /// The generator maps this to List<JsonElement> as a fallback.
    /// </summary>
    public static readonly DiagnosticDescriptor ArrayItemTypeUnknownWarning = new(
        id: "OBSWSGEN010",
        title: "Array item type unknown",
        messageFormat: "Could not determine item type for array field '{0}' in '{1}' from type string '{2}'. Mapping to 'List<System.Text.Json.JsonElement>'.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning, // Warning as List<JsonElement> is usable
        isEnabledByDefault: true
    );

    /// <summary>
    /// Informational diagnostic reported when an optional field that is a value type (struct) is generated as a nullable value type.
    /// </summary>
    public static readonly DiagnosticDescriptor OptionalValueTypeWarning = new(
        id: "OBSWSGEN011",
        title: "Optional value type",
        messageFormat: "Field '{0}' in '{1}' is an optional value type '{2}'. It will be generated as nullable '{2}?'.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info, // Info, as this is expected and correct behavior
        isEnabledByDefault: true
    );
}
