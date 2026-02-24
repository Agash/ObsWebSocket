using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ObsWebSocket.Codegen.Tasks.Generation;

/// <summary>
/// Contains emitter logic for generating a System.Text.Json source generation context.
/// </summary>
internal static partial class Emitter
{
    /// <summary>
    /// Generates a JsonSerializerContext containing all fixed and generated protocol types.
    /// </summary>
    public static void GenerateJsonSerializerContext(
        SourceProductionContext context,
        ProtocolDefinition protocol
    )
    {
        try
        {
            StringBuilder builder = BuildSourceHeader("// Serialization Context: ObsWebSocketJsonContext");

            builder.AppendLine("using System.Collections.Generic;");
            builder.AppendLine("using System.Text.Json;");
            builder.AppendLine("using System.Text.Json.Serialization;");
            builder.AppendLine("using ObsWebSocket.Core.Protocol;");
            builder.AppendLine("using ObsWebSocket.Core.Protocol.Common;");
            builder.AppendLine("using ObsWebSocket.Core.Protocol.Events;");
            builder.AppendLine("using ObsWebSocket.Core.Protocol.Requests;");
            builder.AppendLine("using ObsWebSocket.Core.Protocol.Responses;");
            builder.AppendLine();
            builder.AppendLine("namespace ObsWebSocket.Core.Serialization;");
            builder.AppendLine();
            builder.AppendLine("[JsonSourceGenerationOptions(");
            builder.AppendLine("    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,");
            builder.AppendLine("    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]");

            // Fixed protocol wrapper and payload types.
            builder.AppendLine("[JsonSerializable(typeof(OutgoingMessage<RequestPayload>))]");
            builder.AppendLine("[JsonSerializable(typeof(OutgoingMessage<IdentifyPayload>))]");
            builder.AppendLine("[JsonSerializable(typeof(OutgoingMessage<ReidentifyPayload>))]");
            builder.AppendLine("[JsonSerializable(typeof(OutgoingMessage<RequestBatchPayload>))]");
            builder.AppendLine("[JsonSerializable(typeof(IncomingMessage<JsonElement>))]");
            builder.AppendLine("[JsonSerializable(typeof(RequestResponsePayload<JsonElement>))]");
            builder.AppendLine("[JsonSerializable(typeof(RequestBatchResponsePayload<JsonElement>))]");
            builder.AppendLine("[JsonSerializable(typeof(EventPayloadBase<JsonElement>))]");
            builder.AppendLine("[JsonSerializable(typeof(HelloPayload))]");
            builder.AppendLine("[JsonSerializable(typeof(IdentifiedPayload))]");
            builder.AppendLine("[JsonSerializable(typeof(RequestStatus))]");

            // Handwritten stub types.
            builder.AppendLine("[JsonSerializable(typeof(SceneStub))]");
            builder.AppendLine("[JsonSerializable(typeof(SceneItemStub))]");
            builder.AppendLine("[JsonSerializable(typeof(SceneItemTransformStub))]");
            builder.AppendLine("[JsonSerializable(typeof(FilterStub))]");
            builder.AppendLine("[JsonSerializable(typeof(InputStub))]");
            builder.AppendLine("[JsonSerializable(typeof(TransitionStub))]");
            builder.AppendLine("[JsonSerializable(typeof(OutputStub))]");
            builder.AppendLine("[JsonSerializable(typeof(MonitorStub))]");
            builder.AppendLine("[JsonSerializable(typeof(PropertyItemStub))]");

            // Common collection payload helpers.
            builder.AppendLine("[JsonSerializable(typeof(List<JsonElement>))]");
            builder.AppendLine("[JsonSerializable(typeof(Dictionary<string, bool>))]");
            builder.AppendLine("[JsonSerializable(typeof(Dictionary<string, JsonElement>))]");
            builder.AppendLine(
                "[JsonSerializable(typeof(List<RequestResponsePayload<JsonElement>>))]"
            );

            // Generated request/response/event DTOs.
            if (protocol.Requests is not null)
            {
                foreach (RequestDefinition request in protocol.Requests)
                {
                    string baseName = SanitizeIdentifier(request.RequestType);
                    if (request.RequestFields?.Count > 0)
                    {
                        builder.AppendLine(
                            $"[JsonSerializable(typeof({GeneratedRequestsNamespace}.{baseName}RequestData))]"
                        );
                    }

                    if (request.ResponseFields?.Count > 0)
                    {
                        builder.AppendLine(
                            $"[JsonSerializable(typeof({GeneratedResponsesNamespace}.{baseName}ResponseData))]"
                        );
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
                        builder.AppendLine(
                            $"[JsonSerializable(typeof({GeneratedEventsNamespace}.{payloadType}))]"
                        );
                    }
                }
            }

            foreach (string nestedTypeName in s_generatedNestedTypes.Keys.OrderBy(k => k))
            {
                builder.AppendLine(
                    $"[JsonSerializable(typeof({NestedTypesNamespace}.{nestedTypeName}))]"
                );
            }

            builder.AppendLine("internal sealed partial class ObsWebSocketJsonContext : JsonSerializerContext");
            builder.AppendLine("{");
            builder.AppendLine("}");

            context.AddSource(
                "ObsWebSocketJsonContext.g.cs",
                SourceText.From(builder.ToString(), Encoding.UTF8)
            );
        }
        catch (Exception ex)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    Diagnostics.IdentifierGenerationError,
                    Location.None,
                    "ObsWebSocketJsonContext",
                    "JsonSerializerContext generation",
                    ex.ToString()
                )
            );
        }
    }
}
