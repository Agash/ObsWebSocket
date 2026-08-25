// ObsWebSocket.Codegen.Tasks/Generation/Emitter.BatchBuilder.cs
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ObsWebSocket.Codegen.Tasks.Generation;

/// <summary>
/// Emitter logic for the typed batch builder. Requests are grouped by protocol category, and each
/// returns a reference carrying its response type so a result can be read without restating
/// either its position or its type.
/// </summary>
internal static partial class Emitter
{
    /// <summary>
    /// Generates the category groups and their request methods.
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

        StringBuilder builder = BuildSourceHeader("// Helper: typed batch builder groups");
        builder.AppendLine("using System;");
        builder.AppendLine($"using {GeneratedRequestsNamespace};");
        builder.AppendLine($"using {GeneratedResponsesNamespace};");
        builder.AppendLine();
        builder.AppendLine($"namespace {ExtensionsNamespace};");
        builder.AppendLine();

        List<(string Category, string GroupName)> groups = [];
        foreach (
            IGrouping<string, RequestDefinition> group in protocol
                .Requests.GroupBy(r => r.Category ?? "general", StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
        )
        {
            string groupName = ToGroupName(group.Key);
            groups.Add((group.Key, groupName));

            builder.AppendLine("/// <summary>");
            builder.AppendLine(
                $"/// Batch requests in the <c>{System.Security.SecurityElement.Escape(group.Key)}</c> category."
            );
            builder.AppendLine("/// </summary>");
            builder.AppendLine("/// <param name=\"builder\">The batch being built.</param>");
            builder.AppendLine(
                $"public readonly struct {groupName}BatchGroup(ObsBatchBuilder builder)"
            );
            builder.AppendLine("{");

            foreach (RequestDefinition request in group)
            {
                EmitBatchRequestMethod(context, builder, request);
            }

            builder.AppendLine("}");
            builder.AppendLine();
        }

        builder.AppendLine("public sealed partial class ObsBatchBuilder");
        builder.AppendLine("{");
        foreach ((string category, string groupName) in groups)
        {
            builder.AppendLine("    /// <summary>");
            builder.AppendLine(
                $"    /// Requests in the <c>{System.Security.SecurityElement.Escape(category)}</c> category."
            );
            builder.AppendLine("    /// </summary>");
            builder.AppendLine($"    public {groupName}BatchGroup {groupName} => new(this);");
            builder.AppendLine();
        }

        builder.AppendLine("}");

        context.AddSource(
            "ObsBatchBuilder.Requests.g.cs",
            SourceText.From(builder.ToString(), Encoding.UTF8)
        );
    }

    /// <summary>
    /// Emits one request method, returning a reference to its eventual result.
    /// </summary>
    private static void EmitBatchRequestMethod(
        SourceProductionContext context,
        StringBuilder builder,
        RequestDefinition request
    )
    {
        try
        {
            string requestType = request.RequestType;
            string methodName = SanitizeIdentifier(requestType);
            if (string.IsNullOrEmpty(methodName))
            {
                return;
            }

            bool hasData = request.RequestFields?.Count > 0;
            bool hasResponse = request.ResponseFields?.Count > 0;

            string returnType = hasResponse
                ? $"BatchRef<{GeneratedResponsesNamespace}.{methodName}ResponseData>"
                : "BatchRef";

            builder.AppendLine("    /// <summary>");
            builder.AppendLine($"    /// Adds a <c>{requestType}</c> request to the batch.");
            if (!string.IsNullOrWhiteSpace(request.Description))
            {
                builder.AppendLine(
                    $"    /// <para>{FlattenBatchDescription(request.Description)}</para>"
                );
            }

            builder.AppendLine("    /// </summary>");
            if (hasData)
            {
                builder.AppendLine(
                    "    /// <param name=\"requestData\">The payload for this request.</param>"
                );
            }

            builder.AppendLine(
                "    /// <returns>A reference used to read this request's result.</returns>"
            );
            if (request.Deprecated)
            {
                builder.AppendLine(
                    $"    [System.Obsolete(\"Request '{requestType}' is deprecated since OBS Websocket version {request.InitialVersion}\")]"
                );
            }

            if (hasData)
            {
                string dataTypeName = $"{GeneratedRequestsNamespace}.{methodName}RequestData";
                builder.AppendLine(
                    $"    public {returnType} {methodName}({dataTypeName} requestData)"
                );
                builder.AppendLine("    {");
                builder.AppendLine("        ArgumentNullException.ThrowIfNull(requestData);");
                builder.AppendLine(
                    $"        return new(builder.AddRequest(\"{requestType}\", requestData));"
                );
                builder.AppendLine("    }");
            }
            else
            {
                builder.AppendLine(
                    $"    public {returnType} {methodName}() => new(builder.AddRequest(\"{requestType}\", null));"
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

    /// <summary>
    /// Converts a protocol category such as <c>scene items</c> into a PascalCase group name.
    /// </summary>
    private static string ToGroupName(string category) =>
        string.Concat(
            category
                .Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
                .Select(part =>
                    part.Length == 0
                        ? part
                        : char.ToUpperInvariant(part[0]) + part.Substring(1).ToLowerInvariant()
                )
        );

    /// <summary>
    /// Flattens a protocol description onto a single line for a doc comment.
    /// </summary>
    private static string FlattenBatchDescription(string description) =>
        System.Security.SecurityElement.Escape(
            System.Text.RegularExpressions.Regex.Replace(description, @"\s+", " ").Trim()
        ) ?? string.Empty;
}
