using System.Text.Json.Serialization;

namespace ObsWebSocket.Core.Protocol.Common.FilterSettings;

/// <summary>Settings for the 'mask_filter' or 'mask_filter_v2' filter (Image Mask/Blend).</summary>
/// <remarks>v1 uses opacity 0–100; v2 uses 0.0–1.0.</remarks>
public sealed record ImageMaskBlendFilterSettings(
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("color")] long? Color = null,
    [property: JsonPropertyName("opacity")] double? Opacity = null,
    [property: JsonPropertyName("image_path")] string? ImagePath = null,
    [property: JsonPropertyName("stretch_image")] bool? StretchImage = null,
    [property: JsonPropertyName("subtractive")] bool? Subtractive = null
);

/// <summary>Settings for the 'crop_filter' (Crop/Pad) filter.</summary>
public sealed record CropPadFilterSettings(
    [property: JsonPropertyName("relative")] bool? Relative = null,
    [property: JsonPropertyName("top")] double? Top = null,
    [property: JsonPropertyName("bottom")] double? Bottom = null,
    [property: JsonPropertyName("left")] double? Left = null,
    [property: JsonPropertyName("right")] double? Right = null,
    [property: JsonPropertyName("width")] double? Width = null,
    [property: JsonPropertyName("height")] double? Height = null
);

/// <summary>Settings for the 'gain_filter' filter.</summary>
public sealed record GainFilterSettings(
    [property: JsonPropertyName("db")] double? Db = null
);

/// <summary>Settings for the 'basic_eq_filter' (3-Band EQ) filter.</summary>
public sealed record BasicEqFilterSettings(
    [property: JsonPropertyName("high")] double? High = null,
    [property: JsonPropertyName("low")] double? Low = null,
    [property: JsonPropertyName("mid")] double? Mid = null
);

/// <summary>Settings for the 'hdr_tonemap_filter' filter.</summary>
public sealed record HdrTonemapFilterSettings(
    [property: JsonPropertyName("hdr_input_maximum_nits")] double? HdrInputMaximumNits = null,
    [property: JsonPropertyName("hdr_output_maximum_nits")] double? HdrOutputMaximumNits = null,
    [property: JsonPropertyName("sdr_input_maximum_nits")] double? SdrInputMaximumNits = null,
    [property: JsonPropertyName("sdr_output_maximum_nits")] double? SdrOutputMaximumNits = null,
    [property: JsonPropertyName("sdr_white_level_nits")] double? SdrWhiteLevelNits = null,
    [property: JsonPropertyName("transform")] long? Transform = null
);

/// <summary>
/// Settings for the 'color_filter' (Color Correction v1) or 'color_filter_v2' filter.
/// v1 includes <see cref="Color"/> and uses opacity on a 0–100 scale.
/// v2 replaces <see cref="Color"/> with <see cref="ColorAdd"/> / <see cref="ColorMultiply"/> and uses opacity 0.0–1.0.
/// Null properties are skipped on overlay writes, so this type works for both versions.
/// </summary>
public sealed record ColorCorrectionFilterSettings(
    [property: JsonPropertyName("brightness")] double? Brightness = null,
    [property: JsonPropertyName("contrast")] double? Contrast = null,
    [property: JsonPropertyName("gamma")] double? Gamma = null,
    [property: JsonPropertyName("hue_shift")] double? HueShift = null,
    [property: JsonPropertyName("saturation")] double? Saturation = null,
    [property: JsonPropertyName("opacity")] double? Opacity = null,
    // v1 only
    [property: JsonPropertyName("color")] long? Color = null,
    // v2 only
    [property: JsonPropertyName("color_add")] long? ColorAdd = null,
    [property: JsonPropertyName("color_multiply")] long? ColorMultiply = null
);

/// <summary>Settings for the 'scale_filter' (Scaling/Aspect Ratio) filter.</summary>
public sealed record ScaleFilterSettings(
    [property: JsonPropertyName("resolution")] string? Resolution = null,
    [property: JsonPropertyName("sampling")] string? Sampling = null,
    [property: JsonPropertyName("undistort")] bool? Undistort = null
);

/// <summary>Settings for the 'scroll_filter' filter.</summary>
public sealed record ScrollFilterSettings(
    [property: JsonPropertyName("speed_x")] double? SpeedX = null,
    [property: JsonPropertyName("speed_y")] double? SpeedY = null,
    [property: JsonPropertyName("loop")] bool? Loop = null,
    [property: JsonPropertyName("limit_size")] bool? LimitSize = null,
    [property: JsonPropertyName("cx")] double? Width = null,
    [property: JsonPropertyName("cy")] double? Height = null
);

/// <summary>Settings for the 'gpu_delay' filter. Has no configurable properties.</summary>
public sealed record GpuDelayFilterSettings;

/// <summary>
/// Settings for the 'color_key_filter' or 'color_key_filter_v2' filter.
/// v1 uses opacity 0–100; v2 uses 0.0–1.0.
/// </summary>
public sealed record ColorKeyFilterSettings(
    [property: JsonPropertyName("key_color_type")] string? KeyColorType = null,
    [property: JsonPropertyName("key_color")] long? KeyColor = null,
    [property: JsonPropertyName("similarity")] double? Similarity = null,
    [property: JsonPropertyName("smoothness")] double? Smoothness = null,
    [property: JsonPropertyName("brightness")] double? Brightness = null,
    [property: JsonPropertyName("contrast")] double? Contrast = null,
    [property: JsonPropertyName("gamma")] double? Gamma = null,
    [property: JsonPropertyName("opacity")] double? Opacity = null
);

/// <summary>Settings for the 'clut_filter' (Apply LUT) filter.</summary>
public sealed record LutFilterSettings(
    [property: JsonPropertyName("image_path")] string? ImagePath = null,
    [property: JsonPropertyName("clut_amount")] double? ClutAmount = null,
    [property: JsonPropertyName("passthrough_alpha")] bool? PassthroughAlpha = null
);

/// <summary>Settings for the 'sharpness_filter' or 'sharpness_filter_v2' filter. Both versions share the same schema.</summary>
public sealed record SharpnessFilterSettings(
    [property: JsonPropertyName("sharpness")] double? Sharpness = null
);

/// <summary>
/// Settings for the 'chroma_key_filter' or 'chroma_key_filter_v2' filter.
/// v1 uses opacity 0–100; v2 uses 0.0–1.0.
/// </summary>
public sealed record ChromaKeyFilterSettings(
    [property: JsonPropertyName("key_color_type")] string? KeyColorType = null,
    [property: JsonPropertyName("key_color")] long? KeyColor = null,
    [property: JsonPropertyName("similarity")] double? Similarity = null,
    [property: JsonPropertyName("smoothness")] double? Smoothness = null,
    [property: JsonPropertyName("spill")] double? Spill = null,
    [property: JsonPropertyName("brightness")] double? Brightness = null,
    [property: JsonPropertyName("contrast")] double? Contrast = null,
    [property: JsonPropertyName("gamma")] double? Gamma = null,
    [property: JsonPropertyName("opacity")] double? Opacity = null
);

/// <summary>Settings for the 'async_delay_filter' filter. Has no configurable properties.</summary>
public sealed record AsyncDelayFilterSettings;

/// <summary>Settings for the 'noise_suppress_filter' or 'noise_suppress_filter_v2' filter. Both share the same schema; the default method differs.</summary>
public sealed record NoiseSuppressionFilterSettings(
    [property: JsonPropertyName("method")] string? Method = null,
    [property: JsonPropertyName("suppress_level")] double? SuppressLevel = null
);

/// <summary>Settings for the 'invert_polarity_filter' filter. Has no configurable properties.</summary>
public sealed record InvertPolarityFilterSettings;

/// <summary>Settings for the 'noise_gate_filter' filter.</summary>
public sealed record NoiseGateFilterSettings(
    [property: JsonPropertyName("close_threshold")] double? CloseThreshold = null,
    [property: JsonPropertyName("open_threshold")] double? OpenThreshold = null,
    [property: JsonPropertyName("attack_time")] double? AttackTime = null,
    [property: JsonPropertyName("hold_time")] double? HoldTime = null,
    [property: JsonPropertyName("release_time")] double? ReleaseTime = null
);

/// <summary>Settings for the 'compressor_filter' filter.</summary>
public sealed record CompressorFilterSettings(
    [property: JsonPropertyName("threshold")] double? Threshold = null,
    [property: JsonPropertyName("ratio")] double? Ratio = null,
    [property: JsonPropertyName("attack_time")] double? AttackTime = null,
    [property: JsonPropertyName("release_time")] double? ReleaseTime = null,
    [property: JsonPropertyName("output_gain")] double? OutputGain = null,
    [property: JsonPropertyName("sidechain_source")] string? SidechainSource = null
);

/// <summary>Settings for the 'limiter_filter' filter.</summary>
public sealed record LimiterFilterSettings(
    [property: JsonPropertyName("threshold")] double? Threshold = null,
    [property: JsonPropertyName("release_time")] double? ReleaseTime = null
);

/// <summary>Settings for the 'expander_filter' filter.</summary>
public sealed record ExpanderFilterSettings(
    [property: JsonPropertyName("presets")] string? Presets = null,
    [property: JsonPropertyName("threshold")] double? Threshold = null,
    [property: JsonPropertyName("ratio")] double? Ratio = null,
    [property: JsonPropertyName("attack_time")] double? AttackTime = null,
    [property: JsonPropertyName("release_time")] double? ReleaseTime = null,
    [property: JsonPropertyName("output_gain")] double? OutputGain = null,
    [property: JsonPropertyName("detector")] string? Detector = null
)
{
    /// <summary>Known values for the <see cref="Detector"/> setting.</summary>
    public static class DetectorValues
    {
        /// <summary>Root Mean Square level detection.</summary>
        public const string Rms = "RMS";
        /// <summary>Peak level detection.</summary>
        public const string Peak = "Peak";
    }

    /// <summary>Known values for the <see cref="Presets"/> setting.</summary>
    public static class PresetsValues
    {
        /// <summary>Expander — increases dynamic range below the threshold.</summary>
        public const string Expander = "expander";
        /// <summary>Gate — silences audio below the threshold.</summary>
        public const string Gate = "gate";
    }
}

/// <summary>Settings for the 'upward_compressor_filter' filter.</summary>
public sealed record UpwardCompressorFilterSettings(
    [property: JsonPropertyName("threshold")] double? Threshold = null,
    [property: JsonPropertyName("ratio")] double? Ratio = null,
    [property: JsonPropertyName("knee_width")] double? KneeWidth = null,
    [property: JsonPropertyName("attack_time")] double? AttackTime = null,
    [property: JsonPropertyName("release_time")] double? ReleaseTime = null,
    [property: JsonPropertyName("output_gain")] double? OutputGain = null,
    [property: JsonPropertyName("detector")] string? Detector = null
)
{
    /// <summary>Known values for the <see cref="Detector"/> setting.</summary>
    public static class DetectorValues
    {
        /// <summary>Root Mean Square level detection.</summary>
        public const string Rms = "RMS";
        /// <summary>Peak level detection.</summary>
        public const string Peak = "Peak";
    }
}

/// <summary>Settings for the 'luma_key_filter' or 'luma_key_filter_v2' filter. Both versions share the same schema.</summary>
public sealed record LumaKeyFilterSettings(
    [property: JsonPropertyName("luma_min")] double? LumaMin = null,
    [property: JsonPropertyName("luma_max")] double? LumaMax = null,
    [property: JsonPropertyName("luma_min_smooth")] double? LumaMinSmooth = null,
    [property: JsonPropertyName("luma_max_smooth")] double? LumaMaxSmooth = null
);

/// <summary>Settings for the 'vst_filter' filter. Properties are plugin-specific; use a consumer-defined type for a concrete VST.</summary>
public sealed record VstFilterSettings;

/// <summary>Settings for the 'win_spout_filter' (Spout Filter) filter.</summary>
public sealed record SpoutFilterSettings(
    [property: JsonPropertyName("spout_filter_name")] string? SpoutFilterName = null
);
