using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ObsWebSocket.Core.Protocol.Common;

if (args.Length != 2)
{
    Console.Error.WriteLine(
        "usage: ObsWebSocket.StubAudit <obs-websocket-clone> <obs-studio-clone>"
    );
    return 2;
}

string obsWebSocket = args[0];
string obsStudio = args[1];

string[] sources =
[
    "src/utils/Obs_ArrayHelper.cpp",
    "src/utils/Obs_ObjectHelper.cpp",
    "src/utils/Obs_VolumeMeter.cpp",
    "src/requesthandler/RequestHandler_Canvases.cpp",
    "src/requesthandler/RequestHandler_Ui.cpp",
];

Dictionary<string, string> emitted = new(StringComparer.Ordinal);
Regex assignment = new(
    """\w+\s*\[\s*"(?<field>[A-Za-z0-9_]+)"\s*\]\s*=\s*(?<expr>[^;]+);""",
    RegexOptions.Compiled
);

foreach (string relative in sources)
{
    string path = Path.Combine(obsWebSocket, relative);
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"missing: {path}");
        return 2;
    }

    foreach (Match match in assignment.Matches(File.ReadAllText(path)))
    {
        string field = match.Groups["field"].Value;
        if (field.StartsWith("OBS_", StringComparison.Ordinal))
        {
            continue;
        }

        emitted[field] = match.Groups["expr"].Value.Trim();
    }
}

// Stub fields are the subject. Response fields are here only so a response field emitted by the
// same helper is not reported as a missing stub field; they are generated from protocol.json.
Dictionary<string, (Type Owner, Type Declared)> declared = new(StringComparer.Ordinal);
Dictionary<string, (Type Owner, Type Declared)> stubFields = new(StringComparer.Ordinal);

foreach (Type type in typeof(SceneStub).Assembly.GetTypes().Where(t => t.IsPublic))
{
    bool isStub = type.Name.EndsWith("Stub", StringComparison.Ordinal);
    bool isResponse = type.Namespace?.EndsWith(".Responses", StringComparison.Ordinal) == true;
    if (!isStub && !isResponse)
    {
        continue;
    }

    foreach (PropertyInfo property in type.GetProperties())
    {
        if (property.GetCustomAttribute<JsonExtensionDataAttribute>() is not null)
        {
            continue;
        }

        string name =
            property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
        declared[name] = (type, property.PropertyType);
        if (isStub)
        {
            stubFields[name] = (type, property.PropertyType);
        }
    }
}

// Return types of the libobs accessors, so a field's width can be compared against the C type.
Dictionary<string, string> returns = new(StringComparer.Ordinal);
Regex declaration = new(
    @"EXPORT\s+(?<type>[A-Za-z_][A-Za-z0-9_ \*]*?)\s+(?<name>obs_[A-Za-z0-9_]+)\s*\(",
    RegexOptions.Compiled
);
foreach (
    string header in Directory.EnumerateFiles(
        Path.Combine(obsStudio, "libobs"),
        "*.h",
        SearchOption.AllDirectories
    )
)
{
    foreach (Match match in declaration.Matches(File.ReadAllText(header)))
    {
        returns.TryAdd(match.Groups["name"].Value, match.Groups["type"].Value.Trim());
    }
}

Dictionary<string, int> widths = new(StringComparer.Ordinal)
{
    ["int64_t"] = 64,
    ["uint64_t"] = 64,
    ["long long"] = 64,
    ["uint32_t"] = 32,
    ["int32_t"] = 32,
    ["int"] = 32,
    ["size_t"] = 64,
};

List<string> missing = [];
List<string> extra = [];
List<string> narrow = [];

foreach ((string field, string expr) in emitted.OrderBy(pair => pair.Key, StringComparer.Ordinal))
{
    if (!declared.TryGetValue(field, out (Type Owner, Type Declared) property))
    {
        missing.Add($"{field}  <- {expr}");
        continue;
    }

    // An explicit cast or arithmetic means the C type is not what reaches the wire.
    if (expr.Contains("(double)", StringComparison.Ordinal) || expr.Contains('/'))
    {
        continue;
    }

    Match call = Regex.Match(expr, @"\b(obs_[A-Za-z0-9_]+)\s*\(");
    if (!call.Success || !returns.TryGetValue(call.Groups[1].Value, out string? cType))
    {
        continue;
    }

    if (!widths.TryGetValue(cType, out int cWidth))
    {
        continue;
    }

    Type target = Nullable.GetUnderlyingType(property.Declared) ?? property.Declared;
    int managedWidth = Type.GetTypeCode(target) switch
    {
        TypeCode.Int32 or TypeCode.UInt32 => 32,
        TypeCode.Int64 or TypeCode.UInt64 => 64,
        TypeCode.Double => 64,
        _ => 0,
    };

    bool unsignedC = cType.StartsWith('u') || cType == "size_t";
    if (managedWidth != 0 && (managedWidth < cWidth || (unsignedC && managedWidth == cWidth)))
    {
        narrow.Add(
            $"{property.Owner.Name}.{field}: {target.Name} holds {cType} from {call.Groups[1].Value}"
        );
    }
}

foreach (
    (string field, (Type owner, _)) in stubFields.OrderBy(pair => pair.Key, StringComparer.Ordinal)
)
{
    if (!emitted.ContainsKey(field))
    {
        extra.Add($"{owner.Name}.{field}");
    }
}

Report("Emitted by OBS, absent from the stubs", missing);
Report("Declared on a stub, never emitted", extra);
Report("Narrower than the C type behind them", narrow);

Console.WriteLine(
    $"{emitted.Count} emitted field(s), {stubFields.Count} stub field(s), "
        + $"{missing.Count} missing, {extra.Count} extra, {narrow.Count} narrow."
);

return missing.Count + narrow.Count == 0 ? 0 : 1;

static void Report(string title, List<string> entries)
{
    Console.WriteLine($"## {title}: {entries.Count}");
    foreach (string entry in entries)
    {
        Console.WriteLine($"  {entry}");
    }

    Console.WriteLine();
}
