using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text; // Required for SourceText

namespace ObsWebSocket.Codegen.Tasks.Generation;

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

                // String-valued protocol enums also get a real C# enum, so callers can switch
                // on them and pass them to helpers instead of threading magic strings around.
                if (valueKind == EnumValueKind.StringBased)
                {
                    string? typedSource = GenerateStringBasedEnumTypedSource(enumDef);
                    if (typedSource != null)
                    {
                        context.AddSource(
                            $"{StripObsPrefix(SanitizeIdentifier(enumDef.EnumType))}.TypedEnum.g.cs",
                            SourceText.From(typedSource, Encoding.UTF8)
                        );
                    }
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
                    $"    public const string {fieldName} = \"{memberValueString}\";"
                );
                builder.AppendLine();
            }
        }
        builder.AppendLine("}");
        return builder.ToString();
    }

    /// <summary>
    /// Drops the leading <c>Obs</c> from a protocol enum type name, so <c>ObsMediaInputAction</c>
    /// yields <c>MediaInputAction</c> and does not collide with the string-constant class.
    /// </summary>
    private static string StripObsPrefix(string typeName) =>
        typeName.StartsWith("Obs", StringComparison.Ordinal) && typeName.Length > 3
            ? typeName.Substring(3)
            : typeName + "Value";

    /// <summary>
    /// Finds the longest shared underscore-delimited prefix across the member identifiers, so
    /// the emitted enum members can drop the repeated protocol scaffolding from their names.
    /// </summary>
    private static string FindCommonMemberPrefix(List<string> identifiers)
    {
        if (identifiers.Count < 2)
        {
            return string.Empty;
        }

        string[] first = identifiers[0].Split('_');
        int shared = first.Length;
        foreach (string identifier in identifiers)
        {
            string[] parts = identifier.Split('_');
            int i = 0;
            while (i < shared && i < parts.Length && string.Equals(parts[i], first[i], StringComparison.Ordinal))
            {
                i++;
            }

            shared = i;
        }

        // Never strip everything; each member needs at least one segment left to name it.
        while (shared > 0 && identifiers.Any(id => id.Split('_').Length <= shared))
        {
            shared--;
        }

        return shared == 0 ? string.Empty : string.Join("_", first.Take(shared)) + "_";
    }

    /// <summary>
    /// Converts an UPPER_SNAKE_CASE protocol identifier into a PascalCase C# member name.
    /// </summary>
    private static string SnakeToPascalCase(string upperSnake) =>
        string.Concat(
            upperSnake
                .Split(['_'], StringSplitOptions.RemoveEmptyEntries)
                .Select(part =>
                    part.Length == 0
                        ? part
                        : char.ToUpperInvariant(part[0]) + part.Substring(1).ToLowerInvariant()
                )
        );

    /// <summary>
    /// Generates a real C# enum for a string-valued protocol enum, mapping each member back to
    /// its wire string. The string-constant class stays as-is for low-level and raw payload use.
    /// </summary>
    private static string? GenerateStringBasedEnumTypedSource(EnumDefinition enumDef)
    {
        if (enumDef.EnumIdentifiers is null || enumDef.EnumIdentifiers.Count == 0)
        {
            return null;
        }

        string constantsClass = SanitizeIdentifier(enumDef.EnumType);
        string enumName = StripObsPrefix(constantsClass);

        List<(string Member, string Wire)> members = [];
        foreach (EnumIdentifier member in enumDef.EnumIdentifiers)
        {
            if (member.EnumValue.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string wire = member.EnumValue.GetString() ?? string.Empty;
            if (wire.Length == 0)
            {
                continue;
            }

            members.Add((SanitizeIdentifier(member.IdentifierName), wire));
        }

        if (members.Count == 0)
        {
            return null;
        }

        string prefix = FindCommonMemberPrefix([.. members.Select(m => m.Member)]);

        StringBuilder builder = BuildSourceHeader("// Type: Typed Enum for a string-valued protocol enum");
        builder.AppendLine("using System;");
        builder.AppendLine("using System.Text.Json.Serialization;");
        builder.AppendLine();
        builder.AppendLine($"namespace {GeneratedEnumsNamespace};");
        builder.AppendLine();
        AppendXmlDocSummary(
            builder,
            $"Typed form of the {constantsClass} protocol enum. Use <see cref=\"{enumName}Extensions.ToWireValue\"/> to obtain the string OBS expects.",
            0
        );
        builder.AppendLine("/// <remarks>Generated from OBS WebSocket Protocol definition.</remarks>");
        builder.AppendLine($"public enum {enumName}");
        builder.AppendLine("{");
        foreach ((string memberIdentifier, string wire) in members)
        {
            string shortName = memberIdentifier;
            if (prefix.Length > 0 && shortName.StartsWith(prefix, StringComparison.Ordinal))
            {
                shortName = shortName.Substring(prefix.Length);
            }

            string memberName = SnakeToPascalCase(shortName);
            builder.AppendLine($"    /// <summary>Maps to <c>{System.Security.SecurityElement.Escape(wire)}</c>.</summary>");
            builder.AppendLine($"    [JsonStringEnumMemberName(\"{wire}\")]");
            builder.AppendLine($"    {memberName},");
            builder.AppendLine();
        }

        builder.AppendLine("}");
        builder.AppendLine();

        AppendXmlDocSummary(builder, $"Wire-value conversions for <see cref=\"{enumName}\"/>.", 0);
        builder.AppendLine($"public static class {enumName}Extensions");
        builder.AppendLine("{");
        builder.AppendLine($"    /// <summary>Returns the protocol string OBS expects for this value.</summary>");
        builder.AppendLine($"    public static string ToWireValue(this {enumName} value) => value switch");
        builder.AppendLine("    {");
        foreach ((string memberIdentifier, string wire) in members)
        {
            string shortName = memberIdentifier;
            if (prefix.Length > 0 && shortName.StartsWith(prefix, StringComparison.Ordinal))
            {
                shortName = shortName.Substring(prefix.Length);
            }

            builder.AppendLine($"        {enumName}.{SnakeToPascalCase(shortName)} => {constantsClass}.{memberIdentifier},");
        }

        builder.AppendLine($"        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),");
        builder.AppendLine("    };");
        builder.AppendLine();
        builder.AppendLine($"    /// <summary>Parses a protocol string into a <see cref=\"{enumName}\"/>, returning null when unrecognised.</summary>");
        builder.AppendLine($"    public static {enumName}? FromWireValue(string? value) => value switch");
        builder.AppendLine("    {");
        foreach ((string memberIdentifier, string wire) in members)
        {
            string shortName = memberIdentifier;
            if (prefix.Length > 0 && shortName.StartsWith(prefix, StringComparison.Ordinal))
            {
                shortName = shortName.Substring(prefix.Length);
            }

            builder.AppendLine($"        {constantsClass}.{memberIdentifier} => {enumName}.{SnakeToPascalCase(shortName)},");
        }

        builder.AppendLine("        _ => null,");
        builder.AppendLine("    };");
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
                $"/// Requests in the <c>{System.Security.SecurityElement.Escape(group.Key)}</c> category."
            );
            builder.AppendLine("/// </summary>");
            builder.AppendLine("/// <param name=\"client\">The client these requests are sent on.</param>");
            builder.AppendLine(
                $"public readonly partial struct {groupName}Group(ObsWebSocketClient client)"
            );
            builder.AppendLine("{");

            foreach (RequestDefinition reqDef in group)
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
            builder.AppendLine();
        }

        builder.AppendLine("/// <summary>");
        builder.AppendLine("/// Exposes the request categories defined by the OBS WebSocket protocol.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine("public static class ObsWebSocketClientExtensions");
        builder.AppendLine("{");
        foreach ((string category, string groupName) in groups)
        {
            builder.AppendLine("    extension(ObsWebSocketClient client)");
            builder.AppendLine("    {");
            builder.AppendLine("        /// <summary>");
            builder.AppendLine(
                $"        /// Requests in the <c>{System.Security.SecurityElement.Escape(category)}</c> category."
            );
            builder.AppendLine("        /// </summary>");
            builder.AppendLine(
                $"        public {groupName}Group {groupName} => new(client);"
            );
            builder.AppendLine("    }");
            builder.AppendLine();
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
            returnType = $"Task<{responseDtoType}>";
            baseCallMethod = responseIsValueType ? "CallAsyncValue" : "CallRequiredAsync";
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
                    $"Yields the <see cref=\"{responseDtoType}\"/> response data."
                );
            }
            else // Assumed CallAsync (reference type)
            {
                builder.Append(
                    $"Yields the <see cref=\"{responseDtoType}\"/> response data."
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
            $"    public async {returnType} {methodName}({parameterList})"
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
