// ObsWebSocket.Codegen.Tasks/Generation/Emitter.EventStreams.cs
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ObsWebSocket.Codegen.Tasks.Generation;

/// <summary>
/// Emitter logic for the per-event <see cref="IAsyncEnumerable{T}"/> stream accessors.
/// </summary>
internal static partial class Emitter
{
    /// <summary>
    /// Generates one stream accessor per protocol event, each wrapping the corresponding
    /// classic event so it can be consumed with <c>await foreach</c>. The accessors go onto the
    /// same category group as that category's requests, because the protocol documents requests
    /// and events under one set of category headings.
    /// </summary>
    /// <param name="context">The source production context.</param>
    /// <param name="protocol">The parsed protocol definition.</param>
    public static void GenerateEventStreams(
        SourceProductionContext context,
        ProtocolDefinition protocol
    )
    {
        if (protocol.Events is null || protocol.Events.Count == 0)
        {
            return;
        }

        HashSet<string> requestCategories = new(StringComparer.OrdinalIgnoreCase);
        foreach (RequestDefinition request in protocol.Requests ?? [])
        {
            _ = requestCategories.Add(request.Category ?? "general");
        }

        StringBuilder builder = BuildSourceHeader("// Helper: per-event IAsyncEnumerable streams");
        builder.AppendLine("using System;");
        builder.AppendLine("using System.Collections.Generic;");
        builder.AppendLine("using System.Threading;");
        builder.AppendLine("using ObsWebSocket.Core;");
        builder.AppendLine("using ObsWebSocket.Core.Events;");
        builder.AppendLine($"using {GeneratedEventArgsNamespace};");
        builder.AppendLine();
        builder.AppendLine($"namespace {ExtensionsNamespace};");
        builder.AppendLine();

        foreach (
            IGrouping<string, OBSEvent> group in protocol
                .Events.GroupBy(e => e.Category ?? "general", StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
        )
        {
            string groupName = ToGroupName(group.Key);

            builder.AppendLine("/// <summary>");
            builder.AppendLine(
                $"/// Events in the <c>{System.Security.SecurityElement.Escape(group.Key)}</c> category, as async sequences."
            );
            builder.AppendLine(
                "/// Each accessor subscribes for the lifetime of the enumeration and unsubscribes"
            );
            builder.AppendLine(
                "/// when it ends, so the caller never manages handlers."
            );
            builder.AppendLine("/// </summary>");

            // A category with events but no requests has no group declared elsewhere, so this
            // part has to carry the primary constructor.
            if (requestCategories.Contains(group.Key))
            {
                builder.AppendLine($"public readonly partial struct {groupName}Group");
            }
            else
            {
                builder.AppendLine(
                    "/// <param name=\"client\">The client these events are observed on.</param>"
                );
                builder.AppendLine(
                    $"public readonly partial struct {groupName}Group(ObsWebSocketClient client)"
                );
            }

            builder.AppendLine("{");

            foreach (OBSEvent eventDef in group)
            {
                try
                {
                    string eventName = SanitizeIdentifier(eventDef.EventType);
                    if (string.IsNullOrEmpty(eventName))
                    {
                        continue;
                    }

                    string eventArgsTypeName = $"{GeneratedEventArgsNamespace}.{eventName}EventArgs";

                    builder.AppendLine("    /// <summary>");
                    builder.AppendLine($"    /// Streams <c>{eventName}</c> events as they arrive.");
                    if (!string.IsNullOrWhiteSpace(eventDef.Description))
                    {
                        builder.AppendLine(
                            $"    /// <para>{FlattenDescription(eventDef.Description)}</para>"
                        );
                    }

                    builder.AppendLine("    /// </summary>");
                    builder.AppendLine(
                        "    /// <param name=\"capacity\">Events buffered before the oldest is dropped.</param>"
                    );
                    builder.AppendLine(
                        "    /// <param name=\"cancellationToken\">Ends the enumeration and unsubscribes.</param>"
                    );
                    if (!string.IsNullOrWhiteSpace(eventDef.EventSubscription))
                    {
                        builder.AppendLine(
                            $"    /// <remarks>Requires the <c>{System.Security.SecurityElement.Escape(eventDef.EventSubscription)}</c> subscription.</remarks>"
                        );
                    }

                    builder.AppendLine(
                        $"    public IAsyncEnumerable<{eventArgsTypeName}> {eventName}Stream("
                    );
                    builder.AppendLine("        int capacity = EventStream.DefaultCapacity,");
                    builder.AppendLine("        CancellationToken cancellationToken = default)");
                    builder.AppendLine("    {");
                    builder.AppendLine("        ObsWebSocketClient source = client;");
                    builder.AppendLine($"        return EventStream.Create<{eventArgsTypeName}>(");
                    builder.AppendLine($"            handler => source.{eventName} += handler,");
                    builder.AppendLine($"            handler => source.{eventName} -= handler,");
                    builder.AppendLine("            capacity,");
                    builder.AppendLine("            cancellationToken);");
                    builder.AppendLine("    }");
                    builder.AppendLine();
                }
                catch (Exception ex)
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            Diagnostics.IdentifierGenerationError,
                            Location.None,
                            eventDef.EventType,
                            $"Generating event stream for {eventDef.EventType}",
                            ex.Message
                        )
                    );
                }
            }

            builder.AppendLine("}");
            builder.AppendLine();
        }

        context.AddSource(
            "ObsWebSocketClient.EventStreams.g.cs",
            SourceText.From(builder.ToString(), Encoding.UTF8)
        );
    }

    /// <summary>
    /// Flattens a protocol description onto a single line so it can sit inside a
    /// <c>&lt;para&gt;</c> element without breaking the surrounding doc comment.
    /// </summary>
    private static string FlattenDescription(string description) =>
        System.Security.SecurityElement.Escape(
            System.Text.RegularExpressions.Regex.Replace(description, @"\s+", " ").Trim()
        ) ?? string.Empty;
}
