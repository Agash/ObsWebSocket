// ObsWebSocket.Codegen.Tasks/Generation/Emitter.BatchBuilder.cs
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ObsWebSocket.Codegen.Tasks.Generation;

/// <summary>
/// Emitter logic for the typed batch builder, which pairs each request type string with the
/// request data record the protocol defines for it.
/// </summary>
internal static partial class Emitter
{
    /// <summary>
    /// Generates one builder method per protocol request.
    /// </summary>
    /// <param name="context">The source production context.</param>
    /// <param name="protocol">The parsed protocol definition.</param>
    public static void GenerateBatchBuilder(
        SourceProductionContext context,
        ProtocolDefinition protocol
    )
    {
        if (protocol.Requests is null || protocol.Requests.Count == 0)
        {
            return;
        }

        StringBuilder builder = BuildSourceHeader("// Helper: typed batch builder methods");
        builder.AppendLine("using System;");
        builder.AppendLine($"using {GeneratedRequestsNamespace};");
        builder.AppendLine();
        builder.AppendLine($"namespace {ExtensionsNamespace};");
        builder.AppendLine();
        builder.AppendLine("public sealed partial class ObsBatchBuilder");
        builder.AppendLine("{");

        foreach (RequestDefinition request in protocol.Requests)
        {
            try
            {
                string requestType = request.RequestType;
                string methodName = SanitizeIdentifier(requestType);
                if (string.IsNullOrEmpty(methodName))
                {
                    continue;
                }

                bool hasData = request.RequestFields?.Count > 0;

                builder.AppendLine("    /// <summary>");
                builder.AppendLine(
                    $"    /// Appends a <c>{requestType}</c> request to the batch."
                );
                if (!string.IsNullOrWhiteSpace(request.Description))
                {
                    builder.AppendLine(
                        $"    /// <para>{FlattenDescription(request.Description)}</para>"
                    );
                }

                builder.AppendLine("    /// </summary>");
                builder.AppendLine("    /// <returns>The same builder, for chaining.</returns>");
                if (request.Deprecated)
                {
                    builder.AppendLine(
                        $"    [System.Obsolete(\"Deprecated in OBS Websocket version {request.InitialVersion}\")]"
                    );
                }

                if (hasData)
                {
                    string dataTypeName =
                        $"{GeneratedRequestsNamespace}.{methodName}RequestData";
                    builder.AppendLine(
                        $"    /// <param name=\"requestData\">The payload for this request.</param>"
                    );
                    builder.AppendLine(
                        $"    public ObsBatchBuilder {methodName}({dataTypeName} requestData)"
                    );
                    builder.AppendLine("    {");
                    builder.AppendLine("        ArgumentNullException.ThrowIfNull(requestData);");
                    builder.AppendLine(
                        $"        return Add(\"{requestType}\", requestData);"
                    );
                    builder.AppendLine("    }");
                }
                else
                {
                    builder.AppendLine(
                        $"    public ObsBatchBuilder {methodName}() => Add(\"{requestType}\", null);"
                    );
                }

                builder.AppendLine();
            }
            catch (Exception ex)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.IdentifierGenerationError,
                        Location.None,
                        request.RequestType,
                        $"Generating batch builder method for {request.RequestType}",
                        ex.Message
                    )
                );
            }
        }

        builder.AppendLine("}");

        context.AddSource(
            "ObsBatchBuilder.Requests.g.cs",
            SourceText.From(builder.ToString(), Encoding.UTF8)
        );
    }
}
