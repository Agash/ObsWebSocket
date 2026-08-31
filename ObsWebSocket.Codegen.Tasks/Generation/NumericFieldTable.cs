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
/// <para>
/// The rule for choosing between <c>int</c> and <c>long</c> is not how large the number looks. It
/// is whether obs-websocket bounds it. A field validated with <c>ValidateNumber</c> or
/// <c>ValidateOptionalNumber</c> has a stated range and is safe at its natural width; a field
/// copied straight out of libobs or a settings blob is as wide as the C type behind it, and most of
/// those are <c>uint32_t</c> or <c>int64_t</c>. Getting this wrong is not a truncated field: the
/// response fails to deserialize, so one out-of-range pixel count takes the whole message with it.
/// Check the request handler before adding a field here.
/// </para>
/// </remarks>
internal static class NumericFieldTable
{
    /// <summary>Whole-number fields whose values fit comfortably in 32 bits.</summary>
    private static readonly HashSet<string> s_int32Fields = new(StringComparer.Ordinal)
    {
        // Identity and ordering.
        "sceneItemIndex",
        "filterIndex",
        "monitorIndex",
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
        "sleepFrames",
        "sleepMillis",
        // Protocol version.
        "rpcVersion",
    };

    /// <summary>
    /// Whole-number fields that can exceed 32 bits: byte counts, millisecond durations over a long
    /// session, and the input capability bitflag, which OBS defines as an unsigned 32 bit mask.
    /// </summary>
    /// <remarks>
    /// The frame and message counters are here because of what fills them, not because the numbers
    /// look large. A field is safe as an <c>int</c> only when obs-websocket bounds it. The
    /// resolutions are all validated to 8..4096, and the indices are container positions. These
    /// are copied out of libobs and the session with no clamp in between:
    /// <c>obs_get_total_frames</c> and <c>obs_get_lagged_frames</c> return <c>uint32_t</c>,
    /// <c>video_output_get_skipped_frames</c> returns <c>uint32_t</c>, and the session counters are
    /// <c>uint64_t</c>. A monotonic frame counter passes <see cref="int.MaxValue"/> after roughly
    /// 414 days at 60fps, which a 24/7 instance reaches, and the whole response fails to
    /// deserialize when it does.
    /// </remarks>
    private static readonly HashSet<string> s_int64Fields = new(StringComparer.Ordinal)
    {
        "outputBytes",
        "outputDuration",
        "mediaCursor",
        "mediaCursorOffset",
        "mediaDuration",
        "inputKindCaps",
        // Counters, unclamped from uint32_t (frames) and uint64_t (session messages).
        "renderSkippedFrames",
        "renderTotalFrames",
        "outputSkippedFrames",
        "outputTotalFrames",
        "webSocketSessionIncomingMessages",
        "webSocketSessionOutgoingMessages",
        // A scene item id is int64_t at every point OBS touches it, and the only bound
        // obs-websocket applies is >= 0. Sequential assignment keeps real ids small, but the
        // counter they come from is restored out of the scene collection file, and a plugin can
        // choose an id outright, so neither the type nor the protocol makes 32 bits safe.
        "sceneItemId",
        // Read back out of a scene's private settings with obs_data_get_int, which is int64_t and
        // is not revalidated on the way out. Only the write side is bounded to 50..20000.
        "transitionDuration",
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
        // The T-bar, 0.0 to 1.0. As an int only the two ends were reachable.
        "position",
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
