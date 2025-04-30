using Microsoft.CodeAnalysis;

namespace ObsWebSocket.SourceGenerators;

/// <summary>
/// Contains logic for building the object hierarchy from dot-notated field definitions.
/// Uses a two-pass approach to prioritize structure from dot notation.
/// </summary>
internal static partial class Emitter
{
    /// <summary>
    /// Builds a hierarchical representation using a two-pass approach.
    /// Pass 1: Creates the necessary ProtocolObjectNode structure based on dot notation.
    /// Pass 2: Adds ProtocolFieldNodes, ignoring explicit parent 'Object' definitions if children exist.
    /// </summary>
    /// <param name="fields">The flat list of FieldDefinition objects.</param>
    /// <param name="rootObjectName">The name of the root object (e.g., the DTO name).</param>
    /// <param name="context">The source production context for reporting diagnostics.</param>
    /// <returns>The root ProtocolObjectNode representing the hierarchy, or null if critical errors occur.</returns>
    private static ProtocolObjectNode? BuildObjectHierarchy(
        List<FieldDefinition> fields,
        string rootObjectName,
        SourceProductionContext context
    )
    {
        ProtocolObjectNode rootNode = new(rootObjectName, SanitizeIdentifier(rootObjectName));
        bool criticalErrorOccurred = false;

        // --- Pass 1: Build the Object Structure ---
        foreach (FieldDefinition field in fields)
        {
            string[] parts = field.ValueName.Split('.');
            if (parts.Length <= 1)
            {
                continue; // Only fields with dots define structure in this pass
            }

            ProtocolObjectNode currentNode = rootNode;
            string currentPathForDiagnostics = rootObjectName;

            // Iterate through parent segments to ensure object nodes exist
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string segment = parts[i];
                string sanitizedSegment = SanitizeIdentifier(segment);
                currentPathForDiagnostics += $".{segment}";

                if (string.IsNullOrEmpty(sanitizedSegment))
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            Diagnostics.IdentifierGenerationError,
                            Location.None,
                            field.ValueName,
                            currentPathForDiagnostics,
                            $"Empty segment '{segment}' after sanitization in dot notation."
                        )
                    );
                    criticalErrorOccurred = true;
                    goto NextFieldPass1;
                }

                if (currentNode.Children.TryGetValue(segment, out ProtocolNode? existingNode))
                {
                    if (existingNode is ProtocolObjectNode objNode)
                    {
                        currentNode = objNode; // Path exists, continue
                    }
                    else // Conflict: A FieldNode exists where an ObjectNode is needed
                    {
                        context.ReportDiagnostic(
                            Diagnostic.Create(
                                Diagnostics.IdentifierGenerationError,
                                Location.None,
                                field.ValueName,
                                currentPathForDiagnostics,
                                $"Field '{existingNode.Name}' conflicts with object path segment '{segment}'. Cannot create nested structure."
                            )
                        );
                        criticalErrorOccurred = true;
                        goto NextFieldPass1;
                    }
                }
                else // Create new ObjectNode for this segment
                {
                    ProtocolObjectNode newNode = new(segment, sanitizedSegment);
                    currentNode.Children.Add(segment, newNode);
                    currentNode = newNode;
                }
            }
            NextFieldPass1:
            ;
        }

        if (criticalErrorOccurred)
        {
            return null; // Stop if structural errors found
        }

        // --- Pass 2: Add Field Nodes ---
        foreach (FieldDefinition field in fields)
        {
            string[] parts = field.ValueName.Split('.');
            ProtocolObjectNode parentNode = rootNode; // Node where the final field should reside
            string currentPathForDiagnostics = rootObjectName;
            bool pathFailed = false;

            // Navigate to the correct parent node for the field
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string segment = parts[i];
                currentPathForDiagnostics += $".{segment}";
                // Structure should exist from Pass 1
                if (
                    parentNode.Children.TryGetValue(segment, out ProtocolNode? pathNode)
                    && pathNode is ProtocolObjectNode objNode
                )
                {
                    parentNode = objNode;
                }
                else
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            Diagnostics.IdentifierGenerationError,
                            Location.None,
                            field.ValueName,
                            currentPathForDiagnostics,
                            $"Internal Error: Expected object node for segment '{segment}' not found during Pass 2."
                        )
                    );
                    pathFailed = true;
                    break; // Cannot place field
                }
            }

            if (pathFailed)
            {
                continue; // Skip field
            }

            // Process the final field segment
            string fieldNameSegment = parts[parts.Length - 1];
            string sanitizedFieldName = SanitizeIdentifier(fieldNameSegment);
            currentPathForDiagnostics += $".{fieldNameSegment}";

            if (string.IsNullOrEmpty(sanitizedFieldName))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Diagnostics.IdentifierGenerationError,
                        Location.None,
                        field.ValueName,
                        currentPathForDiagnostics,
                        "Empty field name segment after sanitization."
                    )
                );
                continue;
            }

            // Check for Conflicts or Ignorable Parent Definitions
            if (parentNode.Children.TryGetValue(fieldNameSegment, out ProtocolNode? existingNode))
            {
                // Scenario 1: An ObjectNode exists (from Pass 1's structure building).
                // If the current 'field' is the explicit parent 'Object'/'Any' definition, ignore it.
                if (
                    existingNode is ProtocolObjectNode
                    && parts.Length == 1
                    && field.ValueType is "Object" or "Any"
                )
                {
                    // Ignore explicit parent definition; structure comes from children.
                    continue;
                }
                // Scenario 2: Any other conflict (duplicate field, field vs object mismatch)
                else
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            Diagnostics.IdentifierGenerationError,
                            Location.None,
                            field.ValueName,
                            currentPathForDiagnostics,
                            $"An item named '{fieldNameSegment}' (Type: {existingNode.GetType().Name}) already exists at this level. Cannot add Field (Type: {field.ValueType})."
                        )
                    );
                    continue; // Skip conflicting field
                }
            }
            else // No conflict, add the field node
            {
                parentNode.Children.Add(
                    fieldNameSegment,
                    new ProtocolFieldNode(fieldNameSegment, sanitizedFieldName, field)
                );
            }
        } // End foreach Pass 2

        return rootNode;
    }
}
