using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text; // Required for SourceText

namespace ObsWebSocket.SourceGenerators;

/// <summary>
/// Main entry point for the Emitter logic. Combines helpers and generation methods.
/// Responsible for generating Enums, DTOs (including nested), EventArgs,
/// Client Event Infrastructure, and Client Extension Methods based on the OBS protocol definition.
/// </summary>
internal static partial class Emitter
{
    // Note: Specific generation logic resides in other partial class files.

    #region Enum Generation (Implementation in Emitter.Helpers.cs)

    /// <summary>
    /// Generates source code for all enums defined in the protocol.
    /// </summary>
    public static void GenerateEnums(SourceProductionContext context, ProtocolDefinition protocol)
    {
        if (protocol.Enums is null || protocol.Enums.Count == 0)
        {
            return;
        }

        foreach (EnumDefinition enumDef in protocol.Enums)
        {
            try
            {
                (EnumValueKind valueKind, string? inferredNumericType) =
                    InferEnumValueKindAndNumericType(context, enumDef);
                string? source = valueKind switch
                {
                    EnumValueKind.Numeric => GenerateNumericEnumSource(
                        enumDef,
                        inferredNumericType!
                    ),
                    EnumValueKind.StringBased => GenerateStringBasedEnumClassSource(
                        context,
                        enumDef
                    ),
                    _ => HandleUnknownEnumValueKind(context, enumDef), // Handles Mixed and Unknown
                };
                if (source != null)
                {
                    string suffix =
                        valueKind == EnumValueKind.Numeric ? ".Enum.g.cs" : ".Class.g.cs";
                    context.AddSource(
                        $"{SanitizeIdentifier(enumDef.EnumType)}{suffix}",
                        SourceText.From(source, Encoding.UTF8)
                    );
                }
            }
            catch (Exception ex)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.IdentifierGenerationError,
                        Location.None,
                        enumDef.EnumType,
                        $"Enum generation for {enumDef.EnumType}",
                        ex.Message
                    )
                );
            }
        }
    }

    /// <summary>
    /// Generates the C# source for a standard numeric enum.
    /// </summary>
    private static string? GenerateNumericEnumSource(EnumDefinition enumDef, string underlyingType)
    {
        string enumName = SanitizeIdentifier(enumDef.EnumType);
        StringBuilder builder = BuildSourceHeader($"// Type: Numeric Enum ({underlyingType})");
        builder.AppendLine($"namespace {GeneratedEnumsNamespace};");
        builder.AppendLine();
        AppendXmlDocSummary(
            builder,
            $"Represents the {enumName} options defined in the OBS WebSocket protocol.",
            0
        );
        builder.AppendLine(
            "/// <remarks>Generated from OBS WebSocket Protocol definition.</remarks>"
        );
        if (enumName == "EventSubscription")
        {
            builder.AppendLine("[System.Flags]");
        }

        builder.AppendLine($"public enum {enumName} : {underlyingType}");
        builder.AppendLine("{");
        if (enumDef.EnumIdentifiers != null)
        {
            foreach (EnumIdentifier member in enumDef.EnumIdentifiers)
            {
                string memberName = SanitizeIdentifier(member.IdentifierName);
                if (string.IsNullOrEmpty(memberName))
                {
                    continue;
                }

                string memberValueString = member.EnumValue.GetRawText().Trim('"');
                AppendXmlDocSummary(builder, member.Description, 1);
                builder.AppendLine($"    /// <remarks>");
                builder.AppendLine(
                    $"    /// Initial OBS Websocket Version: {member.InitialVersion}"
                );
                builder.AppendLine($"    /// RPC Version: {member.RpcVersion}");
                if (member.Deprecated)
                {
                    builder.AppendLine($"    /// This member is deprecated.");
                }

                builder.AppendLine($"    /// </remarks>");
                if (member.Deprecated)
                {
                    builder.AppendLine(
                        $"    [System.Obsolete(\"Deprecated in OBS Websocket version {member.InitialVersion}\")]"
                    );
                }

                builder.AppendLine($"    {memberName} = {memberValueString},");
                builder.AppendLine();
            }
        }
        builder.AppendLine("}");
        return builder.ToString();
    }

    /// <summary>
    /// Generates the C# source for a static class containing string constants.
    /// </summary>
    private static string? GenerateStringBasedEnumClassSource(
        SourceProductionContext context,
        EnumDefinition enumDef
    )
    {
        string className = SanitizeIdentifier(enumDef.EnumType);
        StringBuilder builder = BuildSourceHeader($"// Type: String-Constant Class");
        builder.AppendLine($"namespace {GeneratedEnumsNamespace};");
        builder.AppendLine();
        AppendXmlDocSummary(
            builder,
            $"Contains string constants representing the {className} options defined in the OBS WebSocket protocol.",
            0
        );
        builder.AppendLine(
            "/// <remarks>Generated from OBS WebSocket Protocol definition.</remarks>"
        );
        builder.AppendLine($"public static class {className}");
        builder.AppendLine("{");
        if (enumDef.EnumIdentifiers != null)
        {
            foreach (EnumIdentifier member in enumDef.EnumIdentifiers)
            {
                string fieldName = SanitizeIdentifier(member.IdentifierName);
                if (string.IsNullOrEmpty(fieldName))
                {
                    continue;
                }

                if (member.EnumValue.ValueKind != JsonValueKind.String)
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            Diagnostics.MixedEnumValueTypes,
                            Location.None,
                            enumDef.EnumType,
                            $"Internal Error: Expected string value for member '{member.IdentifierName}' in '{enumDef.EnumType}' during string class generation, but got {member.EnumValue.ValueKind}."
                        )
                    );
                    return null;
                }
                string memberValueString = member.EnumValue.GetString() ?? "";
                AppendXmlDocSummary(builder, member.Description, 1);
                builder.AppendLine($"    /// <remarks>");
                builder.AppendLine(
                    $"    /// Initial OBS Websocket Version: {member.InitialVersion}"
                );
                builder.AppendLine($"    /// RPC Version: {member.RpcVersion}");
                if (member.Deprecated)
                {
                    builder.AppendLine($"    /// This member is deprecated.");
                }

                builder.AppendLine(
                    $"    /// Value: \"{System.Security.SecurityElement.Escape(memberValueString)}\""
                );
                builder.AppendLine($"    /// </remarks>");
                if (member.Deprecated)
                {
                    builder.AppendLine(
                        $"    [System.Obsolete(\"Deprecated in OBS Websocket version {member.InitialVersion}\")]"
                    );
                }

                builder.AppendLine(
                    $"    public static readonly string {fieldName} = \"{memberValueString}\";"
                );
                builder.AppendLine();
            }
        }
        builder.AppendLine("}");
        return builder.ToString();
    }

    /// <summary>
    /// Handles the case where the enum value kind could not be determined. Reports an error.
    /// </summary>
    private static string? HandleUnknownEnumValueKind(
        SourceProductionContext context,
        EnumDefinition enumDef
    )
    {
        context.ReportDiagnostic(
            Diagnostic.Create(
                Diagnostics.IdentifierGenerationError,
                Location.None,
                enumDef.EnumType,
                enumDef.EnumType,
                "Could not determine enum value kind. Check protocol.json for invalid or mixed value types."
            )
        );
        return null;
    }

    #endregion

    #region EventArgs Generation (Implementation in Emitter.Helpers.cs)

    /// <summary>
    /// Generates EventArgs classes for all defined OBS events.
    /// </summary>
    public static void GenerateEventArgs(
        SourceProductionContext context,
        ProtocolDefinition protocol
    )
    {
        if (protocol.Events is null || protocol.Events.Count == 0)
        {
            return;
        }

        foreach (OBSEvent eventDef in protocol.Events)
        {
            try
            {
                string? source = GenerateSingleEventArgsSource(context, eventDef);
                if (source != null)
                {
                    string eventArgsName = SanitizeIdentifier(eventDef.EventType + "EventArgs");
                    context.AddSource(
                        $"{eventArgsName}.g.cs",
                        SourceText.From(source, Encoding.UTF8)
                    );
                }
            }
            catch (Exception ex)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.IdentifierGenerationError,
                        Location.None,
                        eventDef.EventType,
                        $"EventArgs generation for {eventDef.EventType}",
                        ex.Message
                    )
                );
            }
        }
    }

    /// <summary>
    /// Generates the C# source for a single EventArgs class corresponding to an OBS event.
    /// </summary>
    private static string? GenerateSingleEventArgsSource(
        SourceProductionContext context,
        OBSEvent eventDef
    )
    {
        try
        {
            string eventArgsName = SanitizeIdentifier(eventDef.EventType + "EventArgs");
            string eventName = SanitizeIdentifier(eventDef.EventType);
            bool hasData = eventDef.DataFields?.Count > 0;
            StringBuilder builder = BuildSourceHeader();
            string? payloadDtoName = null;
            if (hasData)
            {
                payloadDtoName = SanitizeIdentifier(eventDef.EventType + "Payload");
                if (string.IsNullOrEmpty(payloadDtoName))
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            Diagnostics.IdentifierGenerationError,
                            Location.None,
                            eventDef.EventType,
                            $"Could not generate payload DTO name for EventArgs '{eventArgsName}' which has data fields.",
                            "Invalid event type?"
                        )
                    );
                    return null;
                }
                builder.AppendLine($"using {GeneratedEventsNamespace};");
            }
            builder.AppendLine("using ObsWebSocket.Core.Events;");
            builder.AppendLine();
            builder.AppendLine($"namespace {GeneratedEventArgsNamespace};");
            builder.AppendLine();
            AppendXmlDocSummary(
                builder,
                $"Provides event data for the <c>{eventDef.EventType}</c> event.",
                0
            );
            builder.AppendLine("/// <remarks>");
            AppendMultiLineXmlDoc(builder, eventDef.Description, "/// ");
            builder.AppendLine(
                $"/// <para>Requires Subscription: {eventDef.EventSubscription} | Complexity: {eventDef.Complexity}</para>"
            );
            builder.AppendLine(
                $"/// <para>RPC Version: {eventDef.RpcVersion} | Initial Version: {eventDef.InitialVersion}</para>"
            );
            if (eventDef.Deprecated)
            {
                builder.AppendLine($"/// <para>⚠️ This event is deprecated!</para>");
            }

            builder.AppendLine("/// Generated from obs-websocket protocol definition.</remarks>");
            if (eventDef.Deprecated)
            {
                builder.AppendLine(
                    $"[Obsolete(\"Event '{eventDef.EventType}' is deprecated since OBS WebSocket version {eventDef.InitialVersion}\")]"
                );
            }

            if (hasData)
            {
                string fullPayloadDtoName = $"{GeneratedEventsNamespace}.{payloadDtoName}";
                string baseClassName = $"ObsEventEventArgs<{fullPayloadDtoName}>";
                builder.AppendLine(
                    $"/// <param name=\"payload\">The strongly-typed data payload (<see cref=\"{payloadDtoName}\"/>) for this event.</param>"
                );
                builder.AppendLine(
                    $"public sealed partial class {eventArgsName}({payloadDtoName} payload) : {baseClassName}(payload)"
                );
                builder.AppendLine("{");
                builder.AppendLine("}");
            }
            else
            {
                string baseClassName = "ObsEventArgs";
                builder.AppendLine(
                    $"public sealed partial class {eventArgsName} : {baseClassName}"
                );
                builder.AppendLine("{");
                builder.AppendLine(
                    "    /// <summary>Initializes a new instance of the <see cref=\""
                        + eventArgsName
                        + "\"/> class.</summary>"
                );
                builder.AppendLine($"    public {eventArgsName}() {{ }}");
                builder.AppendLine("}");
            }
            return builder.ToString();
        }
        catch (Exception ex)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    Diagnostics.IdentifierGenerationError,
                    Location.None,
                    eventDef.EventType,
                    $"Internal error generating EventArgs source for '{eventDef.EventType}'",
                    ex.ToString()
                )
            );
            return null;
        }
    }

    #endregion

    #region Client Event Infrastructure Generation (Implementation in Emitter.Helpers.cs)

    /// <summary>
    /// Generates the partial class `ObsWebSocketClient` containing event fields and invoker methods.
    /// </summary>
    public static void GenerateClientEventInfrastructure(
        SourceProductionContext context,
        ProtocolDefinition protocol
    )
    {
        if (protocol.Events is null || protocol.Events.Count == 0)
        {
            return;
        }

        StringBuilder builder = BuildSourceHeader();
        builder.AppendLine("using System;");
        builder.AppendLine($"using {GeneratedEventArgsNamespace};");
        builder.AppendLine();
        builder.AppendLine($"namespace {ExtensionsNamespace};");
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine(
            "/// Contains generated event fields and the corresponding invoker methods"
        );
        builder.AppendLine(
            "/// for the <see cref=\"ObsWebSocketClient\"/>, based on the OBS WebSocket protocol definition."
        );
        builder.AppendLine("/// </summary>");
        builder.AppendLine("public sealed partial class ObsWebSocketClient");
        builder.AppendLine("{");
        foreach (OBSEvent eventDef in protocol.Events)
        {
            try
            {
                string eventName = SanitizeIdentifier(eventDef.EventType);
                string eventArgsTypeName = $"{GeneratedEventArgsNamespace}.{eventName}EventArgs";
                builder.AppendLine();
                builder.AppendLine("    /// <summary>");
                builder.AppendLine(
                    $"    /// Occurs when the <c>{eventDef.EventType}</c> event is received from the OBS WebSocket server."
                );
                builder.AppendLine("    /// </summary>");
                builder.AppendLine("    /// <remarks>");
                AppendMultiLineXmlDoc(builder, eventDef.Description, "    /// ");
                builder.AppendLine(
                    $"    /// <para>Requires the <c>{eventDef.EventSubscription}</c> subscription.</para>"
                );
                builder.AppendLine(
                    $"    /// <para>Category: {eventDef.Category} | Complexity: {eventDef.Complexity}</para>"
                );
                builder.AppendLine(
                    $"    /// <para>RPC Version: {eventDef.RpcVersion} | Initial Version: {eventDef.InitialVersion}</para>"
                );
                if (eventDef.Deprecated)
                {
                    builder.AppendLine($"    /// <para>⚠️ This event is deprecated!</para>");
                }

                builder.AppendLine("    /// </remarks>");
                if (eventDef.Deprecated)
                {
                    builder.AppendLine(
                        $"    [Obsolete(\"Event '{eventDef.EventType}' is deprecated since OBS WebSocket version {eventDef.InitialVersion}\")]"
                    );
                }

                builder.AppendLine(
                    $"    public event EventHandler<{eventArgsTypeName}>? {eventName};"
                );
                builder.AppendLine();
                builder.AppendLine(
                    $"    /// <summary>Invokes the <see cref=\"{eventName}\"/> event handler safely.</summary>"
                );
                builder.AppendLine(
                    $"    /// <param name=\"e\">The <see cref=\"{eventArgsTypeName}\"/> containing event data.</param>"
                );
                builder.AppendLine($"    private void On{eventName}({eventArgsTypeName} e)");
                builder.AppendLine("    {");
                builder.AppendLine($"        {eventName}?.Invoke(this, e);");
                builder.AppendLine("    }");
            }
            catch (Exception ex)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.IdentifierGenerationError,
                        Location.None,
                        eventDef.EventType,
                        $"Event infrastructure for {eventDef.EventType}",
                        ex.Message
                    )
                );
            }
        }
        builder.AppendLine("}");
        context.AddSource(
            "ObsWebSocketClient.Events.g.cs",
            SourceText.From(builder.ToString(), Encoding.UTF8)
        );
    }

    #endregion

    #region Client Extension Generation (Implementation in Emitter.Helpers.cs)

    /// <summary>
    /// Generates the static class containing strongly-typed extension methods for sending requests.
    /// </summary>
    public static void GenerateClientExtensions(
        SourceProductionContext context,
        ProtocolDefinition protocol
    )
    {
        if (protocol.Requests is null || protocol.Requests.Count == 0)
        {
            return;
        }

        StringBuilder builder = BuildSourceHeader();
        builder.AppendLine("using System;");
        builder.AppendLine("using System.Collections.Generic;");
        builder.AppendLine("using System.Threading;");
        builder.AppendLine("using System.Threading.Tasks;");
        builder.AppendLine("using ObsWebSocket.Core;");
        builder.AppendLine("using ObsWebSocket.Core.Protocol;");
        builder.AppendLine("using ObsWebSocket.Core.Protocol.Requests;");
        builder.AppendLine("using ObsWebSocket.Core.Protocol.Responses;");
        builder.AppendLine($"using {GeneratedCommonNamespace};");
        builder.AppendLine();
        builder.AppendLine($"namespace {ExtensionsNamespace};");
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine(
            "/// Provides strongly-typed extension methods for the <see cref=\"ObsWebSocketClient\"/>,"
        );
        builder.AppendLine(
            "/// corresponding to the requests defined in the OBS WebSocket v5 protocol."
        );
        builder.AppendLine("/// </summary>");
        builder.AppendLine("public static partial class ObsWebSocketClientExtensions");
        builder.AppendLine("{");
        foreach (RequestDefinition reqDef in protocol.Requests)
        {
            try
            {
                GenerateSingleExtensionMethod(builder, reqDef);
                builder.AppendLine();
            }
            catch (Exception ex)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.IdentifierGenerationError,
                        Location.None,
                        reqDef.RequestType,
                        $"Extension method for {reqDef.RequestType}",
                        ex.Message
                    )
                );
            }
        }
        builder.AppendLine("}");
        context.AddSource(
            "ObsWebSocketClient.Extensions.g.cs",
            SourceText.From(builder.ToString(), Encoding.UTF8)
        );
    }

    /// <summary>
    /// Generates the source code for a single request extension method.
    /// </summary>
    private static void GenerateSingleExtensionMethod(
        StringBuilder builder,
        RequestDefinition reqDef
    )
    {
        string methodName = SanitizeIdentifier(reqDef.RequestType + "Async");
        string requestTypeStringLiteral = $"\"{reqDef.RequestType}\"";
        bool hasRequestData = reqDef.RequestFields?.Count > 0;
        bool hasResponseData = reqDef.ResponseFields?.Count > 0;
        string requestDtoType = hasRequestData
            ? $"{GeneratedRequestsNamespace}.{SanitizeIdentifier(reqDef.RequestType)}RequestData"
            : "";
        string requestParamName = hasRequestData ? "requestData" : "";
        string parameterList = hasRequestData
            ? $"{requestDtoType} {requestParamName}, CancellationToken cancellationToken = default"
            : "CancellationToken cancellationToken = default";
        string responseDtoType = hasResponseData
            ? $"{GeneratedResponsesNamespace}.{SanitizeIdentifier(reqDef.RequestType)}ResponseData"
            : "";
        string returnType;
        string baseCallMethod;
        if (hasResponseData)
        {
            bool responseIsValueType = false; // Assume class/record
            returnType = $"Task<{responseDtoType}?>";
            baseCallMethod = responseIsValueType ? "CallAsyncValue" : "CallAsync";
        }
        else
        {
            returnType = "Task";
            baseCallMethod = "CallAsync";
            responseDtoType = "object"; // Placeholder for CallAsync<T>
        }

        // XML Documentation
        builder.AppendLine("    /// <summary>");
        AppendMultiLineXmlDoc(builder, reqDef.Description, "    ///");
        builder.AppendLine("    /// </summary>");
        builder.AppendLine(
            $"    /// <param name=\"client\">The <see cref=\"ObsWebSocketClient\"/> instance.</param>"
        );
        if (hasRequestData)
        {
            builder.AppendLine(
                $"    /// <param name=\"{requestParamName}\">The data required for the request (<see cref=\"{requestDtoType}\"/>).</param>"
            );
        }

        builder.AppendLine(
            $"    /// <param name=\"cancellationToken\">A token to cancel the asynchronous operation.</param>"
        );

        builder.Append($"    /// <returns>A task representing the asynchronous operation. ");
        if (hasResponseData)
        {
            if (baseCallMethod == "CallAsyncValue")
            {
                builder.Append(
                    $"Yields a nullable <see cref=\"{responseDtoType}\"/> containing the deserialized response data, or <c>null</c> if the successful response does not contain data."
                );
            }
            else // Assumed CallAsync (reference type)
            {
                builder.Append(
                    $"Yields the <see cref=\"{responseDtoType}\"/> response data, or <c>null</c> if the successful response does not contain data."
                );
            }
        }
        else // No response data
        {
            builder.Append("Completes when the request is processed successfully by the server.");
        }
        builder.AppendLine("</returns>");

        builder.AppendLine("    /// <remarks>");
        builder.AppendLine(
            $"    /// <para>OBS WebSocket Protocol Category: {reqDef.Category}</para>"
        );
        builder.AppendLine($"    /// <para>Complexity Rating: {reqDef.Complexity}/5</para>");
        builder.AppendLine(
            $"    /// <para>RPC Version: {reqDef.RpcVersion} | Initial OBS WebSocket Version: {reqDef.InitialVersion}</para>"
        );
        if (reqDef.Deprecated)
        {
            builder.AppendLine($"    /// <para>⚠️ This request is deprecated!</para>");
        }

        builder.AppendLine("    /// Generated from obs-websocket protocol definition.</remarks>");
        builder.AppendLine(
            $"    /// <exception cref=\"ObsWebSocketException\">Thrown if the request fails on the OBS side.</exception>"
        );
        builder.AppendLine(
            $"    /// <exception cref=\"InvalidOperationException\">Thrown if the client is not connected.</exception>"
        );
        builder.AppendLine(
            $"    /// <exception cref=\"OperationCanceledException\">Thrown if cancelled.</exception>"
        );
        // Method Signature
        if (reqDef.Deprecated)
        {
            builder.AppendLine(
                $"    [Obsolete(\"Request '{reqDef.RequestType}' is deprecated since OBS WebSocket version {reqDef.InitialVersion}\")]"
            );
        }

        builder.AppendLine(
            $"    public static async {returnType} {methodName}(this ObsWebSocketClient client, {parameterList})"
        );
        builder.AppendLine("    {");
        // Method Body
        string callParams = hasRequestData ? requestParamName : "null";
        string awaitPrefix = hasResponseData ? "return " : ""; // Add "return " only if there's response data
        builder.AppendLine(
            $"        {awaitPrefix}await client.{baseCallMethod}<{responseDtoType}>({requestTypeStringLiteral}, {callParams}, cancellationToken: cancellationToken).ConfigureAwait(false);"
        );
        builder.AppendLine("    }");
    }

    #endregion
}
