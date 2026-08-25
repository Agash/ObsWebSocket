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
    /// classic event so it can be consumed with <c>await foreach</c>.
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
        builder.AppendLine("/// <summary>");
        builder.AppendLine(
            "/// Observes OBS events as async sequences. Each accessor subscribes for the lifetime"
        );
        builder.AppendLine(
            "/// of the enumeration and unsubscribes when it ends, so the caller never manages handlers."
        );
        builder.AppendLine("/// </summary>");
        builder.AppendLine("public static class ObsWebSocketClientEventStreams");
        builder.AppendLine("{");

        foreach (OBSEvent? eventDef in protocol.Events)
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
                builder.AppendLine(
                    $"    /// Streams <c>{eventName}</c> events as they arrive."
                );
                if (!string.IsNullOrWhiteSpace(eventDef.Description))
                {
                    builder.AppendLine(
                        $"    /// <para>{FlattenDescription(eventDef.Description)}</para>"
                    );
                }

                builder.AppendLine("    /// </summary>");
                builder.AppendLine("    /// <param name=\"client\">The ObsWebSocketClient instance.</param>");
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
                    $"    public static IAsyncEnumerable<{eventArgsTypeName}> {eventName}Stream("
                );
                builder.AppendLine("        this ObsWebSocketClient client,");
                builder.AppendLine("        int capacity = EventStream.DefaultCapacity,");
                builder.AppendLine("        CancellationToken cancellationToken = default)");
                builder.AppendLine("    {");
                builder.AppendLine("        ArgumentNullException.ThrowIfNull(client);");
                builder.AppendLine($"        return EventStream.Create<{eventArgsTypeName}>(");
                builder.AppendLine($"            handler => client.{eventName} += handler,");
                builder.AppendLine($"            handler => client.{eventName} -= handler,");
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
