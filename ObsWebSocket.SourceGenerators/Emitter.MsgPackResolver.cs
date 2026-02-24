using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ObsWebSocket.SourceGenerators;

/// <summary>
/// Contains emitter logic for generating a MessagePack resolver for generated protocol types.
/// </summary>
internal static partial class Emitter
{
    /// <summary>
    /// Generates a MessagePack formatter resolver for generated request/response/event and nested DTOs.
    /// </summary>
    public static void GenerateMsgPackResolver(
        SourceProductionContext context,
        ProtocolDefinition protocol
    )
    {
        try
        {
            StringBuilder builder = BuildSourceHeader("// Serialization Resolver: ObsWebSocketMsgPackResolver");

            builder.AppendLine("using System;");
            builder.AppendLine("using MessagePack;");
            builder.AppendLine("using MessagePack.Formatters;");
            builder.AppendLine("using MessagePack.Resolvers;");
            builder.AppendLine("using ObsWebSocket.Core.Protocol.Events;");
            builder.AppendLine("using ObsWebSocket.Core.Protocol.Requests;");
            builder.AppendLine("using ObsWebSocket.Core.Protocol.Responses;");
            builder.AppendLine();
            builder.AppendLine("namespace ObsWebSocket.Core.Serialization;");
            builder.AppendLine();
            builder.AppendLine(
                "// Manual formatter path: MessagePack's own source-generated resolver is not configured in this project."
            );
            builder.AppendLine("public sealed class ObsWebSocketMsgPackResolver : IFormatterResolver");
            builder.AppendLine("{");
            builder.AppendLine(
                "    public static readonly IFormatterResolver Instance = new ObsWebSocketMsgPackResolver();"
            );
            builder.AppendLine();
            builder.AppendLine("    private ObsWebSocketMsgPackResolver() { }");
            builder.AppendLine();
            builder.AppendLine(
                "    public IMessagePackFormatter<T>? GetFormatter<T>() => ObsWebSocketMsgPackResolverCore.GetFormatter<T>();"
            );
            builder.AppendLine("}");
            builder.AppendLine();
            builder.AppendLine("internal static partial class ObsWebSocketMsgPackResolverCore");
            builder.AppendLine("{");
            builder.AppendLine(
                "    private static readonly MessagePackSerializerOptions s_fallbackOptions = MessagePackSerializerOptions.Standard.WithResolver(CompositeResolver.Create(PrimitiveObjectResolver.Instance, StandardResolver.Instance));"
            );
            builder.AppendLine();
            builder.AppendLine("    public static IMessagePackFormatter<T>? GetFormatter<T>()");
            builder.AppendLine("    {");
            builder.AppendLine("        Type type = typeof(T);");

            List<string> typeNames = [];
            HashSet<string> seen = new(StringComparer.Ordinal);

            void AddType(string typeName)
            {
                if (seen.Add(typeName))
                {
                    typeNames.Add(typeName);
                }
            }

            if (protocol.Requests is not null)
            {
                foreach (RequestDefinition request in protocol.Requests)
                {
                    string baseName = SanitizeIdentifier(request.RequestType);
                    if (request.RequestFields?.Count > 0)
                    {
                        AddType($"{GeneratedRequestsNamespace}.{baseName}RequestData");
                    }

                    if (request.ResponseFields?.Count > 0)
                    {
                        AddType($"{GeneratedResponsesNamespace}.{baseName}ResponseData");
                    }
                }
            }

            if (protocol.Events is not null)
            {
                foreach (OBSEvent eventDef in protocol.Events)
                {
                    if (eventDef.DataFields?.Count > 0)
                    {
                        string payloadType = SanitizeIdentifier(eventDef.EventType) + "Payload";
                        AddType($"{GeneratedEventsNamespace}.{payloadType}");
                    }
                }
            }

            foreach (string nestedTypeName in s_generatedNestedTypes.Keys.OrderBy(k => k))
            {
                AddType($"{NestedTypesNamespace}.{nestedTypeName}");
            }

            foreach (string typeName in typeNames)
            {
                string formatterName = GetFormatterName(typeName);
                builder.AppendLine($"        if (type == typeof({typeName}))");
                builder.AppendLine("        {");
                builder.AppendLine(
                    $"            return (IMessagePackFormatter<T>)(object)new {formatterName}();"
                );
                builder.AppendLine("        }");
            }

            builder.AppendLine();
            builder.AppendLine("        return null;");
            builder.AppendLine("    }");
            builder.AppendLine();

            foreach (string typeName in typeNames)
            {
                string formatterName = GetFormatterName(typeName);
                builder.AppendLine(
                    $"    private sealed class {formatterName} : IMessagePackFormatter<{typeName}>"
                );
                builder.AppendLine("    {");
                builder.AppendLine(
                    $"        public void Serialize(ref MessagePackWriter writer, {typeName} value, MessagePackSerializerOptions options)"
                );
                builder.AppendLine("        {");
                builder.AppendLine(
                    "            MessagePackSerializer.Serialize(ref writer, value, s_fallbackOptions);"
                );
                builder.AppendLine("        }");
                builder.AppendLine();
                builder.AppendLine(
                    $"        public {typeName} Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)"
                );
                builder.AppendLine("        {");
                builder.AppendLine(
                    $"            return MessagePackSerializer.Deserialize<{typeName}>(ref reader, s_fallbackOptions);"
                );
                builder.AppendLine("        }");
                builder.AppendLine("    }");
                builder.AppendLine();
            }

            builder.AppendLine("}");

            context.AddSource(
                "ObsWebSocketMsgPackResolver.g.cs",
                SourceText.From(builder.ToString(), Encoding.UTF8)
            );
        }
        catch (Exception ex)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    Diagnostics.IdentifierGenerationError,
                    Location.None,
                    "ObsWebSocketMsgPackResolver",
                    "MessagePack resolver generation",
                    ex.ToString()
                )
            );
        }
    }

    private static string GetFormatterName(string fullTypeName)
    {
        StringBuilder nameBuilder = new(fullTypeName.Length + 18);
        nameBuilder.Append("GeneratedFormatter_");
        foreach (char c in fullTypeName)
        {
            nameBuilder.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return SanitizeIdentifier(nameBuilder.ToString());
    }
}
