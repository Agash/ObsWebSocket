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

            _ = builder.AppendLine("using System.Collections.Generic;");
            _ = builder.AppendLine("using System.Text.Json;");
            _ = builder.AppendLine("using System.Text.Json.Serialization;");
            _ = builder.AppendLine("using ObsWebSocket.Core.Protocol;");
            _ = builder.AppendLine("using ObsWebSocket.Core.Protocol.Common;");
            _ = builder.AppendLine("using ObsWebSocket.Core.Protocol.Events;");
            _ = builder.AppendLine("using ObsWebSocket.Core.Protocol.Requests;");
            _ = builder.AppendLine("using ObsWebSocket.Core.Protocol.Responses;");
            _ = builder.AppendLine();
            _ = builder.AppendLine("namespace ObsWebSocket.Core.Serialization;");
            _ = builder.AppendLine();
            _ = builder.AppendLine("[JsonSourceGenerationOptions(");
            _ = builder.AppendLine("    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,");
            _ = builder.AppendLine("    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]");

            // Fixed protocol wrapper and payload types.
            _ = builder.AppendLine("[JsonSerializable(typeof(OutgoingMessage<RequestPayload>))]");
            _ = builder.AppendLine("[JsonSerializable(typeof(OutgoingMessage<IdentifyPayload>))]");
            _ = builder.AppendLine("[JsonSerializable(typeof(OutgoingMessage<ReidentifyPayload>))]");
            _ = builder.AppendLine("[JsonSerializable(typeof(OutgoingMessage<RequestBatchPayload>))]");
            _ = builder.AppendLine("[JsonSerializable(typeof(IncomingMessage<JsonElement>))]");
            _ = builder.AppendLine("[JsonSerializable(typeof(RequestResponsePayload<JsonElement>))]");
            _ = builder.AppendLine("[JsonSerializable(typeof(RequestBatchResponsePayload<JsonElement>))]");
            _ = builder.AppendLine("[JsonSerializable(typeof(EventPayloadBase<JsonElement>))]");
            _ = builder.AppendLine("[JsonSerializable(typeof(HelloPayload))]");
            _ = builder.AppendLine("[JsonSerializable(typeof(IdentifiedPayload))]");
            _ = builder.AppendLine("[JsonSerializable(typeof(RequestStatus))]");

            // Handwritten stub types.
            _ = builder.AppendLine("[JsonSerializable(typeof(SceneStub))]");
            _ = builder.AppendLine("[JsonSerializable(typeof(SceneItemStub))]");
            _ = builder.AppendLine("[JsonSerializable(typeof(SceneItemTransformStub))]");
            _ = builder.AppendLine("[JsonSerializable(typeof(FilterStub))]");
            _ = builder.AppendLine("[JsonSerializable(typeof(InputStub))]");
            _ = builder.AppendLine("[JsonSerializable(typeof(TransitionStub))]");
            _ = builder.AppendLine("[JsonSerializable(typeof(OutputStub))]");
            _ = builder.AppendLine("[JsonSerializable(typeof(MonitorStub))]");
            _ = builder.AppendLine("[JsonSerializable(typeof(PropertyItemStub))]");

            // Common collection payload helpers.
            _ = builder.AppendLine("[JsonSerializable(typeof(List<JsonElement>))]");
            _ = builder.AppendLine("[JsonSerializable(typeof(Dictionary<string, bool>))]");
            _ = builder.AppendLine("[JsonSerializable(typeof(Dictionary<string, JsonElement>))]");
            _ = builder.AppendLine(
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
                        _ = builder.AppendLine(
                            $"[JsonSerializable(typeof({GeneratedRequestsNamespace}.{baseName}RequestData))]"
                        );
                    }

                    if (request.ResponseFields?.Count > 0)
                    {
                        _ = builder.AppendLine(
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
                        _ = builder.AppendLine(
                            $"[JsonSerializable(typeof({GeneratedEventsNamespace}.{payloadType}))]"
                        );
                    }
                }
            }

            foreach (string nestedTypeName in s_generatedNestedTypes.Keys.OrderBy(k => k))
            {
                _ = builder.AppendLine(
                    $"[JsonSerializable(typeof({NestedTypesNamespace}.{nestedTypeName}))]"
                );
            }

            _ = builder.AppendLine("internal sealed partial class ObsWebSocketJsonContext : JsonSerializerContext");
            _ = builder.AppendLine("{");
            _ = builder.AppendLine("}");

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
