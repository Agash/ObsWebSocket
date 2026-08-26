namespace ObsWebSocket.Codegen.Tasks.Generation;

/// <summary>
/// One thing a request addresses, and the protocol fields that address it.
/// </summary>
/// <param name="Kind">The entity kind, such as <c>scene</c> or <c>input</c>.</param>
/// <param name="Role">
/// Which reference this is within the request. Usually the same as the kind, but
/// <c>DuplicateSceneItem</c> names a second scene <c>destinationScene</c>.
/// </param>
/// <param name="Fields">The protocol fields this reference consumes.</param>
internal sealed record EntityReference(string Kind, string Role, IReadOnlyList<string> Fields);

/// <summary>
/// Finds the things a request addresses, from the shape of its fields rather than a list of
/// request names.
/// </summary>
/// <remarks>
/// The protocol never says "this request takes a scene". It says the request has an optional
/// <c>sceneName</c> and an optional <c>sceneUuid</c>, which is the same statement made twice per
/// request across 68 of them. Reading that shape back out is what lets the handle overloads be
/// generated instead of transcribed, and it is why <c>DuplicateSceneItem</c>'s second scene falls
/// out without anyone thinking about it.
/// </remarks>
internal static class EntityReferenceTable
{
    /// <summary>
    /// The kinds that are addressed by a name-or-uuid pair, mapped to the handle type that carries
    /// them. A kind not listed here is a field that happens to end in Name, not an entity.
    /// </summary>
    private static readonly Dictionary<string, string> s_handleTypes = new(StringComparer.Ordinal)
    {
        ["scene"] = "SceneHandle",
        ["destinationScene"] = "SceneHandle",
        ["input"] = "InputHandle",
        ["source"] = "SourceHandle",
    };

    /// <summary>Returns the handle type for a kind, or <see langword="null"/> if it is not one.</summary>
    public static string? HandleTypeFor(string kind) =>
        s_handleTypes.TryGetValue(kind, out string? type) ? type : null;

    /// <summary>
    /// Reads the entity references out of a request's fields.
    /// </summary>
    /// <remarks>
    /// A <c>{X}Name</c> and <c>{X}Uuid</c> pair, both optional, is a reference to X. Both optional
    /// matters: <c>CreateInput</c> takes a required <c>inputName</c> for the input it is about to
    /// make, which is a value, not a reference to something that already exists.
    /// <para>
    /// <c>canvasUuid</c> is folded into the scene or source reference it scopes, because OBS reads
    /// it only when resolving a name and ignores it beside a uuid. A request with a canvas and no
    /// name-addressable entity keeps it as an ordinary parameter.
    /// </para>
    /// <para>
    /// A <c>sceneItemId</c> beside a scene reference, and a <c>filterName</c> beside a source
    /// reference, are composite references: both fields together address one thing.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<EntityReference> Find(IReadOnlyList<FieldDefinition>? fields)
    {
        if (fields is null || fields.Count == 0)
        {
            return [];
        }

        Dictionary<string, FieldDefinition> byName = new(StringComparer.Ordinal);
        foreach (FieldDefinition f in fields)
        {
            byName[f.ValueName] = f;
        }

        List<EntityReference> found = [];
        foreach (FieldDefinition field in fields)
        {
            string name = field.ValueName;
            if (!name.EndsWith("Name", StringComparison.Ordinal) || field.ValueOptional != true)
            {
                continue;
            }

            string role = name.Substring(0, name.Length - "Name".Length);
            string uuid = role + "Uuid";
            if (
                !byName.TryGetValue(uuid, out FieldDefinition? uuidField)
                || uuidField.ValueOptional != true
            )
            {
                continue;
            }

            string kind = role;
            if (HandleTypeFor(kind) is null)
            {
                continue;
            }

            found.Add(new EntityReference(kind, role, [name, uuid]));
        }

        if (found.Count == 0)
        {
            return found;
        }

        // The canvas scopes the first name-addressed reference in the request. Only one reference
        // can own it, and in every request that has both it is the primary scene or source.
        if (byName.ContainsKey("canvasUuid"))
        {
            EntityReference primary = found[0];
            found[0] = primary with { Fields = [.. primary.Fields, "canvasUuid"] };
        }

        // Composite references: the id or the filter name plus the entity it hangs off.
        EntityReference? scene = found.Find(r => r.Kind == "scene");
        if (scene is not null && byName.ContainsKey("sceneItemId"))
        {
            found[found.IndexOf(scene)] = new EntityReference(
                "sceneItem",
                "sceneItem",
                [.. scene.Fields, "sceneItemId"]
            );
        }

        EntityReference? source = found.Find(r => r.Kind == "source");
        if (source is not null && byName.ContainsKey("filterName"))
        {
            found[found.IndexOf(source)] = new EntityReference(
                "filter",
                "filter",
                [.. source.Fields, "filterName"]
            );
        }

        return found;
    }

    /// <summary>The handle type that carries a reference, including the composite kinds.</summary>
    public static string HandleTypeForReference(EntityReference reference) =>
        reference.Kind switch
        {
            "sceneItem" => "SceneItemHandle",
            "filter" => "FilterHandle",
            _ => HandleTypeFor(reference.Kind)!,
        };

    /// <summary>The parameter name a reference is given in a generated overload.</summary>
    public static string ParameterNameForReference(EntityReference reference) =>
        reference.Role switch
        {
            "destinationScene" => "destinationScene",
            "sceneItem" => "sceneItem",
            "filter" => "filter",
            _ => reference.Kind,
        };

    /// <summary>
    /// The arguments a reference contributes to the generated request record, keyed by protocol
    /// field name.
    /// </summary>
    /// <remarks>
    /// A handle holds either a name or a uuid, never both, so writing both fields sends exactly
    /// one of them and the other stays null. That is the shape OBS resolves, without the caller
    /// having to know the order it resolves in.
    /// </remarks>
    public static IEnumerable<KeyValuePair<string, string>> ArgumentsFor(
        EntityReference reference,
        string parameterName
    )
    {
        string root = reference.Kind switch
        {
            "sceneItem" => $"{parameterName}.Scene",
            "filter" => $"{parameterName}.Source",
            _ => parameterName,
        };

        foreach (string field in reference.Fields)
        {
            yield return field switch
            {
                "canvasUuid" => new("canvasUuid", $"{root}.Canvas.Uuid"),
                "sceneItemId" => new("sceneItemId", $"{parameterName}.SceneItemId"),
                "filterName" => new("filterName", $"{parameterName}.FilterName"),
                _ when field.EndsWith("Uuid", StringComparison.Ordinal) => new(
                    field,
                    $"{root}.Uuid"
                ),
                _ => new(field, $"{root}.Name"),
            };
        }
    }
}
