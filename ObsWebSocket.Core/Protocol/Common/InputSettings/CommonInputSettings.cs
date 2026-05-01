using System.Text.Json.Serialization;

namespace ObsWebSocket.Core.Protocol.Common.InputSettings;

/// <summary>OBS font descriptor used by text input sources.</summary>
public sealed record ObsFontSettings(
    [property: JsonPropertyName("face")] string? Face = null,
    [property: JsonPropertyName("size")] int? Size = null,
    [property: JsonPropertyName("flags")] int? Flags = null,
    [property: JsonPropertyName("style")] string? Style = null
);

// ---------------------------------------------------------------------------
// Built-in input sources
// ---------------------------------------------------------------------------

/// <summary>Settings for the 'image_source' input.</summary>
public sealed record ImageSourceSettings(
    [property: JsonPropertyName("file")] string? File = null,
    [property: JsonPropertyName("linear_alpha")] bool? LinearAlpha = null,
    [property: JsonPropertyName("unload")] bool? Unload = null
);

/// <summary>Settings for the 'color_source_v3' (Color Source) input.</summary>
public sealed record ColorSourceSettings(
    [property: JsonPropertyName("color")] long? Color = null,
    [property: JsonPropertyName("width")] int? Width = null,
    [property: JsonPropertyName("height")] int? Height = null
);

/// <summary>Settings for the 'slideshow_v2' (Image Slideshow) input.</summary>
public sealed record SlideshowSettings(
    [property: JsonPropertyName("playback_behavior")] string? PlaybackBehavior = null,
    [property: JsonPropertyName("playback_mode")] string? PlaybackMode = null,
    [property: JsonPropertyName("slide_mode")] string? SlideMode = null,
    [property: JsonPropertyName("slide_time")] int? SlideTime = null,
    [property: JsonPropertyName("transition")] string? Transition = null,
    [property: JsonPropertyName("transition_speed")] int? TransitionSpeed = null,
    [property: JsonPropertyName("use_custom_size")] string? UseCustomSize = null,
    [property: JsonPropertyName("loop")] bool? Loop = null,
    [property: JsonPropertyName("hide")] bool? Hide = null,
    [property: JsonPropertyName("randomize")] bool? Randomize = null
)
{
    /// <summary>Known values for <see cref="PlaybackBehavior"/>.</summary>
    public static class PlaybackBehaviorValues
    {
        /// <summary>Always play regardless of scene visibility.</summary>
        public const string AlwaysPlay = "always_play";
        /// <summary>Pause when invisible, unpause when visible.</summary>
        public const string PauseUnpause = "pause_unpause";
        /// <summary>Stop and restart from the beginning when made visible.</summary>
        public const string StopRestart = "stop_restart";
    }

    /// <summary>Known values for <see cref="PlaybackMode"/>.</summary>
    public static class PlaybackModeValues
    {
        /// <summary>Loop through slides continuously.</summary>
        public const string Loop = "loop";
        /// <summary>Bounce back and forth through slides.</summary>
        public const string Bounce = "bounce";
        /// <summary>Manual advance only.</summary>
        public const string Manual = "manual";
    }
}

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
    [property: JsonPropertyName("restart_when_active")] bool? RestartWhenActive = null,
    [property: JsonPropertyName("shutdown")] bool? Shutdown = null
);

/// <summary>Settings for the 'ffmpeg_source' (Media Source) input.</summary>
public sealed record MediaSourceSettings(
    [property: JsonPropertyName("local_file")] string? LocalFile = null,
    [property: JsonPropertyName("is_local_file")] bool? IsLocalFile = null,
    [property: JsonPropertyName("looping")] bool? Looping = null,
    [property: JsonPropertyName("restart_on_activate")] bool? RestartOnActivate = null,
    [property: JsonPropertyName("clear_on_media_end")] bool? ClearOnMediaEnd = null,
    [property: JsonPropertyName("close_when_inactive")] bool? CloseWhenInactive = null,
    [property: JsonPropertyName("speed_percent")] int? SpeedPercent = null,
    [property: JsonPropertyName("buffering_mb")] int? BufferingMb = null,
    [property: JsonPropertyName("reconnect_delay_sec")] int? ReconnectDelaySec = null,
    [property: JsonPropertyName("linear_alpha")] bool? LinearAlpha = null,
    [property: JsonPropertyName("log_changes")] bool? LogChanges = null,
    [property: JsonPropertyName("hw_decode")] bool? HwDecode = null
);

/// <summary>Settings for the 'text_gdiplus_v2' / 'text_gdiplus_v3' (Text GDI+) input.</summary>
public sealed record TextGdiPlusInputSettings(
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("align")] string? Align = null,
    [property: JsonPropertyName("valign")] string? Valign = null,
    [property: JsonPropertyName("color")] long? Color = null,
    [property: JsonPropertyName("opacity")] int? Opacity = null,
    [property: JsonPropertyName("bk_color")] long? BackgroundColor = null,
    [property: JsonPropertyName("bk_opacity")] int? BackgroundOpacity = null,
    [property: JsonPropertyName("font")] ObsFontSettings? Font = null,
    [property: JsonPropertyName("antialiasing")] bool? Antialiasing = null,
    [property: JsonPropertyName("outline")] bool? Outline = null,
    [property: JsonPropertyName("outline_color")] long? OutlineColor = null,
    [property: JsonPropertyName("outline_opacity")] int? OutlineOpacity = null,
    [property: JsonPropertyName("outline_size")] int? OutlineSize = null,
    [property: JsonPropertyName("gradient_color")] long? GradientColor = null,
    [property: JsonPropertyName("gradient_dir")] double? GradientDir = null,
    [property: JsonPropertyName("gradient_opacity")] int? GradientOpacity = null,
    [property: JsonPropertyName("extents_cx")] int? ExtentsCx = null,
    [property: JsonPropertyName("extents_cy")] int? ExtentsCy = null,
    [property: JsonPropertyName("extents_wrap")] bool? ExtentsWrap = null,
    [property: JsonPropertyName("chatlog_lines")] int? ChatlogLines = null,
    [property: JsonPropertyName("transform")] int? Transform = null,
    [property: JsonPropertyName("word_wrap")] bool? WordWrap = null,
    [property: JsonPropertyName("from_file")] bool? FromFile = null,
    [property: JsonPropertyName("log_mode")] bool? LogMode = null,
    [property: JsonPropertyName("text_file")] string? TextFile = null
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
    [property: JsonPropertyName("color1")] long? Color1 = null,
    [property: JsonPropertyName("color2")] long? Color2 = null,
    [property: JsonPropertyName("font")] ObsFontSettings? Font = null,
    [property: JsonPropertyName("antialiasing")] bool? Antialiasing = null,
    [property: JsonPropertyName("word_wrap")] bool? WordWrap = null,
    [property: JsonPropertyName("outline")] bool? Outline = null,
    [property: JsonPropertyName("drop_shadow")] bool? DropShadow = null,
    [property: JsonPropertyName("log_lines")] int? LogLines = null,
    [property: JsonPropertyName("log_mode")] bool? LogMode = null,
    [property: JsonPropertyName("from_file")] bool? FromFile = null,
    [property: JsonPropertyName("text_file")] string? TextFile = null
);

/// <summary>Settings for the 'vlc_source' (VLC Media Source) input.</summary>
public sealed record VlcSourceSettings(
    [property: JsonPropertyName("loop")] bool? Loop = null,
    [property: JsonPropertyName("shuffle")] bool? Shuffle = null,
    [property: JsonPropertyName("playback_behavior")] string? PlaybackBehavior = null,
    [property: JsonPropertyName("network_caching")] int? NetworkCaching = null,
    [property: JsonPropertyName("track")] int? Track = null,
    [property: JsonPropertyName("subtitle")] int? Subtitle = null,
    [property: JsonPropertyName("subtitle_enable")] bool? SubtitleEnable = null
)
{
    /// <summary>Known values for <see cref="PlaybackBehavior"/>.</summary>
    public static class PlaybackBehaviorValues
    {
        /// <summary>Stop and restart from the beginning when made visible.</summary>
        public const string StopRestart = "stop_restart";
        /// <summary>Pause when invisible, unpause when visible.</summary>
        public const string PauseUnpause = "pause_unpause";
        /// <summary>Always play regardless of scene visibility.</summary>
        public const string AlwaysPlay = "always_play";
    }
}

// ---------------------------------------------------------------------------
// Capture sources
// ---------------------------------------------------------------------------

/// <summary>Settings for the 'monitor_capture' (Display Capture) input.</summary>
/// <remarks><see cref="MonitorId"/> and <see cref="MonitorWgc"/> are system-specific identifiers.</remarks>
public sealed record MonitorCaptureSettings(
    [property: JsonPropertyName("capture_cursor")] bool? CaptureCursor = null,
    [property: JsonPropertyName("force_sdr")] bool? ForceSdr = null,
    [property: JsonPropertyName("method")] int? Method = null,
    [property: JsonPropertyName("monitor_id")] string? MonitorId = null,
    [property: JsonPropertyName("monitor_wgc")] int? MonitorWgc = null
);

/// <summary>Settings for the 'window_capture' (Window Capture) input.</summary>
public sealed record WindowCaptureSettings(
    [property: JsonPropertyName("window")] string? Window = null,
    [property: JsonPropertyName("method")] int? Method = null,
    [property: JsonPropertyName("cursor")] bool? Cursor = null,
    [property: JsonPropertyName("client_area")] bool? ClientArea = null,
    [property: JsonPropertyName("compatibility")] bool? Compatibility = null,
    [property: JsonPropertyName("force_sdr")] bool? ForceSdr = null
);

/// <summary>Settings for the 'game_capture' (Game Capture) input.</summary>
public sealed record GameCaptureSettings(
    [property: JsonPropertyName("capture_mode")] string? CaptureMode = null,
    [property: JsonPropertyName("window")] string? Window = null,
    [property: JsonPropertyName("capture_cursor")] bool? CaptureCursor = null,
    [property: JsonPropertyName("allow_transparency")] bool? AllowTransparency = null,
    [property: JsonPropertyName("limit_framerate")] bool? LimitFramerate = null,
    [property: JsonPropertyName("capture_overlays")] bool? CaptureOverlays = null,
    [property: JsonPropertyName("anti_cheat_hook")] bool? AntiCheatHook = null,
    [property: JsonPropertyName("hook_rate")] int? HookRate = null,
    [property: JsonPropertyName("priority")] int? Priority = null,
    [property: JsonPropertyName("premultiplied_alpha")] bool? PremultipliedAlpha = null,
    [property: JsonPropertyName("rgb10a2_space")] string? Rgb10a2Space = null,
    [property: JsonPropertyName("sli_compatibility")] bool? SliCompatibility = null
)
{
    /// <summary>Known values for <see cref="CaptureMode"/>.</summary>
    public static class CaptureModeValues
    {
        /// <summary>Capture any fullscreen application.</summary>
        public const string AnyFullscreen = "any_fullscreen";
        /// <summary>Capture a specific window.</summary>
        public const string Window = "window";
        /// <summary>Toggle capture with a hotkey.</summary>
        public const string Hotkey = "hotkey";
    }
}

// ---------------------------------------------------------------------------
// Audio capture sources
// ---------------------------------------------------------------------------

/// <summary>Settings for the 'wasapi_input_capture' (Audio Input Capture) input.</summary>
/// <remarks><see cref="DeviceId"/> is system-specific; use <c>"default"</c> for the default device.</remarks>
public sealed record WasapiInputCaptureSettings(
    [property: JsonPropertyName("device_id")] string? DeviceId = null,
    [property: JsonPropertyName("use_device_timing")] bool? UseDeviceTiming = null
);

/// <summary>Settings for the 'wasapi_output_capture' (Audio Output Capture) input.</summary>
/// <remarks><see cref="DeviceId"/> is system-specific; use <c>"default"</c> for the default device.</remarks>
public sealed record WasapiOutputCaptureSettings(
    [property: JsonPropertyName("device_id")] string? DeviceId = null,
    [property: JsonPropertyName("use_device_timing")] bool? UseDeviceTiming = null
);

/// <summary>Settings for the 'dshow_input' (Video Capture Device / DirectShow) input.</summary>
/// <remarks>Device-identifying properties (device, device_id, video_device_id, audio_device_id) are not included — use a consumer-defined type to target a specific device.</remarks>
public sealed record DShowInputSettings(
    [property: JsonPropertyName("active")] bool? Active = null,
    [property: JsonPropertyName("hw_decode")] bool? HwDecode = null,
    [property: JsonPropertyName("autorotation")] bool? Autorotation = null,
    [property: JsonPropertyName("color_space")] string? ColorSpace = null,
    [property: JsonPropertyName("color_range")] string? ColorRange = null,
    [property: JsonPropertyName("res_type")] int? ResType = null,
    [property: JsonPropertyName("frame_interval")] long? FrameInterval = null,
    [property: JsonPropertyName("video_format")] int? VideoFormat = null,
    [property: JsonPropertyName("audio_output_mode")] int? AudioOutputMode = null
);
