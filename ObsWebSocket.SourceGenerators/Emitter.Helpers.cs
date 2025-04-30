using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace ObsWebSocket.SourceGenerators;

/// <summary>
/// Contains helper methods, constants, and internal data structures for the Emitter class.
/// </summary>
internal static partial class Emitter
{
    // --- Constants for Namespaces ---
    private const string BaseNamespace = "ObsWebSocket.Core.Protocol";
    private const string ExtensionsNamespace = "ObsWebSocket.Core";
    private const string GeneratedEnumsNamespace = $"{BaseNamespace}.Generated";
    private const string GeneratedRequestsNamespace = $"{BaseNamespace}.Requests";
    private const string GeneratedResponsesNamespace = $"{BaseNamespace}.Responses";
    private const string GeneratedEventsNamespace = $"{BaseNamespace}.Events";
    private const string GeneratedCommonNamespace = $"{BaseNamespace}.Common";
    private const string GeneratedEventArgsNamespace = "ObsWebSocket.Core.Events.Generated";

    // --- Regex Helpers ---
    // Matches numeric literals or C#-like bitwise expressions for enum values.
    private static readonly Regex s_numericLikeStringRegex = new(
        @"^([-+]?\d+)$|^(\(1\s*<<\s*\d+\))$|^(\([\w\s|]+\))$",
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace
    );

    // Extracts the inner type T from an "Array<T>" string.
    private static readonly Regex s_arrayTypeRegex = new(
        @"^Array<(.+)>$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    // Set of C# keywords for identifier sanitization.
    private static readonly HashSet<string> s_keywords = new(StringComparer.Ordinal)
    {
        "abstract",
        "as",
        "base",
        "bool",
        "break",
        "byte",
        "case",
        "catch",
        "char",
        "checked",
        "class",
        "const",
        "continue",
        "decimal",
        "default",
        "delegate",
        "do",
        "double",
        "else",
        "enum",
        "event",
        "explicit",
        "extern",
        "false",
        "finally",
        "fixed",
        "float",
        "for",
        "foreach",
        "goto",
        "if",
        "implicit",
        "in",
        "int",
        "interface",
        "internal",
        "is",
        "lock",
        "long",
        "namespace",
        "new",
        "null",
        "object",
        "operator",
        "out",
        "override",
        "params",
        "private",
        "protected",
        "public",
        "readonly",
        "ref",
        "return",
        "sbyte",
        "sealed",
        "short",
        "sizeof",
        "stackalloc",
        "static",
        "string",
        "struct",
        "switch",
        "this",
        "throw",
        "true",
        "try",
        "typeof",
        "uint",
        "ulong",
        "unchecked",
        "unsafe",
        "ushort",
        "using",
        "virtual",
        "void",
        "volatile",
        "while",
        // Contextual keywords
        "add",
        "alias",
        "async",
        "await",
        "by",
        "descending",
        "dynamic",
        "from",
        "get",
        "global",
        "group",
        "into",
        "join",
        "let",
        "nameof",
        "on",
        "orderby",
        "partial",
        "remove",
        "select",
        "set",
        "value",
        "var",
        "when",
        "where",
        "yield",
    };

    // Represents the kind of values an enum definition contains.
    private enum EnumValueKind
    {
        Unknown,
        Numeric,
        StringBased,
        Mixed,
    }

    // Helper record for property generation info. Used in DTO/Payload generation.
    private sealed record PropertyGenInfo(
        string PropertyName,
        string ParamName,
        string CSharpType,
        string NullableSuffix,
        bool IsRequired,
        string OriginalName, // Keep track of original name for constructor ordering
        FieldDefinition? FieldDef // Keep track of original definition for docs/required status
    );

    // --- Hierarchy Node Structures ---
    /// <summary>
    /// Base class for nodes in the protocol object hierarchy, built from dot notation.
    /// </summary>
    private abstract record ProtocolNode(string Name, string SanitizedName);

    /// <summary>
    /// Represents an object node which can contain other fields or nested objects.
    /// </summary>
    private sealed record ProtocolObjectNode(string Name, string SanitizedName)
        : ProtocolNode(Name, SanitizedName)
    {
        public Dictionary<string, ProtocolNode> Children { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public string? GeneratedTypeName { get; set; } // Stores the generated C# type name for this nested object
    }

    /// <summary>
    /// Represents a leaf node (a primitive field) in the hierarchy.
    /// </summary>
    private sealed record ProtocolFieldNode(
        string Name,
        string SanitizedName,
        FieldDefinition FieldDefinition
    ) : ProtocolNode(Name, SanitizedName);

    // --- Shared Helper Methods ---
    /// <summary>
    /// Builds the standard C# file header for generated files.
    /// </summary>
    private static StringBuilder BuildSourceHeader(string? fileTypeComment = null)
    {
        StringBuilder builder = new();
        builder.AppendLine("// <auto-generated/>");
        if (!string.IsNullOrEmpty(fileTypeComment))
        {
            builder.AppendLine(fileTypeComment);
        }

        builder.AppendLine("#nullable enable"); // Enable nullable context globally for the file
        builder.AppendLine();
        return builder;
    }

    /// <summary>
    /// Maps an OBS WebSocket protocol type string to its corresponding C# type name and value type status.
    /// Reports diagnostics for unmappable types and handles fallback for Object/Any.
    /// Includes heuristics for common Array<Object> and specific Object fields.
    /// Note: Does not handle nested object types derived from dot notation; that's handled during hierarchy processing.
    /// </summary>
    /// <param name="context">Source generation context for reporting diagnostics.</param>
    /// <param name="field">The field definition being mapped.</param>
    /// <param name="parentDtoName">Name of the DTO this field belongs to (for diagnostics).</param>
    /// <returns>A tuple containing the C# type name (or null if unmappable) and a boolean indicating if it's a value type.</returns>
    private static (string? CSharpType, bool IsValueType) MapProtocolTypeToCSharp(
        SourceProductionContext context,
        FieldDefinition field,
        string parentDtoName
    )
    {
        string obsType = field.ValueType;
        string fieldName = field.ValueName;

        // --- Specific Field Name/Type Checks (Applied Universally) ---
        // These checks apply regardless of request/response/event context.
        if (obsType == "Object")
        {
            switch (fieldName)
            {
                case "inputAudioTracks":
                    // Map specifically named 'Object' field to Dictionary
                    return ("System.Collections.Generic.Dictionary<string, bool>?", false);
                case "sceneItemTransform":
                    // Map specifically named 'Object' field to Stub record
                    // Use the fully qualified name to avoid potential namespace conflicts
                    return ($"{GeneratedCommonNamespace}.SceneItemTransformStub?", false);
                // Add other specific 'Object' mappings here if needed in the future
            }
            // If not handled above, it falls through to the general 'Object'/'Any' handling below
        }

        // --- Basic Type Mapping ---
        string? mappedType = obsType switch
        {
            "String" => "string",
            "Number" => "double",
            "Boolean" => "bool",
            "Uuid" => "string",
            "Object" or "Any" => "System.Text.Json.JsonElement?", // Fallback for unhandled Object/Any
            _ => null,
        };

        if (mappedType != null)
        {
            // --- Report Diagnostics for Explicit Object/Any Fallback ---
            // Only warn if it fell back to JsonElement and wasn't handled by specific rules above
            if (mappedType == "System.Text.Json.JsonElement?")
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.NestedObjectNotSupportedWarning,
                        Location.None,
                        fieldName,
                        parentDtoName
                    )
                );
            }

            // --- Report Diagnostics for Optional Value Types ---
            bool isOptional = field.ValueOptional.GetValueOrDefault(false);
            if (isOptional && IsValueType(mappedType) && !mappedType.EndsWith("?"))
            {
                // Report diagnostic for optional value types that need nullable annotation
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.OptionalValueTypeWarning,
                        Location.None,
                        fieldName,
                        parentDtoName,
                        mappedType // Pass the original mapped type name
                    )
                );
                // Note: The actual generation logic in Emitter.DtoGeneration.cs adds the '?' suffix later based on 'isConsideredRequired'.
                // This diagnostic just informs the user about the mapping decision.
            }
            return (mappedType, IsValueType(mappedType));
        }

        // --- Handle Array<T> Types ---
        Match arrayMatch = s_arrayTypeRegex.Match(obsType);
        if (arrayMatch.Success)
        {
            string innerObsType = arrayMatch.Groups[1].Value;

            // --- Handle Array<Object> with Heuristics ---
            if (innerObsType == "Object")
            {
                // Use fully qualified names for stub types to avoid ambiguity
                string? stubType = fieldName switch
                {
                    "sceneItems" => $"{GeneratedCommonNamespace}.SceneItemStub",
                    "filters" => $"{GeneratedCommonNamespace}.FilterStub",
                    // Need to check fully qualified parent name to exclude InputVolumeMetersPayload
                    "inputs"
                        when parentDtoName
                            != $"{GeneratedEventsNamespace}.InputVolumeMetersPayload" =>
                        $"{GeneratedCommonNamespace}.InputStub",
                    "scenes" => $"{GeneratedCommonNamespace}.SceneStub",
                    "outputs" => $"{GeneratedCommonNamespace}.OutputStub",
                    "transitions" => $"{GeneratedCommonNamespace}.TransitionStub",
                    "monitors" => $"{GeneratedCommonNamespace}.MonitorStub",
                    "propertyItems" => $"{GeneratedCommonNamespace}.PropertyItemStub",
                    _ => null,
                };

                if (stubType != null)
                {
                    // Use fully qualified List<T>
                    return ($"System.Collections.Generic.List<{stubType}>?", false); // List of specific stub type
                }
                else // Fallback for unknown or explicitly excluded Array<Object>
                {
                    // Only warn if it's truly unknown, not the handled InputVolumeMeters case
                    if (
                        fieldName != "inputs"
                        || parentDtoName != $"{GeneratedEventsNamespace}.InputVolumeMetersPayload"
                    )
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                Diagnostics.ArrayItemTypeUnknownWarning,
                                Location.None,
                                fieldName,
                                parentDtoName,
                                obsType
                            )
                        );
                    }
                    // Fallback to List<JsonElement> for InputVolumeMetersPayload.inputs and any other unmapped Array<Object>
                    return (
                        "System.Collections.Generic.List<System.Text.Json.JsonElement>?",
                        false
                    );
                }
            }

            // --- Handle Array<Primitive> ---
            // Create a temporary field definition for the inner type to reuse mapping logic
            FieldDefinition innerField = new("", innerObsType, "", null, null, null, null);
            (string? innerCSharpType, bool isInnerValueType) = MapProtocolTypeToCSharp(
                context,
                innerField,
                $"{parentDtoName}.{fieldName}<>" // Provide context for potential nested diagnostics
            );

            if (innerCSharpType != null)
            {
                // Determine if the element type itself should be nullable within the list
                string elementNullableSuffix =
                    (!isInnerValueType || innerCSharpType.EndsWith("?")) ? "" : "?";
                // Use fully qualified List<T>
                return (
                    $"System.Collections.Generic.List<{innerCSharpType}{elementNullableSuffix}>?",
                    false
                ); // List<T>? (The list itself is nullable)
            }
            else // Inner primitive type could not be mapped
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.ArrayItemTypeUnknownWarning,
                        Location.None,
                        fieldName,
                        parentDtoName,
                        obsType
                    )
                );
                // Fallback if inner type mapping failed
                return ("System.Collections.Generic.List<System.Text.Json.JsonElement>?", false);
            }
        }

        // --- Handle Unmappable Type ---
        // If we reach here, the obsType is not a basic type, not Object/Any, and not Array<T>
        context.ReportDiagnostic(
            Diagnostic.Create(
                Diagnostics.UnmappableTypeError,
                Location.None,
                obsType,
                fieldName,
                parentDtoName
            )
        );
        return (null, false);
    }

    /// <summary>
    /// Determines if a C# type name represents a value type (struct).
    /// </summary>
    private static bool IsValueType(string csharpTypeName)
    {
        string baseType = csharpTypeName.TrimEnd('?');
        return baseType
            is "bool"
                or "byte"
                or "short"
                or "int"
                or "long"
                or "sbyte"
                or "ushort"
                or "uint"
                or "ulong"
                or "float"
                or "double"
                or "decimal"
                or "char"
                or "System.Text.Json.JsonElement";
    }

    /// <summary>
    /// Appends an XML documentation summary tag to the StringBuilder. Escapes content.
    /// </summary>
    private static void AppendXmlDocSummary(
        StringBuilder builder,
        string? text,
        int indentLevel,
        string tag = "summary",
        string? attributeName = null
    )
    {
        string indent = new(' ', indentLevel * 4);
        string attribute = attributeName == null ? "" : $" name=\"{attributeName}\"";
        string content = string.IsNullOrWhiteSpace(text)
            ? "No description provided."
            : text!.Trim();
        string[] lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);

        builder.AppendLine($"{indent}/// <{tag}{attribute}>");
        foreach (string line in lines)
        {
            builder.AppendLine($"{indent}/// {System.Security.SecurityElement.Escape(line)}");
        }
        builder.AppendLine($"{indent}/// </{tag}>");
    }

    /// <summary>
    /// Appends multi-line text content within XML documentation comments with a specified prefix. Escapes content.
    /// </summary>
    private static void AppendMultiLineXmlDoc(
        StringBuilder builder,
        string? text,
        string linePrefix
    )
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            builder.AppendLine($"{linePrefix} No description provided.");
            return;
        }

        string[] lines = text?.Trim().Split(["\r\n", "\n"], StringSplitOptions.None) ?? [];
        foreach (string line in lines)
        {
            builder.AppendLine($"{linePrefix} {System.Security.SecurityElement.Escape(line)}");
        }
    }

    /// <summary>
    /// Appends XML documentation remarks based on field properties (Restrictions, Optional Behavior) for request DTOs.
    /// </summary>
    private static void AppendXmlDocRemarks(
        StringBuilder builder,
        FieldDefinition field,
        int indentLevel
    )
    {
        string indent = new(' ', indentLevel * 4);
        bool hasRemarks = false;
        StringBuilder remarksBuilder = new();

        if (!string.IsNullOrEmpty(field.ValueRestrictions))
        {
            remarksBuilder.AppendLine(
                $"{indent}/// <para>Restrictions: {System.Security.SecurityElement.Escape(field.ValueRestrictions)}</para>"
            );
            hasRemarks = true;
        }

        if (field.ValueOptional.HasValue)
        {
            remarksBuilder.AppendLine(
                $"{indent}/// <para>Optional: {field.ValueOptional.Value.ToString().ToLowerInvariant()}</para>"
            );
            hasRemarks = true;
            if (!string.IsNullOrEmpty(field.ValueOptionalBehavior))
            {
                remarksBuilder.AppendLine(
                    $"{indent}/// Behavior When Optional: {System.Security.SecurityElement.Escape(field.ValueOptionalBehavior)}"
                );
            }
        }

        if (hasRemarks)
        {
            builder.AppendLine($"{indent}/// <remarks>");
            builder.Append(remarksBuilder.ToString());
            builder.AppendLine($"{indent}/// </remarks>");
        }
    }

    /// <summary>
    /// Sanitizes a name from the protocol to be a valid C# identifier.
    /// Prepends '@' if the sanitized name is a C# keyword.
    /// Replaces invalid characters with underscores.
    /// Ensures the first character is a letter or underscore.
    /// </summary>
    private static string SanitizeIdentifier(string? originalName)
    {
        if (string.IsNullOrWhiteSpace(originalName))
        {
            return "_"; // Return a default valid identifier
        }

        StringBuilder builder = new();
        char firstChar = originalName![0];

        // Ensure the first character is valid
        if (!char.IsLetter(firstChar) && firstChar != '_')
        {
            builder.Append('_');
        }
        else
        {
            builder.Append(firstChar);
        }

        // Process remaining characters
        for (int i = 1; i < originalName.Length; i++)
        {
            char c = originalName[i];
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('_'); // Replace invalid characters
            }
        }

        string sanitized = builder.ToString();

        // Check if the sanitized name is a C# keyword
        return s_keywords.Contains(sanitized) ? "@" + sanitized : sanitized;
    }

    /// <summary>
    /// Converts a potentially sanitized C# identifier to PascalCase.
    /// Handles identifiers starting with '@'.
    /// </summary>
    private static string ToPascalCase(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        bool startsWithAt = input.StartsWith("@");
        string baseName = startsWithAt ? input.Substring(1) : input;

        if (string.IsNullOrEmpty(baseName))
        {
            return input; // Handle "@" case
        }

        // If already PascalCase or ALL CAPS, return as is (preserving '@' if present)
        if (char.IsUpper(baseName[0]) || baseName.All(char.IsUpper))
        {
            return input;
        }

        // Convert first character to uppercase
        string pascalName =
            char.ToUpperInvariant(baseName[0]).ToString()
            + (baseName.Length > 1 ? baseName.Substring(1) : "");

        return startsWithAt ? "@" + pascalName : pascalName;
    }

    /// <summary>
    /// Converts a potentially sanitized C# identifier to camelCase.
    /// Handles identifiers starting with '@'.
    /// </summary>
    private static string ToCamelCase(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        bool startsWithAt = input.StartsWith("@");
        string baseName = startsWithAt ? input.Substring(1) : input;

        if (string.IsNullOrEmpty(baseName))
        {
            return input; // Handle "@" case
        }

        // If already camelCase or ALL CAPS, return as is (preserving '@' if present)
        if (char.IsLower(baseName[0]) || baseName.All(char.IsUpper))
        {
            return input;
        }

        // Convert first character to lowercase
        string camelName =
            char.ToLowerInvariant(baseName[0]).ToString()
            + (baseName.Length > 1 ? baseName.Substring(1) : "");

        return startsWithAt ? "@" + camelName : camelName;
    }

    /// <summary>
    /// Infers the kind of values (numeric, string, mixed) and the specific numeric type (byte, int) for an enum definition.
    /// Handles numeric values represented as strings or C#-like expressions in the JSON.
    /// Reports diagnostics for unsupported or mixed value types.
    /// </summary>
    private static (EnumValueKind Kind, string? NumericType) InferEnumValueKindAndNumericType(
        SourceProductionContext context,
        EnumDefinition enumDef
    )
    {
        if (enumDef.EnumIdentifiers is null || enumDef.EnumIdentifiers.Count == 0)
        {
            return (EnumValueKind.Unknown, null);
        }

        EnumValueKind overallKind = EnumValueKind.Unknown;
        bool canBeByte = true;
        bool canBeInt = true; // Assume int is possible until proven otherwise
        List<string> rawValuesForDiag = []; // For better diagnostic messages

        foreach (EnumIdentifier member in enumDef.EnumIdentifiers)
        {
            string rawText = member.EnumValue.GetRawText().Trim('"');
            rawValuesForDiag.Add(rawText);
            EnumValueKind currentMemberKind;
            long? numericValue = null; // Store parsed numeric value for range checks

            switch (member.EnumValue.ValueKind)
            {
                case JsonValueKind.Number:
                {
                    currentMemberKind = EnumValueKind.Numeric;
                    if (member.EnumValue.TryGetInt64(out long val))
                    {
                        numericValue = val;
                    }
                    else // Handle non-integer numbers if needed, currently flags as non-byte/int
                    {
                        canBeByte = canBeInt = false;
                    }

                    break;
                }
                case JsonValueKind.String:
                {
                    // Check if the string looks like a number or a bitwise expression
                    if (s_numericLikeStringRegex.IsMatch(rawText))
                    {
                        currentMemberKind = EnumValueKind.Numeric;
                        // Try parsing simple numbers for range checks
                        if (long.TryParse(rawText, out long val))
                        {
                            numericValue = val;
                        }
                        else
                        {
                            // Assume expressions might exceed byte range, but could fit in int
                            canBeByte = false;
                            // We don't invalidate `canBeInt` here, as expressions like (1 << 10) fit
                        }
                    }
                    else
                    {
                        // It's a genuine string-based enum value
                        currentMemberKind = EnumValueKind.StringBased;
                        canBeByte = canBeInt = false; // Cannot be numeric underlying type
                    }
                    break;
                }
                default:
                    // Report error for unsupported JSON kinds (like true, false, null, object, array)
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            Diagnostics.UnsupportedEnumValueType,
                            Location.None,
                            enumDef.EnumType,
                            member.IdentifierName,
                            member.EnumValue.ValueKind
                        )
                    );
                    return (EnumValueKind.Mixed, null); // Treat as Mixed/Error
            }

            // Check for consistency
            if (overallKind == EnumValueKind.Unknown)
            {
                overallKind = currentMemberKind;
            }
            else if (overallKind != currentMemberKind)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.MixedEnumValueTypes,
                        Location.None,
                        enumDef.EnumType
                    )
                );
                return (EnumValueKind.Mixed, null); // Cannot generate if mixed
            }

            // Check numeric range if applicable
            if (numericValue.HasValue)
            {
                if (numericValue.Value is < byte.MinValue or > byte.MaxValue)
                {
                    canBeByte = false;
                }

                if (numericValue.Value is < int.MinValue or > int.MaxValue)
                {
                    canBeInt = false; // If it exceeds int range, we have a problem
                }
            }
        }

        // Determine final numeric type or return if not consistently numeric
        if (overallKind == EnumValueKind.Numeric)
        {
            // Handle specific known enums that use int for flags
            if (enumDef.EnumType is "EventSubscription" or "RequestBatchExecutionType")
            {
                // Check if it can fit in int, otherwise report error (or potentially long if needed?)
                if (!canBeInt)
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            Diagnostics.EnumUnderlyingTypeInferenceWarning,
                            Location.None,
                            enumDef.EnumType,
                            $"Value(s) exceed Int32 range: {string.Join(", ", rawValuesForDiag)}"
                        )
                    );
                    // Still generate as 'int' with a warning
                    return (EnumValueKind.Numeric, "int");
                }
                return (EnumValueKind.Numeric, "int");
            }
            if (enumDef.EnumType is "WebSocketOpCode")
            {
                // WebSocketOpCode specifically uses byte in the protocol
                return (EnumValueKind.Numeric, "byte");
            }

            // Default logic: prefer byte if possible, then int
            if (canBeByte)
            {
                return (EnumValueKind.Numeric, "byte");
            }

            if (canBeInt)
            {
                return (EnumValueKind.Numeric, "int");
            }

            // If values exceeded int range, report warning and fallback (though this indicates a potential protocol issue)
            context.ReportDiagnostic(
                Diagnostic.Create(
                    Diagnostics.EnumUnderlyingTypeInferenceWarning,
                    Location.None,
                    enumDef.EnumType,
                    $"Numeric value(s) exceed Int32 range or couldn't be determined: {string.Join(", ", rawValuesForDiag)}"
                )
            );
            return (EnumValueKind.Numeric, "int"); // Fallback, but might be incorrect if values exceed Int32.MaxValue
        }
        else if (overallKind == EnumValueKind.StringBased)
        {
            return (EnumValueKind.StringBased, null); // It's consistently string-based
        }
        else // Mixed or Unknown (should have been handled by errors above)
        {
            // This path should ideally not be reached due to prior error reporting
            return (overallKind, null);
        }
    }
}
