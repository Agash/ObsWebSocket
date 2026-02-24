using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ObsWebSocket.Codegen.Tasks.Generation;

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
            List<string> fixedTypeNames = [];
            List<string> requestTypeNames = [];
            List<string> responseTypeNames = [];
            List<string> eventTypeNames = [];
            List<string> nestedTypeNames = [];
            HashSet<string> seen = new(StringComparer.Ordinal);

            void AddFixedType(string typeName)
            {
                if (seen.Add(typeName))
                {
                    fixedTypeNames.Add(typeName);
                }
            }

            AddFixedType("ObsWebSocket.Core.Protocol.HelloPayload");
            AddFixedType("ObsWebSocket.Core.Protocol.AuthenticationData");
            AddFixedType("ObsWebSocket.Core.Protocol.IdentifyPayload");
            AddFixedType("ObsWebSocket.Core.Protocol.IdentifiedPayload");
            AddFixedType("ObsWebSocket.Core.Protocol.ReidentifyPayload");
            AddFixedType("ObsWebSocket.Core.Protocol.RequestPayload");
            AddFixedType("ObsWebSocket.Core.Protocol.RequestBatchPayload");
            AddFixedType("ObsWebSocket.Core.Protocol.RequestStatus");

            if (protocol.Requests is not null)
            {
                foreach (RequestDefinition request in protocol.Requests)
                {
                    string baseName = SanitizeIdentifier(request.RequestType);
                    if (request.RequestFields?.Count > 0)
                    {
                        string typeName = $"{GeneratedRequestsNamespace}.{baseName}RequestData";
                        if (seen.Add(typeName))
                        {
                            requestTypeNames.Add(typeName);
                        }
                    }

                    if (request.ResponseFields?.Count > 0)
                    {
                        string typeName = $"{GeneratedResponsesNamespace}.{baseName}ResponseData";
                        if (seen.Add(typeName))
                        {
                            responseTypeNames.Add(typeName);
                        }
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
                        string typeName = $"{GeneratedEventsNamespace}.{payloadType}";
                        if (seen.Add(typeName))
                        {
                            eventTypeNames.Add(typeName);
                        }
                    }
                }
            }

            foreach (string nestedTypeName in s_generatedNestedTypes.Keys.OrderBy(k => k))
            {
                string typeName = $"{NestedTypesNamespace}.{nestedTypeName}";
                if (seen.Add(typeName))
                {
                    nestedTypeNames.Add(typeName);
                }
            }

            context.AddSource(
                "ObsWebSocketMsgPackResolver.g.cs",
                SourceText.From(BuildResolverRootSource(), Encoding.UTF8)
            );

            context.AddSource(
                "ObsWebSocketMsgPackResolver.FixedTypes.g.cs",
                SourceText.From(BuildKnownTypeSource("IsFixedType", fixedTypeNames), Encoding.UTF8)
            );

            context.AddSource(
                "ObsWebSocketMsgPackResolver.RequestTypes.g.cs",
                SourceText.From(BuildKnownTypeSource("IsRequestType", requestTypeNames), Encoding.UTF8)
            );

            context.AddSource(
                "ObsWebSocketMsgPackResolver.ResponseTypes.g.cs",
                SourceText.From(BuildKnownTypeSource("IsResponseType", responseTypeNames), Encoding.UTF8)
            );

            context.AddSource(
                "ObsWebSocketMsgPackResolver.EventTypes.g.cs",
                SourceText.From(BuildKnownTypeSource("IsEventType", eventTypeNames), Encoding.UTF8)
            );

            context.AddSource(
                "ObsWebSocketMsgPackResolver.NestedTypes.g.cs",
                SourceText.From(BuildKnownTypeSource("IsNestedType", nestedTypeNames), Encoding.UTF8)
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

    private static string BuildResolverRootSource()
    {
        StringBuilder builder = BuildSourceHeader("// Serialization Resolver: ObsWebSocketMsgPackResolver");
        builder.AppendLine("using System;");
        builder.AppendLine("using MessagePack;");
        builder.AppendLine("using MessagePack.Formatters;");
        builder.AppendLine("using MessagePack.Resolvers;");
        builder.AppendLine();
        builder.AppendLine("namespace ObsWebSocket.Core.Serialization;");
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine("/// MessagePack resolver for OBS WebSocket protocol DTOs.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine("public sealed class ObsWebSocketMsgPackResolver : IFormatterResolver");
        builder.AppendLine("{");
        builder.AppendLine("    /// <summary>");
        builder.AppendLine("    /// Singleton resolver instance.");
        builder.AppendLine("    /// </summary>");
        builder.AppendLine(
            "    public static readonly IFormatterResolver Instance = new ObsWebSocketMsgPackResolver();"
        );
        builder.AppendLine();
        builder.AppendLine("    private ObsWebSocketMsgPackResolver() { }");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>");
        builder.AppendLine("    /// Gets a formatter for <typeparamref name=\"T\"/> when this resolver supports it.");
        builder.AppendLine("    /// </summary>");
        builder.AppendLine(
            "    public IMessagePackFormatter<T>? GetFormatter<T>() => ObsWebSocketMsgPackResolverCore.GetFormatter<T>();"
        );
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("internal static partial class ObsWebSocketMsgPackResolverCore");
        builder.AppendLine("{");
        builder.AppendLine("    public static IMessagePackFormatter<T>? GetFormatter<T>()");
        builder.AppendLine("    {");
        builder.AppendLine("        Type type = typeof(T);");
        builder.AppendLine("        if (!IsKnownType(type))");
        builder.AppendLine("        {");
        builder.AppendLine("            return null;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        return SourceGeneratedFormatterResolver.Instance.GetFormatter<T>();");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static bool IsKnownType(Type type)");
        builder.AppendLine("    {");
        builder.AppendLine(
            "        return IsFixedType(type) || IsRequestType(type) || IsResponseType(type) || IsEventType(type) || IsNestedType(type);"
        );
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static partial bool IsFixedType(Type type);");
        builder.AppendLine("    private static partial bool IsRequestType(Type type);");
        builder.AppendLine("    private static partial bool IsResponseType(Type type);");
        builder.AppendLine("    private static partial bool IsEventType(Type type);");
        builder.AppendLine("    private static partial bool IsNestedType(Type type);");
        builder.AppendLine("}");

        return builder.ToString();
    }

    private static string BuildKnownTypeSource(string methodName, List<string> typeNames)
    {
        StringBuilder builder = BuildSourceHeader("// Serialization Resolver Type Map");
        builder.AppendLine("using System;");
        builder.AppendLine();
        builder.AppendLine("namespace ObsWebSocket.Core.Serialization;");
        builder.AppendLine();
        builder.AppendLine("internal static partial class ObsWebSocketMsgPackResolverCore");
        builder.AppendLine("{");
        builder.AppendLine($"    private static partial bool {methodName}(Type type)");
        builder.AppendLine("    {");

        if (typeNames.Count == 0)
        {
            builder.AppendLine("        return false;");
        }
        else
        {
            builder.AppendLine("        return");
            for (int i = 0; i < typeNames.Count; i++)
            {
                string suffix = i == typeNames.Count - 1 ? ";" : " ||";
                builder.AppendLine($"            type == typeof({typeNames[i]}){suffix}");
            }
        }

        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }
}
