using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ObsWebSocket.SourceGenerators;

/// <summary>
/// Contains logic for generating Data Transfer Object (DTO) records, including handling nested records.
/// </summary>
internal static partial class Emitter
{
    // Store generated nested type names globally to prevent duplicates
    private static readonly ConcurrentDictionary<string, bool> s_generatedNestedTypes = new();
    private const string NestedTypesNamespace = $"{GeneratedCommonNamespace}.NestedTypes";

    /// <summary>
    /// Scans the protocol and generates all unique nested record types first.
    /// </summary>
    public static void PreGenerateNestedDtos(
        SourceProductionContext context,
        ProtocolDefinition protocol
    )
    {
        s_generatedNestedTypes.Clear(); // Clear for fresh generation run

        // Use a local function to avoid repeating the processing logic
        void ProcessFieldsForNested(List<FieldDefinition>? fields, string baseName)
        {
            if (fields == null || fields.Count == 0)
            {
                return;
            }

            // Only build hierarchy if dot notation is present to find nested objects
            if (fields.Any(f => f.ValueName.Contains('.')))
            {
                try
                {
                    ProtocolObjectNode? hierarchy = BuildObjectHierarchy(fields, baseName, context);
                    if (hierarchy == null)
                    {
                        return;
                    }

                    GenerateNestedRecordsRecursive(context, hierarchy, baseName, fields); // Pass original fields
                }
                catch (Exception ex)
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            Diagnostics.IdentifierGenerationError,
                            Location.None,
                            baseName,
                            $"Nested DTO pre-generation for {baseName}",
                            ex.ToString()
                        )
                    );
                }
            }
        }

        if (protocol.Requests != null)
        {
            foreach (RequestDefinition reqDef in protocol.Requests)
            {
                ProcessFieldsForNested(
                    reqDef.RequestFields,
                    SanitizeIdentifier(reqDef.RequestType) + "RequestData"
                );
                ProcessFieldsForNested(
                    reqDef.ResponseFields,
                    SanitizeIdentifier(reqDef.RequestType) + "ResponseData"
                );
            }
        }

        if (protocol.Events != null)
        {
            foreach (OBSEvent eventDef in protocol.Events)
            {
                ProcessFieldsForNested(
                    eventDef.DataFields,
                    SanitizeIdentifier(eventDef.EventType) + "Payload"
                );
            }
        }
    }

    /// <summary>
    /// Recursively finds and generates nested records from a hierarchy node.
    /// </summary>
    private static void GenerateNestedRecordsRecursive(
        SourceProductionContext context,
        ProtocolObjectNode node,
        string parentTypeName,
        List<FieldDefinition> originalParentFields
    )
    {
        foreach (KeyValuePair<string, ProtocolNode> kvp in node.Children.OrderBy(c => c.Key)) // Order for stable generation
        {
            if (kvp.Value is ProtocolObjectNode objectNode)
            {
                string nestedRecordName =
                    $"{parentTypeName}_{ToPascalCase(objectNode.SanitizedName)}";
                if (s_generatedNestedTypes.TryAdd(nestedRecordName, true))
                {
                    try
                    {
                        // Pass null for originalFields as nested types use alphabetical order
                        string? source = GenerateRecordSource(
                            context,
                            objectNode,
                            NestedTypesNamespace,
                            nestedRecordName,
                            null,
                            null,
                            true,
                            null
                        );
                        if (source != null)
                        {
                            context.AddSource(
                                $"{nestedRecordName}.g.cs",
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
                                nestedRecordName,
                                $"Generating nested record {nestedRecordName}",
                                ex.ToString()
                            )
                        );
                    }
                }
                // Recurse, passing the original list down for context if needed later
                GenerateNestedRecordsRecursive(
                    context,
                    objectNode,
                    nestedRecordName,
                    originalParentFields
                );
            }
        }
    }

    /// <summary>
    /// Generates source code for all request DTOs. Assumes nested types are pre-generated.
    /// </summary>
    public static void GenerateRequestDtos(
        SourceProductionContext context,
        ProtocolDefinition protocol
    )
    {
        if (protocol.Requests is null)
        {
            return;
        }

        foreach (RequestDefinition reqDef in protocol.Requests)
        {
            if (reqDef.RequestFields?.Count > 0)
            {
                string baseName = SanitizeIdentifier(reqDef.RequestType);
                string dtoName = baseName + "RequestData";
                try
                {
                    ProtocolObjectNode? hierarchy = BuildObjectHierarchy(
                        reqDef.RequestFields,
                        dtoName,
                        context
                    );
                    if (hierarchy == null)
                    {
                        continue;
                    }
                    // Pass the original list
                    string? source = GenerateRecordSource(
                        context,
                        hierarchy,
                        GeneratedRequestsNamespace,
                        dtoName,
                        reqDef,
                        null,
                        false,
                        reqDef.RequestFields
                    );
                    if (source != null)
                    {
                        context.AddSource(
                            $"{baseName}.Request.g.cs",
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
                            reqDef.RequestType,
                            $"Request DTO for {reqDef.RequestType}",
                            ex.ToString()
                        )
                    );
                }
            }
        }
    }

    /// <summary>
    /// Generates source code for all response DTOs. Assumes nested types are pre-generated.
    /// </summary>
    public static void GenerateResponseDtos(
        SourceProductionContext context,
        ProtocolDefinition protocol
    )
    {
        if (protocol.Requests is null)
        {
            return;
        }

        foreach (RequestDefinition reqDef in protocol.Requests)
        {
            if (reqDef.ResponseFields?.Count > 0)
            {
                string baseName = SanitizeIdentifier(reqDef.RequestType);
                string dtoName = baseName + "ResponseData";
                try
                {
                    ProtocolObjectNode? hierarchy = BuildObjectHierarchy(
                        reqDef.ResponseFields,
                        dtoName,
                        context
                    );
                    if (hierarchy == null)
                    {
                        continue;
                    }
                    // Pass the original list
                    string? source = GenerateRecordSource(
                        context,
                        hierarchy,
                        GeneratedResponsesNamespace,
                        dtoName,
                        reqDef,
                        null,
                        false,
                        reqDef.ResponseFields
                    );
                    if (source != null)
                    {
                        context.AddSource(
                            $"{baseName}.Response.g.cs",
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
                            reqDef.RequestType,
                            $"Response DTO for {reqDef.RequestType}",
                            ex.ToString()
                        )
                    );
                }
            }
        }
    }

    /// <summary>
    /// Generates source code for all event payload DTOs. Assumes nested types are pre-generated.
    /// </summary>
    public static void GenerateEventPayloads(
        SourceProductionContext context,
        ProtocolDefinition protocol
    )
    {
        if (protocol.Events is null)
        {
            return;
        }

        foreach (OBSEvent eventDef in protocol.Events)
        {
            if (eventDef.DataFields?.Count > 0)
            {
                string baseName = SanitizeIdentifier(eventDef.EventType);
                string dtoName = baseName + "Payload";
                try
                {
                    ProtocolObjectNode? hierarchy = BuildObjectHierarchy(
                        eventDef.DataFields,
                        dtoName,
                        context
                    );
                    if (hierarchy == null)
                    {
                        continue;
                    }
                    // Pass the original list
                    string? source = GenerateRecordSource(
                        context,
                        hierarchy,
                        GeneratedEventsNamespace,
                        dtoName,
                        null,
                        eventDef,
                        false,
                        eventDef.DataFields
                    );
                    if (source != null)
                    {
                        context.AddSource(
                            $"{baseName}.EventPayload.g.cs",
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
                            $"Event Payload DTO for {eventDef.EventType}",
                            ex.ToString()
                        )
                    );
                }
            }
        }
    }

    /// <summary>
    /// Generates the C# source code string for a DTO record.
    /// </summary>
    /// <param name="originalFields">The original flat list of fields from protocol.json for this DTO (used for constructor order). Null for nested types.</param>
    private static string? GenerateRecordSource(
        SourceProductionContext context,
        ProtocolObjectNode node,
        string targetNamespace, // Used only if isNestedType is false
        string recordName,
        RequestDefinition? reqDef,
        OBSEvent? eventDef,
        bool isNestedType,
        List<FieldDefinition>? originalFields
    )
    {
        string recordTypeContext =
            (reqDef != null) ? (node.Name.EndsWith("RequestData") ? "request" : "response")
            : (eventDef != null) ? "payload"
            : "nested";

        string actualNamespace = isNestedType ? NestedTypesNamespace : targetNamespace;

        StringBuilder mainBuilder = BuildSourceHeader();
        // --- Add necessary using directives ---
        mainBuilder.AppendLine("using System;");
        mainBuilder.AppendLine("using System.Collections.Generic;");
        mainBuilder.AppendLine("using System.Text.Json;");
        mainBuilder.AppendLine("using System.Text.Json.Serialization;");
        mainBuilder.AppendLine("using System.Diagnostics.CodeAnalysis;");
        mainBuilder.AppendLine($"using {GeneratedCommonNamespace};");
        if (!isNestedType)
        {
            mainBuilder.AppendLine($"using {NestedTypesNamespace};");
        }
        mainBuilder.AppendLine();
        mainBuilder.AppendLine($"namespace {actualNamespace};");
        mainBuilder.AppendLine();

        // --- Append Record Documentation ---
        string summaryText = recordTypeContext switch
        {
            "request" => $"Data required for the {reqDef!.RequestType} request.",
            "response" => $"Data returned by the {reqDef!.RequestType} request.",
            "payload" => $"Data payload for the {eventDef!.EventType} event.",
            "nested" => $"Represents the nested '{node.Name}' structure.",
            _ => $"Data structure for {recordName}.",
        };
        AppendXmlDocSummary(mainBuilder, summaryText, 0);
        mainBuilder.AppendLine("/// <remarks>");
        if (reqDef != null) // Add request-specific docs
        {
            AppendMultiLineXmlDoc(mainBuilder, reqDef.Description, "/// ");
            mainBuilder.AppendLine(
                $"/// <para>OBS WebSocket Protocol Category: {reqDef.Category} | Complexity: {reqDef.Complexity}/5</para>"
            );
            mainBuilder.AppendLine(
                $"/// <para>RPC Version: {reqDef.RpcVersion} | Initial OBS WebSocket Version: {reqDef.InitialVersion}</para>"
            );
            if (reqDef.Deprecated)
            {
                mainBuilder.AppendLine("/// <para>⚠️ This request is deprecated!</para>");
            }
        }
        else if (eventDef != null) // Add event-specific docs
        {
            AppendMultiLineXmlDoc(mainBuilder, eventDef.Description, "/// ");
            mainBuilder.AppendLine(
                $"/// <para>Requires Subscription: {eventDef.EventSubscription} | Complexity: {eventDef.Complexity}</para>"
            );
            mainBuilder.AppendLine(
                $"/// <para>RPC Version: {eventDef.RpcVersion} | Initial Version: {eventDef.InitialVersion}</para>"
            );
            if (eventDef.Deprecated)
            {
                mainBuilder.AppendLine("/// <para>⚠️ This event is deprecated!</para>");
            }
        }
        else if (isNestedType) // Add nested-specific docs
        {
            string parentNameGuess = recordName.Contains('_')
                ? recordName.Substring(0, recordName.LastIndexOf('_'))
                : "an unknown structure";
            mainBuilder.AppendLine(
                $"/// Represents the nested '{node.Name}' structure used within {parentNameGuess}."
            );
        }
        else // Fallback docs
        {
            mainBuilder.AppendLine($"/// Part of the data structure for {recordName}.");
        }
        mainBuilder.AppendLine("/// Generated from obs-websocket protocol definition.</remarks>");
        if (reqDef?.Deprecated ?? eventDef?.Deprecated ?? false)
        {
            mainBuilder.AppendLine($"[Obsolete(\"Associated request/event is deprecated.\")]");
        }

        // Suppress CS8618
        mainBuilder.AppendLine("#pragma warning disable CS8618");

        // --- Record Definition Start ---
        mainBuilder.AppendLine($"public sealed partial record {recordName}");
        mainBuilder.AppendLine("{");
        List<PropertyGenInfo> propertyInfos = [];
        Dictionary<string, PropertyGenInfo> propertyInfoMap = []; // Map original name to info

        // --- Generate Properties (Keep stable order based on original name for properties themselves) ---
        foreach (KeyValuePair<string, ProtocolNode> kvp in node.Children.OrderBy(c => c.Key))
        {
            string originalName = kvp.Key;
            ProtocolNode childNode = kvp.Value;
            string propertyName = ToPascalCase(childNode.SanitizedName);

            string? csharpType = null;
            bool isValueType = false;
            bool isConsideredRequired = false;
            string propertyNullableSuffix = "?";
            FieldDefinition? associatedFieldDef = null;

            if (childNode is ProtocolFieldNode fieldNode)
            {
                associatedFieldDef = fieldNode.FieldDefinition;
                (csharpType, isValueType) = MapProtocolTypeToCSharp(
                    context,
                    associatedFieldDef,
                    recordName
                );
                if (csharpType == null)
                {
                    continue;
                }

                bool isExplicitlyOptional = associatedFieldDef.ValueOptional.GetValueOrDefault(
                    false
                );
                bool typeIsInherentlyNullable = !isValueType || csharpType.EndsWith("?");

                if (recordTypeContext == "request")
                {
                    isConsideredRequired = !isExplicitlyOptional;
                }
                else // Response, Event, Nested
                {
                    isConsideredRequired = !typeIsInherentlyNullable && !csharpType.EndsWith("?");
                    if (csharpType.StartsWith("List<") || csharpType.StartsWith("Dictionary<"))
                    {
                        isConsideredRequired = false;
                    }
                }
                propertyNullableSuffix =
                    (!isConsideredRequired && !csharpType.EndsWith("?")) ? "?" : "";
            }
            else if (childNode is ProtocolObjectNode objectNode)
            {
                csharpType = $"{recordName}_{ToPascalCase(objectNode.SanitizedName)}";
                isValueType = false;
                isConsideredRequired = false;
                propertyNullableSuffix = "?";
                associatedFieldDef = FindRepresentativeFieldDef(objectNode, reqDef, eventDef);
            }

            if (csharpType == null)
            {
                continue;
            }

            PropertyGenInfo propInfo = new(
                propertyName,
                ToCamelCase(propertyName),
                csharpType,
                propertyNullableSuffix,
                isConsideredRequired,
                originalName,
                associatedFieldDef
            );
            propertyInfos.Add(propInfo);
            propertyInfoMap.Add(originalName, propInfo); // Add to map for constructor lookup

            // --- Generate Property Definition ---
            if (associatedFieldDef != null)
            {
                AppendXmlDocSummary(mainBuilder, associatedFieldDef.ValueDescription, 1);
                if (recordTypeContext == "request")
                {
                    AppendXmlDocRemarks(mainBuilder, associatedFieldDef, 1);
                }
            }
            else if (childNode is ProtocolObjectNode)
            {
                AppendXmlDocSummary(
                    mainBuilder,
                    $"Nested '{childNode.Name}' structure (see <see cref=\"{NestedTypesNamespace}.{csharpType}\"/>).",
                    1
                );
            }
            else
            {
                AppendXmlDocSummary(mainBuilder, $"Property {propertyName}", 1);
            }

            mainBuilder.AppendLine($"    [JsonPropertyName(\"{originalName}\")]");
            mainBuilder.Append("    public ");
            if (isConsideredRequired)
            {
                mainBuilder.Append("required ");
            }

            mainBuilder.AppendLine(
                $"{csharpType}{propertyNullableSuffix} {propertyName} {{ get; init; }}"
            );
            mainBuilder.AppendLine();
        }

        // --- Generate Constructors ---
        // 1. Parameterless constructor
        mainBuilder.AppendLine(
            "    /// <summary>Initializes a new instance for deserialization via <see cref=\"JsonConstructorAttribute\"/>.</summary>"
        );
        mainBuilder.AppendLine($"    [JsonConstructor]");
        mainBuilder.AppendLine($"    public {recordName}() {{ }}");
        mainBuilder.AppendLine();

        // 2. Convenience constructor accepting ALL properties
        if (propertyInfos.Count > 0)
        {
            mainBuilder.AppendLine("    /// <summary>");
            mainBuilder.AppendLine(
                "    /// Initializes a new instance with all properties specified."
            );
            mainBuilder.AppendLine(
                "    /// <para>Parameters are ordered with required properties first, then optional properties (with defaults). Follows protocol definition order where possible.</para>"
            );
            mainBuilder.AppendLine("    /// </summary>");
            // Keep SetsRequiredMembers if *any* property is required
            if (propertyInfos.Any(p => p.IsRequired))
            {
                mainBuilder.AppendLine("    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]");
            }
            mainBuilder.Append($"    public {recordName}(");

            List<string> constructorParams = [];
            List<PropertyGenInfo> constructorPropOrder = []; // To track order for assignment block
            List<PropertyGenInfo> requiredParamsList = []; // To store required params in order
            List<PropertyGenInfo> optionalParamsList = []; // To store optional params in order

            if (!isNestedType && originalFields != null)
            {
                // --- Top-Level DTO: Order based on originalFields ---
                foreach (FieldDefinition field in originalFields)
                {
                    PropertyGenInfo? propInfo = FindPropertyInfoForField(
                        field,
                        propertyInfoMap,
                        out bool isRootOfNested
                    );
                    if (
                        propInfo != null
                        && !constructorPropOrder.Any(p => p.PropertyName == propInfo.PropertyName)
                    )
                    {
                        constructorPropOrder.Add(propInfo); // Add to assignment order list
                        if (propInfo.IsRequired)
                        {
                            requiredParamsList.Add(propInfo);
                        }
                        else
                        {
                            optionalParamsList.Add(propInfo);
                        }
                    }
                }
            }
            else
            {
                // --- Nested DTO: Order alphabetically within required/optional ---
                requiredParamsList.AddRange(propertyInfos.Where(p => p.IsRequired));
                optionalParamsList.AddRange(propertyInfos.Where(p => !p.IsRequired));
                constructorPropOrder.AddRange(requiredParamsList);
                constructorPropOrder.AddRange(optionalParamsList);
            }

            // Append required parameters (no default value)
            constructorParams.AddRange(
                requiredParamsList.Select(p => $"{p.CSharpType}{p.NullableSuffix} {p.ParamName}")
            );
            // Append optional parameters WITH default null value
            constructorParams.AddRange(
                optionalParamsList.Select(p =>
                    $"{p.CSharpType}{p.NullableSuffix} {p.ParamName} = null"
                )
            );

            mainBuilder.Append(string.Join(", ", constructorParams));
            mainBuilder.AppendLine(")");
            mainBuilder.AppendLine("    {");
            // Assign properties using the determined constructor order
            foreach (PropertyGenInfo propInfo in constructorPropOrder)
            {
                mainBuilder.AppendLine(
                    $"        this.{propInfo.PropertyName} = {propInfo.ParamName};"
                );
            }
            mainBuilder.AppendLine("    }");
            mainBuilder.AppendLine();
        }

        mainBuilder.AppendLine("}"); // Close main record body
        mainBuilder.AppendLine("#pragma warning restore CS8618");
        mainBuilder.AppendLine();

        return mainBuilder.ToString();
    }

    private static PropertyGenInfo? FindPropertyInfoForField(
        FieldDefinition field,
        Dictionary<string, PropertyGenInfo> map,
        out bool isRootOfNested
    )
    {
        isRootOfNested = false;
        // Check if the field name itself exists as a key (non-nested field)
        if (map.TryGetValue(field.ValueName, out PropertyGenInfo? propInfo))
        {
            return propInfo;
        }

        // Check if the *first part* of a dotted field name exists (represents the nested object property)
        if (field.ValueName.Contains('.'))
        {
            string firstPart = field.ValueName.Split('.')[0];
            if (map.TryGetValue(firstPart, out PropertyGenInfo? nestedPropInfo))
            {
                // This field definition corresponds to the *root* of a nested structure
                isRootOfNested = true;
                return nestedPropInfo;
            }
        }

        return null; // Field doesn't correspond directly to a top-level property or the root of a nested structure
    }

    // Helper to find a representative FieldDefinition for documentation purposes
    private static FieldDefinition? FindRepresentativeFieldDef(
        ProtocolObjectNode objectNode,
        RequestDefinition? reqDef,
        OBSEvent? eventDef
    )
    {
        List<FieldDefinition> potentialFields = [];
        if (reqDef?.RequestFields != null)
        {
            potentialFields.AddRange(reqDef.RequestFields);
        }

        if (reqDef?.ResponseFields != null)
        {
            potentialFields.AddRange(reqDef.ResponseFields);
        }

        if (eventDef?.DataFields != null)
        {
            potentialFields.AddRange(eventDef.DataFields);
        }

        return potentialFields.FirstOrDefault(f =>
            f.ValueName.Split('.').LastOrDefault() == objectNode.Name
            || f.ValueName.EndsWith("." + objectNode.Name)
        );
    }
}
