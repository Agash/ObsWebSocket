// ObsWebSocket.Codegen.Tasks/Generation/NumericFieldTable.cs
namespace ObsWebSocket.Codegen.Tasks.Generation;

/// <summary>
/// Decides which of the protocol's <c>Number</c> fields are whole numbers.
/// </summary>
/// <remarks>
/// The protocol has one numeric type because JSON has one numeric type, so <c>sceneItemId</c> and
/// <c>inputVolumeMul</c> are indistinguishable in the definition: both are <c>Number</c> with a
/// <c>&gt;= 0</c> restriction. This table is written out by hand rather than inferred from the
/// field name, because a rule that guesses wrong on a volume field truncates it silently, while a
/// field missing from the table only stays <c>double</c>, which is what it would have been anyway.
/// A refresh that introduces an unlisted <c>Number</c> field reports OBSWSGEN012 so it gets
/// classified deliberately instead of drifting in.
/// </remarks>
internal static class NumericFieldTable
{
    /// <summary>Whole-number fields whose values fit comfortably in 32 bits.</summary>
    private static readonly HashSet<string> s_int32Fields = new(StringComparer.Ordinal)
    {
        // Identity and ordering.
        "sceneItemId",
        "sceneItemIndex",
        "filterIndex",
        "monitorIndex",
        "position",
        "searchOffset",
        // Resolutions, in pixels.
        "baseWidth",
        "baseHeight",
        "outputWidth",
        "outputHeight",
        "imageWidth",
        "imageHeight",
        "imageCompressionQuality",
        // Frame rate, expressed as a fraction.
        "fpsNumerator",
        "fpsDenominator",
        // Durations and offsets that OBS reports in whole milliseconds or frames.
        "inputAudioSyncOffset",
        "transitionDuration",
        "sleepFrames",
        "sleepMillis",
        // Counters.
        "renderSkippedFrames",
        "renderTotalFrames",
        "outputSkippedFrames",
        "outputTotalFrames",
        "webSocketSessionIncomingMessages",
        "webSocketSessionOutgoingMessages",
        // Protocol version.
        "rpcVersion",
    };

    /// <summary>
    /// Whole-number fields that can exceed 32 bits: byte counts, millisecond durations over a long
    /// session, and the input capability bitflag, which OBS defines as an unsigned 32 bit mask.
    /// </summary>
    private static readonly HashSet<string> s_int64Fields = new(StringComparer.Ordinal)
    {
        "outputBytes",
        "outputDuration",
        "mediaCursor",
        "mediaCursorOffset",
        "mediaDuration",
        "inputKindCaps",
    };

    /// <summary>
    /// Fields deliberately left fractional, listed so an unclassified field is distinguishable
    /// from one that was considered and left alone.
    /// </summary>
    private static readonly HashSet<string> s_doubleFields = new(StringComparer.Ordinal)
    {
        "inputVolumeMul",
        "inputVolumeDb",
        "inputAudioBalance",
        "transitionCursor",
        "outputCongestion",
        "cpuUsage",
        "memoryUsage",
        "availableDiskSpace",
        "activeFps",
        "averageFrameRenderTime",
    };

    /// <summary>
    /// Returns the C# type for a protocol <c>Number</c> field.
    /// </summary>
    /// <param name="fieldName">The protocol field name, matched case sensitively.</param>
    /// <param name="classified">
    /// Whether the field appears in the table at all. An unclassified field still maps to
    /// <c>double</c>; the flag lets the caller report it.
    /// </param>
    public static string MapNumber(string fieldName, out bool classified)
    {
        if (s_int32Fields.Contains(fieldName))
        {
            classified = true;
            return "int";
        }

        if (s_int64Fields.Contains(fieldName))
        {
            classified = true;
            return "long";
        }

        classified = s_doubleFields.Contains(fieldName);
        return "double";
    }
}
