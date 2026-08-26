// ObsWebSocket.Codegen.Tasks/Generation/StringEnumFieldTable.cs
namespace ObsWebSocket.Codegen.Tasks.Generation;

/// <summary>
/// Maps protocol fields that carry one of the string-valued protocol enums onto that enum, so the
/// generated property is the enum rather than a string the caller has to convert.
/// </summary>
/// <remarks>
/// The definition types these fields as plain <c>String</c> and never says which enum they draw
/// from, so the association is written out here. Only fields backed by an enum the protocol
/// actually declares are listed: <c>mediaState</c>, <c>monitorType</c>, <c>sceneItemBlendMode</c>
/// and <c>inputKind</c> also carry fixed vocabularies, but the protocol declares no enum for them,
/// so they stay strings rather than being given one this library would have to maintain by hand.
/// </remarks>
internal static class StringEnumFieldTable
{
    private static readonly Dictionary<string, string> s_fieldToEnum = new(StringComparer.Ordinal)
    {
        // StreamStateChanged, RecordStateChanged, ReplayBufferStateChanged, VirtualcamStateChanged
        ["outputState"] = "OutputState",
        // TriggerMediaInputAction, and the event it raises
        ["mediaAction"] = "MediaInputAction",
    };

    /// <summary>The enum type names this table maps fields onto.</summary>
    public static IEnumerable<string> MappedEnums => s_fieldToEnum.Values;

    /// <summary>
    /// Returns the enum type name a <c>String</c> field maps to, or <see langword="null"/> when it
    /// is an ordinary string.
    /// </summary>
    /// <param name="fieldName">The protocol field name, matched case sensitively.</param>
    public static string? MapStringEnum(string fieldName) =>
        s_fieldToEnum.TryGetValue(fieldName, out string? enumName) ? enumName : null;
}
