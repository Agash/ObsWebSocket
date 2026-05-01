using System.Text.Json.Serialization;

namespace ObsWebSocket.Core.Protocol.Common.InputSettings;

/// <summary>Settings for the 'browser_source' input.</summary>
public sealed record BrowserSourceSettings(
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("width")] int? Width = null,
    [property: JsonPropertyName("height")] int? Height = null,
    [property: JsonPropertyName("css")] string? Css = null,
    [property: JsonPropertyName("fps")] int? Fps = null,
    [property: JsonPropertyName("fps_custom")] bool? FpsCustom = null,
    [property: JsonPropertyName("reroute_audio")] bool? RerouteAudio = null,
    [property: JsonPropertyName("webpage_control_level")] int? WebpageControlLevel = null,
    [property: JsonPropertyName("restart_when_active")] bool? RestartWhenActive = null
);

/// <summary>Settings for the 'text_gdiplus_v2' / 'text_gdiplus_v3' (Text GDI+) input.</summary>
public sealed record TextGdiPlusInputSettings(
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("align")] string? Align = null,
    [property: JsonPropertyName("valign")] string? Valign = null,
    [property: JsonPropertyName("color1")] int? Color1 = null,
    [property: JsonPropertyName("color2")] int? Color2 = null,
    [property: JsonPropertyName("word_wrap")] bool? WordWrap = null,
    [property: JsonPropertyName("outline")] bool? Outline = null,
    [property: JsonPropertyName("extents")] bool? Extents = null,
    [property: JsonPropertyName("extents_cx")] double? ExtentsCx = null,
    [property: JsonPropertyName("extents_cy")] double? ExtentsCy = null
)
{
    /// <summary>Known values for the <see cref="Align"/> setting.</summary>
    public static class AlignValues
    {
        /// <summary>Left-align text.</summary>
        public const string Left = "left";
        /// <summary>Center-align text.</summary>
        public const string Center = "center";
        /// <summary>Right-align text.</summary>
        public const string Right = "right";
    }

    /// <summary>Known values for the <see cref="Valign"/> setting.</summary>
    public static class ValignValues
    {
        /// <summary>Align text to the top.</summary>
        public const string Top = "top";
        /// <summary>Align text to the center.</summary>
        public const string Center = "center";
        /// <summary>Align text to the bottom.</summary>
        public const string Bottom = "bottom";
    }
}

/// <summary>Settings for the 'text_ft2_source_v2' (Text Freetype 2) input.</summary>
public sealed record TextFreetype2InputSettings(
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("color1")] int? Color1 = null,
    [property: JsonPropertyName("color2")] int? Color2 = null,
    [property: JsonPropertyName("word_wrap")] bool? WordWrap = null,
    [property: JsonPropertyName("outline")] bool? Outline = null,
    [property: JsonPropertyName("log_mode")] bool? LogMode = null,
    [property: JsonPropertyName("from_file")] bool? FromFile = null,
    [property: JsonPropertyName("text_file")] string? TextFile = null
);
