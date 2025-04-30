using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ObsWebSocket.SourceGenerators;

/// <summary>
/// An Incremental Source Generator that reads OBS WebSocket protocol.json
/// and generates corresponding C# enums, DTO records, and client extension methods.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ProtocolGenerator : IIncrementalGenerator
{
    private const string ProtocolFileName = "protocol.json";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // Allow reading numbers from strings (e.g., for enum values)
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// Initializes the generator and sets up the generation pipeline.
    /// </summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Step 1: Find protocol.json
        IncrementalValueProvider<AdditionalText?> protocolFileProvider = context
            .AdditionalTextsProvider.Where(static at =>
                Path.GetFileName(at.Path)
                    .Equals(ProtocolFileName, StringComparison.OrdinalIgnoreCase)
            )
            .Collect()
            .Select(static (texts, ct) => texts.FirstOrDefault());

        // Step 2: Read and parse protocol.json
        IncrementalValueProvider<(
            ProtocolDefinition? Definition,
            Diagnostic? Diagnostic
        )> protocolDefinitionProvider = protocolFileProvider.Select(
            static (additionalText, cancellationToken) =>
                LoadAndParseProtocol(additionalText, cancellationToken)
        );

        // Step 3: Register source output action
        context.RegisterSourceOutput(
            protocolDefinitionProvider,
            static (spc, result) =>
            {
                if (result.Diagnostic is not null)
                {
                    spc.ReportDiagnostic(result.Diagnostic);
                    return;
                }
                if (result.Definition is null)
                {
                    return;
                }

                Emitter.PreGenerateNestedDtos(spc, result.Definition);

                // Generate Enums, Top-Level DTOs, Client Extensions, Event Payloads, EventArgs, and Client Event Infrastructure
                Emitter.GenerateEnums(spc, result.Definition);
                Emitter.GenerateRequestDtos(spc, result.Definition);
                Emitter.GenerateResponseDtos(spc, result.Definition);
                Emitter.GenerateClientExtensions(spc, result.Definition);
                Emitter.GenerateEventPayloads(spc, result.Definition);
                Emitter.GenerateEventArgs(spc, result.Definition);
                Emitter.GenerateClientEventInfrastructure(spc, result.Definition);
            }
        );
    }

    private static (ProtocolDefinition? Definition, Diagnostic? Diagnostic) LoadAndParseProtocol(
        AdditionalText? additionalText,
        CancellationToken cancellationToken
    )
    {
        if (additionalText is null)
        {
            return (null, Diagnostic.Create(Diagnostics.ProtocolFileNotFound, Location.None));
        }
        SourceText? sourceText = additionalText.GetText(cancellationToken);
        if (sourceText is null)
        {
            return (
                null,
                Diagnostic.Create(
                    Diagnostics.ProtocolFileReadError,
                    Location.None,
                    $"Could not get SourceText for '{additionalText.Path}'"
                )
            );
        }
        try
        {
            ProtocolDefinition? definition = JsonSerializer.Deserialize<ProtocolDefinition>(
                sourceText.ToString(),
                s_jsonOptions
            );
            return definition is null
                ? ((ProtocolDefinition? Definition, Diagnostic? Diagnostic))
                    (
                        null,
                        Diagnostic.Create(
                            Diagnostics.ProtocolJsonParseError,
                            Location.None,
                            "Deserialization returned null"
                        )
                    )
                : ((ProtocolDefinition? Definition, Diagnostic? Diagnostic))(definition, null);
        }
        catch (JsonException jsonEx)
        {
            Location location = Location.Create(
                additionalText.Path,
                TextSpan.FromBounds(0, 0),
                new LinePositionSpan()
            );
            // Add line/byte position if available in JsonException
            string message =
                jsonEx.LineNumber.HasValue && jsonEx.BytePositionInLine.HasValue
                    ? $"Failed to parse JSON at Line {jsonEx.LineNumber + 1}, Position {jsonEx.BytePositionInLine + 1}: {jsonEx.Message}"
                    : $"Failed to parse JSON: {jsonEx.Message}";
            return (null, Diagnostic.Create(Diagnostics.ProtocolJsonParseError, location, message));
        }
        catch (Exception ex)
        {
            Location location = Location.Create(
                additionalText.Path,
                TextSpan.FromBounds(0, 0),
                new LinePositionSpan()
            );
            return (
                null,
                Diagnostic.Create(
                    Diagnostics.ProtocolFileReadError,
                    location,
                    $"Unexpected error: {ex.Message}"
                )
            );
        }
    }
}
