using System.Text.Json.Serialization;
using ObsWebSocket.Core.Protocol.Common.FilterSettings;
using ObsWebSocket.Core.Protocol.Common.InputSettings;
using ObsWebSocket.Core.Protocol.Common.StreamServiceSettings;

namespace ObsWebSocket.Core.Serialization;

/// <summary>
/// Supplemental JSON serialization context for library-defined input, filter, and stream service settings types.
/// Combined with <see cref="ObsWebSocketJsonContext"/> via <c>JsonTypeInfoResolver.Combine</c>.
/// </summary>
// Shared sub-types
[JsonSerializable(typeof(ObsFontSettings))]
// Input settings
[JsonSerializable(typeof(ImageSourceSettings))]
[JsonSerializable(typeof(ColorSourceSettings))]
[JsonSerializable(typeof(SlideshowSettings))]
[JsonSerializable(typeof(BrowserSourceSettings))]
[JsonSerializable(typeof(MediaSourceSettings))]
[JsonSerializable(typeof(TextGdiPlusInputSettings))]
[JsonSerializable(typeof(TextFreetype2InputSettings))]
[JsonSerializable(typeof(VlcSourceSettings))]
[JsonSerializable(typeof(MonitorCaptureSettings))]
[JsonSerializable(typeof(WindowCaptureSettings))]
[JsonSerializable(typeof(GameCaptureSettings))]
[JsonSerializable(typeof(WasapiInputCaptureSettings))]
[JsonSerializable(typeof(WasapiOutputCaptureSettings))]
[JsonSerializable(typeof(DShowInputSettings))]
// Filter settings
[JsonSerializable(typeof(ImageMaskBlendFilterSettings))]
[JsonSerializable(typeof(CropPadFilterSettings))]
[JsonSerializable(typeof(GainFilterSettings))]
[JsonSerializable(typeof(BasicEqFilterSettings))]
[JsonSerializable(typeof(HdrTonemapFilterSettings))]
[JsonSerializable(typeof(ColorCorrectionFilterSettings))]
[JsonSerializable(typeof(ScaleFilterSettings))]
[JsonSerializable(typeof(ScrollFilterSettings))]
[JsonSerializable(typeof(GpuDelayFilterSettings))]
[JsonSerializable(typeof(ColorKeyFilterSettings))]
[JsonSerializable(typeof(LutFilterSettings))]
[JsonSerializable(typeof(SharpnessFilterSettings))]
[JsonSerializable(typeof(ChromaKeyFilterSettings))]
[JsonSerializable(typeof(AsyncDelayFilterSettings))]
[JsonSerializable(typeof(NoiseSuppressionFilterSettings))]
[JsonSerializable(typeof(InvertPolarityFilterSettings))]
[JsonSerializable(typeof(NoiseGateFilterSettings))]
[JsonSerializable(typeof(CompressorFilterSettings))]
[JsonSerializable(typeof(LimiterFilterSettings))]
[JsonSerializable(typeof(ExpanderFilterSettings))]
[JsonSerializable(typeof(UpwardCompressorFilterSettings))]
[JsonSerializable(typeof(LumaKeyFilterSettings))]
[JsonSerializable(typeof(VstFilterSettings))]
[JsonSerializable(typeof(SpoutFilterSettings))]
// Stream service settings
[JsonSerializable(typeof(RtmpCommonStreamServiceSettings))]
[JsonSerializable(typeof(RtmpCustomStreamServiceSettings))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
internal sealed partial class ObsWebSocketSettingsJsonContext : JsonSerializerContext { }
