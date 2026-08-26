// ObsWebSocket.Codegen.Tasks/Generation/Emitter.PayloadSchema.cs
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ObsWebSocket.Codegen.Tasks.Generation;

/// <summary>
/// Emits the wire keys each response record expects, so a payload can be checked against the type
/// it is about to be read as.
/// </summary>
internal static partial class Emitter
{
    /// <summary>
    /// Generates a lookup from response record name to the field names OBS sends for it.
    /// </summary>
    /// <remarks>
    /// Reading a payload as the wrong record is silent on MessagePack, which maps by key name and
    /// leaves everything unmatched at its default, and silent on JSON too for the records that
    /// happen to have no required member. Response records almost never share field names, so
    /// checking that a payload carries at least one key the target record knows catches the
    /// mistake on both transports.
    /// </remarks>
    /// <param name="context">The source production context.</param>
    /// <param name="protocol">The parsed protocol definition.</param>
    public static void GeneratePayloadSchema(
        SourceProductionContext context,
        ProtocolDefinition protocol
    )
    {
        if (protocol.Requests is null || protocol.Requests.Count == 0)
        {
            return;
        }

        StringBuilder builder = BuildSourceHeader("// Wire keys per response record");
        builder.AppendLine("using System;");
        builder.AppendLine("using System.Collections.Generic;");
        builder.AppendLine();
        builder.AppendLine("namespace ObsWebSocket.Core.Serialization;");
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine(
            "/// The field names OBS sends for each response record, used to reject a payload being"
        );
        builder.AppendLine("/// read as a record it did not come from.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine("internal static class ObsWebSocketPayloadSchema");
        builder.AppendLine("{");
        builder.AppendLine(
            "    private static readonly Dictionary<string, string[]> s_keys = new(StringComparer.Ordinal)"
        );
        builder.AppendLine("    {");

        foreach (RequestDefinition reqDef in protocol.Requests)
        {
            List<FieldDefinition> fields = reqDef.ResponseFields ?? [];
            if (fields.Count == 0)
            {
                continue;
            }

            // Nested fields arrive as "parent.child"; only the outermost name is a map key.
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (FieldDefinition field in fields)
            {
                string name = field.ValueName;
                int dot = name.IndexOf('.');
                _ = keys.Add(dot >= 0 ? name.Substring(0, dot) : name);
            }

            string recordName = $"{SanitizeIdentifier(reqDef.RequestType)}ResponseData";
            string list = string.Join(
                ", ",
                keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => $"\"{k}\"")
            );
            builder.AppendLine($"        [\"{recordName}\"] = [{list}],");
        }

        builder.AppendLine("    };");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            "    /// The keys a response record expects, or an empty span when the record is unknown"
        );
        builder.AppendLine("    /// to the schema, in which case no check is possible.");
        builder.AppendLine("    /// </summary>");
        builder.AppendLine(
            "    /// <param name=\"responseTypeName\">The record's type name.</param>"
        );
        builder.AppendLine("    public static string[] KnownKeys(string responseTypeName) =>");
        builder.AppendLine(
            "        s_keys.TryGetValue(responseTypeName, out string[]? keys) ? keys : [];"
        );
        builder.AppendLine("}");

        context.AddSource(
            "ObsWebSocketPayloadSchema.g.cs",
            SourceText.From(builder.ToString(), Encoding.UTF8)
        );
    }
}
