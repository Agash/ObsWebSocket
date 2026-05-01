using System.Text.Json.Serialization;

namespace ObsWebSocket.Core.Protocol.Common.StreamServiceSettings;

/// <summary>
/// Settings for the <c>rtmp_common</c> stream service type (YouTube, Twitch, etc. via the built-in service list).
/// Pass <c>"rtmp_common"</c> as the <c>streamServiceType</c> argument to <c>SetStreamServiceSettingsAsync</c>.
/// </summary>
public sealed record RtmpCommonStreamServiceSettings(
    [property: JsonPropertyName("service")] string? Service = null,
    [property: JsonPropertyName("server")] string? Server = null,
    [property: JsonPropertyName("key")] string? Key = null,
    [property: JsonPropertyName("protocol")] string? Protocol = null,
    [property: JsonPropertyName("bwtest")] bool? BandwidthTest = null,
    [property: JsonPropertyName("stream_key_link")] string? StreamKeyLink = null,
    [property: JsonPropertyName("multitrack_video_name")] string? MultitrackVideoName = null,
    [property: JsonPropertyName("multitrack_video_disclaimer")] string? MultitrackVideoDisclaimer = null
);

/// <summary>
/// Settings for the <c>rtmp_custom</c> stream service type (custom RTMP/RTMPS server).
/// Pass <c>"rtmp_custom"</c> as the <c>streamServiceType</c> argument to <c>SetStreamServiceSettingsAsync</c>.
/// </summary>
public sealed record RtmpCustomStreamServiceSettings(
    [property: JsonPropertyName("server")] string? Server = null,
    [property: JsonPropertyName("key")] string? Key = null,
    [property: JsonPropertyName("use_auth")] bool? UseAuth = null,
    [property: JsonPropertyName("username")] string? Username = null,
    [property: JsonPropertyName("password")] string? Password = null
);
