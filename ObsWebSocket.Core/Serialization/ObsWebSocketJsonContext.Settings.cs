using System.Text.Json.Serialization;
using ObsWebSocket.Core.Protocol.Common.FilterSettings;
using ObsWebSocket.Core.Protocol.Common.InputSettings;

namespace ObsWebSocket.Core.Serialization;

/// <summary>
/// Supplemental JSON serialization context for library-defined input and filter settings stubs.
/// Combined with <see cref="ObsWebSocketJsonContext"/> via <c>JsonTypeInfoResolver.Combine</c>.
/// </summary>
// Input settings
[JsonSerializable(typeof(BrowserSourceSettings))]
[JsonSerializable(typeof(TextGdiPlusInputSettings))]
[JsonSerializable(typeof(TextFreetype2InputSettings))]
// Filter settings
[JsonSerializable(typeof(ImageMaskBlendFilterSettings))]
[JsonSerializable(typeof(CropPadFilterSettings))]
[JsonSerializable(typeof(GainFilterSettings))]
[JsonSerializable(typeof(BasicEqFilterSettings))]
[JsonSerializable(typeof(HdrTonemapFilterSettings))]
[JsonSerializable(typeof(ColorCorrectionFilterSettings))]
[JsonSerializable(typeof(ScaleFilterSettings))]
[JsonSerializable(typeof(ScrollFilterSettings))]
[JsonSerializable(typeof(ColorKeyFilterSettings))]
[JsonSerializable(typeof(LutFilterSettings))]
[JsonSerializable(typeof(SharpnessFilterSettings))]
[JsonSerializable(typeof(ChromaKeyFilterSettings))]
[JsonSerializable(typeof(NoiseSuppressionFilterSettings))]
[JsonSerializable(typeof(NoiseGateFilterSettings))]
[JsonSerializable(typeof(CompressorFilterSettings))]
[JsonSerializable(typeof(LimiterFilterSettings))]
[JsonSerializable(typeof(ExpanderFilterSettings))]
[JsonSerializable(typeof(UpwardCompressorFilterSettings))]
[JsonSerializable(typeof(LumaKeyFilterSettings))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
internal sealed partial class ObsWebSocketSettingsJsonContext : JsonSerializerContext { }
