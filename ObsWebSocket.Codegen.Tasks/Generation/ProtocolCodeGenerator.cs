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

    public static (IReadOnlyDictionary<string, string> Sources, IReadOnlyList<Diagnostic> Diagnostics) Generate(string protocolJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(protocolJson);

        GenerationContext context = new();
        ProtocolDefinition? protocol = JsonSerializer.Deserialize<ProtocolDefinition>(protocolJson, s_jsonOptions);
        if (protocol is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ProtocolJsonParseError, Location.None, "Deserialization returned null."));
            return (context.Sources, context.Diagnostics);
        }

        Emitter.PreGenerateNestedDtos(context, protocol);
        Emitter.GenerateEnums(context, protocol);
        Emitter.GenerateRequestDtos(context, protocol);
        Emitter.GenerateResponseDtos(context, protocol);
        Emitter.GenerateClientExtensions(context, protocol);
        Emitter.GenerateEventPayloads(context, protocol);
        Emitter.GenerateEventArgs(context, protocol);
        Emitter.GenerateClientEventInfrastructure(context, protocol);
        Emitter.GenerateWaitForEventHelper(context, protocol);
        Emitter.GenerateJsonSerializerContext(context, protocol);

        return (context.Sources, context.Diagnostics);
    }
}
