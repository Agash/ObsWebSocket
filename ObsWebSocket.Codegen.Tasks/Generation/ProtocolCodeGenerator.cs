using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;

namespace ObsWebSocket.Codegen.Tasks.Generation;

internal static class ProtocolCodeGenerator
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static (
        IReadOnlyDictionary<string, string> Sources,
        IReadOnlyList<Diagnostic> Diagnostics
    ) Generate(string protocolJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(protocolJson);

        GenerationContext context = new();
        ProtocolDefinition? protocol = JsonSerializer.Deserialize<ProtocolDefinition>(
            protocolJson,
            s_jsonOptions
        );
        if (protocol is null)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    Diagnostics.ProtocolJsonParseError,
                    Location.None,
                    "Deserialization returned null."
                )
            );
            return (context.Sources, context.Diagnostics);
        }

        ReportUnmappedStringEnums(context, protocol);
        Emitter.PreGenerateNestedDtos(context, protocol);
        Emitter.GenerateEnums(context, protocol);
        Emitter.GenerateRequestDtos(context, protocol);
        Emitter.GenerateResponseDtos(context, protocol);
        Emitter.GeneratePayloadSchema(context, protocol);
        Emitter.GenerateClientExtensions(context, protocol);
        Emitter.GenerateHandleOverloads(context, protocol);
        Emitter.GenerateEventPayloads(context, protocol);
        Emitter.GenerateEventArgs(context, protocol);
        Emitter.GenerateClientEventInfrastructure(context, protocol);
        Emitter.GenerateWaitForEventHelper(context, protocol);
        Emitter.GenerateEventStreams(context, protocol);
        Emitter.GenerateBatchBuilder(context, protocol);
        Emitter.GenerateJsonSerializerContext(context, protocol);
        Emitter.GenerateMsgPackResolver(context, protocol);

        return (context.Sources, context.Diagnostics);
    }

    /// <summary>
    /// Fails the build when the protocol declares a string-valued enum that no field is mapped
    /// onto.
    /// </summary>
    /// <remarks>
    /// The definition types these fields as plain <c>String</c> and never says which enum they
    /// draw from, so the association is hand written. That table cannot be derived, but it can be
    /// checked: a protocol refresh introducing a new string enum has to be noticed, or every field
    /// carrying it silently stays a string.
    /// </remarks>
    private static void ReportUnmappedStringEnums(
        SourceProductionContext context,
        ProtocolDefinition protocol
    )
    {
        if (protocol.Enums is null)
        {
            return;
        }

        HashSet<string> mapped = new(StringEnumFieldTable.MappedEnums, StringComparer.Ordinal);

        foreach (EnumDefinition definition in protocol.Enums)
        {
            bool stringValued =
                definition.EnumIdentifiers.Count > 0
                && definition.EnumIdentifiers.TrueForAll(i =>
                    i.EnumValue.ValueKind == System.Text.Json.JsonValueKind.String
                );

            // The generated C# name drops the protocol's Obs prefix.
            string generatedName = definition.EnumType.StartsWith("Obs", StringComparison.Ordinal)
                ? definition.EnumType["Obs".Length..]
                : definition.EnumType;

            if (stringValued && !mapped.Contains(generatedName))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.UnmappedStringEnum,
                        Location.None,
                        definition.EnumType
                    )
                );
            }
        }
    }
}
