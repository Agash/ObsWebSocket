using System.Text.Json;
using System.Text.Json.Serialization;
using MessagePack;

namespace ObsWebSocket.Core.Protocol.Common;

/// <summary>
/// Represents a common structure for scene data, often used in lists. Resilient to missing fields.
/// </summary>
/// <remarks>Generated from heuristics based on obs-websocket protocol.</remarks>
[MessagePackObject]
public sealed class SceneStub
{
    /// <summary>Scene name.</summary>
    [JsonPropertyName("sceneName")]
    [Key("sceneName")]
    public required string SceneName { get; init; }

    /// <summary>Scene UUID.</summary>
    [JsonPropertyName("sceneUuid")]
    [Key("sceneUuid")]
    public required string SceneUuid { get; init; }

    /// <summary>Scene index position.</summary>
    /// <remarks>
    /// Nullable because OBS sends null, not because the protocol says so. The main scene list
    /// numbers its entries, but GetCanvasSceneList enumerates through a callback that has no index
    /// to report and writes null in its place. As a non-nullable int that failed the whole
    /// response rather than the one field.
    /// </remarks>
    [JsonPropertyName("sceneIndex")]
    [Key("sceneIndex")]
    public int? SceneIndex { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [IgnoreMember]
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public SceneStub() { }
}

/// <summary>
/// A scene item's identity and position, as carried by the <c>SceneItemListReindexed</c> event.
/// </summary>
/// <remarks>
/// Not a <see cref="SceneItemStub"/>: the reindex event asks OBS for the list in its basic form,
/// which carries only the id and the index. Reading it as a full scene item fails on the source
/// and transform fields it never sends.
/// </remarks>
[MessagePackObject]
public sealed class SceneItemOrderStub
{
    /// <summary>Numeric ID of the scene item.</summary>
    [JsonPropertyName("sceneItemId")]
    [Key("sceneItemId")]
    public required long SceneItemId { get; init; }

    /// <summary>Index of the scene item, counted from the bottom of the list.</summary>
    [JsonPropertyName("sceneItemIndex")]
    [Key("sceneItemIndex")]
    public required int SceneItemIndex { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [IgnoreMember]
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public SceneItemOrderStub() { }
}

/// <summary>
/// One input's audio levels, as carried by the <c>InputVolumeMeters</c> event.
/// </summary>
/// <remarks>
/// Not an <see cref="InputStub"/>: the meter payload carries only the name, the uuid and the
/// levels, so reading it as one fails on the input kind it never sends.
/// </remarks>
[MessagePackObject]
public sealed class InputVolumeMeterStub
{
    /// <summary>Input name.</summary>
    [JsonPropertyName("inputName")]
    [Key("inputName")]
    public required string InputName { get; init; }

    /// <summary>Input UUID.</summary>
    [JsonPropertyName("inputUuid")]
    [Key("inputUuid")]
    public required string InputUuid { get; init; }

    /// <summary>
    /// Per channel levels as multipliers, each entry being magnitude, peak and input peak.
    /// </summary>
    [JsonPropertyName("inputLevelsMul")]
    [Key("inputLevelsMul")]
    public required List<List<double>> InputLevelsMul { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [IgnoreMember]
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public InputVolumeMeterStub() { }
}

/// <summary>
/// A canvas, as returned by <c>GetCanvasList</c>. Resilient to missing fields.
/// </summary>
/// <remarks>
/// The protocol definition types the array as <c>Array&lt;Object&gt;</c> and says no more, so the
/// shape is taken from the request handler: name, uuid, flags and video settings.
/// </remarks>
[MessagePackObject]
public sealed class CanvasStub
{
    /// <summary>Canvas name. No request accepts it; it is for display and for looking up a uuid.</summary>
    [JsonPropertyName("canvasName")]
    [Key("canvasName")]
    public required string CanvasName { get; init; }

    /// <summary>Canvas UUID. This is what every canvas-scoped request takes.</summary>
    [JsonPropertyName("canvasUuid")]
    [Key("canvasUuid")]
    public required string CanvasUuid { get; init; }

    /// <summary>Canvas capability flags.</summary>
    [JsonPropertyName("canvasFlags")]
    [Key("canvasFlags")]
    public required CanvasFlagsStub CanvasFlags { get; init; }

    /// <summary>Video settings for this canvas.</summary>
    [JsonPropertyName("canvasVideoSettings")]
    [Key("canvasVideoSettings")]
    public required CanvasVideoSettingsStub CanvasVideoSettings { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [IgnoreMember]
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public CanvasStub() { }
}

/// <summary>
/// The capability flags reported for a canvas.
/// </summary>
[MessagePackObject]
public sealed class CanvasFlagsStub
{
    /// <summary>The main canvas, the one every request addresses when <c>canvasUuid</c> is omitted.</summary>
    [JsonPropertyName("MAIN")]
    [Key("MAIN")]
    public required bool Main { get; init; }

    /// <summary>Sources on this canvas are activated.</summary>
    [JsonPropertyName("ACTIVATE")]
    [Key("ACTIVATE")]
    public required bool Activate { get; init; }

    /// <summary>Audio from this canvas is mixed into the main output.</summary>
    [JsonPropertyName("MIX_AUDIO")]
    [Key("MIX_AUDIO")]
    public required bool MixAudio { get; init; }

    /// <summary>The canvas holds references to its scenes.</summary>
    [JsonPropertyName("SCENE_REF")]
    [Key("SCENE_REF")]
    public required bool SceneRef { get; init; }

    /// <summary>The canvas is not saved with the scene collection.</summary>
    [JsonPropertyName("EPHEMERAL")]
    [Key("EPHEMERAL")]
    public required bool Ephemeral { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [IgnoreMember]
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public CanvasFlagsStub() { }
}

/// <summary>
/// Video settings for a canvas.
/// </summary>
/// <remarks>
/// Every field is nullable because OBS sends the whole object as nulls when it cannot read the
/// canvas video info, rather than omitting it.
/// </remarks>
[MessagePackObject]
public sealed class CanvasVideoSettingsStub
{
    /// <summary>Numerator of the frame rate.</summary>
    [JsonPropertyName("fpsNumerator")]
    [Key("fpsNumerator")]
    public int? FpsNumerator { get; init; }

    /// <summary>Denominator of the frame rate.</summary>
    [JsonPropertyName("fpsDenominator")]
    [Key("fpsDenominator")]
    public int? FpsDenominator { get; init; }

    /// <summary>Base (canvas) width, in pixels.</summary>
    [JsonPropertyName("baseWidth")]
    [Key("baseWidth")]
    public int? BaseWidth { get; init; }

    /// <summary>Base (canvas) height, in pixels.</summary>
    [JsonPropertyName("baseHeight")]
    [Key("baseHeight")]
    public int? BaseHeight { get; init; }

    /// <summary>Output (scaled) width, in pixels.</summary>
    [JsonPropertyName("outputWidth")]
    [Key("outputWidth")]
    public int? OutputWidth { get; init; }

    /// <summary>Output (scaled) height, in pixels.</summary>
    [JsonPropertyName("outputHeight")]
    [Key("outputHeight")]
    public int? OutputHeight { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [IgnoreMember]
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public CanvasVideoSettingsStub() { }
}

/// <summary>
/// Represents a common structure for scene item transform data. Resilient to missing fields.
/// </summary>
/// <remarks>Generated from heuristics based on obs-websocket protocol.</remarks>
[MessagePackObject]
public sealed class SceneItemTransformStub
{
    /// <summary>
    /// Position X value.
    /// </summary>
    [JsonPropertyName("positionX")]
    [Key("positionX")]
    public required double PositionX { get; init; }

    /// <summary>
    /// Position X value.
    /// </summary>
    [JsonPropertyName("positionY")]
    [Key("positionY")]
    public required double PositionY { get; init; }

    /// <summary>
    /// Rotation value.
    /// </summary>
    [JsonPropertyName("rotation")]
    [Key("rotation")]
    public required double Rotation { get; init; }

    /// <summary>
    /// Scale X value.
    /// </summary>
    [JsonPropertyName("scaleX")]
    [Key("scaleX")]
    public required double ScaleX { get; init; }

    /// <summary>
    /// Scale Y value.
    /// </summary>
    [JsonPropertyName("scaleY")]
    [Key("scaleY")]
    public required double ScaleY { get; init; }

    /// <summary>
    /// Width value.
    /// </summary>
    [JsonPropertyName("width")]
    [Key("width")]
    public required double Width { get; init; }

    /// <summary>
    /// Height value.
    /// </summary>
    [JsonPropertyName("height")]
    [Key("height")]
    public required double Height { get; init; }

    /// <summary>
    /// Source width value.
    /// </summary>
    [JsonPropertyName("sourceWidth")]
    [Key("sourceWidth")]
    public required double SourceWidth { get; init; }

    /// <summary>
    /// Source height value.
    /// </summary>
    [JsonPropertyName("sourceHeight")]
    [Key("sourceHeight")]
    public required double SourceHeight { get; init; }

    /// <summary>
    /// Alignment of the scene item, as an <c>OBS_ALIGN_*</c> bit mask.
    /// </summary>
    /// <remarks>
    /// A mask, not a count, and OBS treats it as the full width of one: the field is
    /// <c>uint32_t</c> in <c>obs_transform_info</c>, and obs-websocket validates writes to
    /// <c>0 .. uint32_t max</c> rather than to the flags it defines. A value past
    /// <see cref="int.MaxValue"/> is accepted on the way in and handed back on the way out.
    /// </remarks>
    [JsonPropertyName("alignment")]
    [Key("alignment")]
    public required long Alignment { get; init; }

    /// <summary>
    /// Bounds type value.
    /// </summary>
    [JsonPropertyName("boundsType")]
    [Key("boundsType")]
    public required string BoundsType { get; init; }

    /// <summary>
    /// Alignment of the bounding box, as an <c>OBS_ALIGN_*</c> bit mask. See
    /// <see cref="Alignment"/> for why this is 64 bits wide.
    /// </summary>
    [JsonPropertyName("boundsAlignment")]
    [Key("boundsAlignment")]
    public required long BoundsAlignment { get; init; }

    /// <summary>
    /// Bounds width value.
    /// </summary>
    [JsonPropertyName("boundsWidth")]
    [Key("boundsWidth")]
    public required double BoundsWidth { get; init; }

    /// <summary>
    /// Bounds height value.
    /// </summary>
    [JsonPropertyName("boundsHeight")]
    [Key("boundsHeight")]
    public required double BoundsHeight { get; init; }

    /// <summary>
    /// Crop left value.
    /// </summary>
    [JsonPropertyName("cropLeft")]
    [Key("cropLeft")]
    public required int CropLeft { get; init; }

    /// <summary>
    /// Crop top value.
    /// </summary>
    [JsonPropertyName("cropTop")]
    [Key("cropTop")]
    public required int CropTop { get; init; }

    /// <summary>
    /// Crop right value.
    /// </summary>
    [JsonPropertyName("cropRight")]
    [Key("cropRight")]
    public required int CropRight { get; init; }

    /// <summary>
    /// Crop bottom value.
    /// </summary>
    [JsonPropertyName("cropBottom")]
    [Key("cropBottom")]
    public required int CropBottom { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [IgnoreMember]
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public SceneItemTransformStub() { }
}

/// <summary>
/// The fields of a scene item transform a caller wants to change.
/// </summary>
/// <remarks>
/// Separate from <see cref="SceneItemTransformStub"/> because <c>SetSceneItemTransform</c>
/// reads only the fields that are present and applies those; it is not a whole object write.
/// A transform read back from OBS is refused, because it carries the source dimensions OBS
/// computes for itself.
/// </remarks>
[MessagePackObject]
public sealed class SceneItemTransformPatchStub
{
    /// <summary>
    /// Position X value.
    /// </summary>
    [JsonPropertyName("positionX")]
    [Key("positionX")]
    public double? PositionX { get; init; }

    /// <summary>
    /// Position X value.
    /// </summary>
    [JsonPropertyName("positionY")]
    [Key("positionY")]
    public double? PositionY { get; init; }

    /// <summary>
    /// Rotation value.
    /// </summary>
    [JsonPropertyName("rotation")]
    [Key("rotation")]
    public double? Rotation { get; init; }

    /// <summary>
    /// Scale X value.
    /// </summary>
    [JsonPropertyName("scaleX")]
    [Key("scaleX")]
    public double? ScaleX { get; init; }

    /// <summary>
    /// Scale Y value.
    /// </summary>
    [JsonPropertyName("scaleY")]
    [Key("scaleY")]
    public double? ScaleY { get; init; }

    /// <summary>
    /// Width value.
    /// </summary>
    [JsonPropertyName("width")]
    [Key("width")]
    public double? Width { get; init; }

    /// <summary>
    /// Height value.
    /// </summary>
    [JsonPropertyName("height")]
    [Key("height")]
    public double? Height { get; init; }

    /// <summary>
    /// Source width value.
    /// </summary>
    [JsonPropertyName("sourceWidth")]
    [Key("sourceWidth")]
    public double? SourceWidth { get; init; }

    /// <summary>
    /// Source height value.
    /// </summary>
    [JsonPropertyName("sourceHeight")]
    [Key("sourceHeight")]
    public double? SourceHeight { get; init; }

    /// <summary>
    /// Alignment of the scene item, as an <c>OBS_ALIGN_*</c> bit mask.
    /// </summary>
    [JsonPropertyName("alignment")]
    [Key("alignment")]
    public long? Alignment { get; init; }

    /// <summary>
    /// Bounds type value.
    /// </summary>
    [JsonPropertyName("boundsType")]
    [Key("boundsType")]
    public string? BoundsType { get; init; }

    /// <summary>
    /// Alignment of the bounding box, as an <c>OBS_ALIGN_*</c> bit mask.
    /// </summary>
    [JsonPropertyName("boundsAlignment")]
    [Key("boundsAlignment")]
    public long? BoundsAlignment { get; init; }

    /// <summary>
    /// Bounds width value.
    /// </summary>
    [JsonPropertyName("boundsWidth")]
    [Key("boundsWidth")]
    public double? BoundsWidth { get; init; }

    /// <summary>
    /// Bounds height value.
    /// </summary>
    [JsonPropertyName("boundsHeight")]
    [Key("boundsHeight")]
    public double? BoundsHeight { get; init; }

    /// <summary>
    /// Crop left value.
    /// </summary>
    [JsonPropertyName("cropLeft")]
    [Key("cropLeft")]
    public int? CropLeft { get; init; }

    /// <summary>
    /// Crop top value.
    /// </summary>
    [JsonPropertyName("cropTop")]
    [Key("cropTop")]
    public int? CropTop { get; init; }

    /// <summary>
    /// Crop right value.
    /// </summary>
    [JsonPropertyName("cropRight")]
    [Key("cropRight")]
    public int? CropRight { get; init; }

    /// <summary>
    /// Crop bottom value.
    /// </summary>
    [JsonPropertyName("cropBottom")]
    [Key("cropBottom")]
    public int? CropBottom { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [IgnoreMember]
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public SceneItemTransformPatchStub() { }
}

/// <summary>
/// Represents a common structure for scene item data, often used in lists. Resilient to missing fields.
/// </summary>
/// <remarks>Generated from heuristics based on obs-websocket protocol.</remarks>
[MessagePackObject]
public sealed class SceneItemStub
{
    /// <summary>Scene item ID.</summary>
    [JsonPropertyName("sceneItemId")]
    [Key("sceneItemId")]
    public required long SceneItemId { get; init; }

    /// <summary>Scene item index position.</summary>
    [JsonPropertyName("sceneItemIndex")]
    [Key("sceneItemIndex")]
    public required int SceneItemIndex { get; init; }

    /// <summary>Name of the source associated with the scene item.</summary>
    [JsonPropertyName("sourceName")]
    [Key("sourceName")]
    public required string SourceName { get; init; }

    /// <summary>UUID of the source associated with the scene item.</summary>
    [JsonPropertyName("sourceUuid")]
    [Key("sourceUuid")]
    public required string SourceUuid { get; init; }

    /// <summary>Whether the scene item is enabled (visible).</summary>
    [JsonPropertyName("sceneItemEnabled")]
    [Key("sceneItemEnabled")]
    public required bool SceneItemEnabled { get; init; }

    /// <summary>Whether the scene item is locked.</summary>
    [JsonPropertyName("sceneItemLocked")]
    [Key("sceneItemLocked")]
    public required bool SceneItemLocked { get; init; }

    /// <summary>Whether the source is a group.</summary>
    [JsonPropertyName("isGroup")]
    [Key("isGroup")]
    public bool? IsGroup { get; init; }

    /// <summary>Transform data for the scene item.</summary>
    [JsonPropertyName("sceneItemTransform")]
    [Key("sceneItemTransform")]
    public required SceneItemTransformStub SceneItemTransform { get; init; } // Made nullable for safety

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [IgnoreMember]
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public SceneItemStub() { }
}

/// <summary>
/// Represents a common structure for filter data, often used in lists. Resilient to missing fields.
/// </summary>
/// <remarks>Generated from heuristics based on obs-websocket protocol.</remarks>
[MessagePackObject]
public sealed class FilterStub
{
    /// <summary>Filter name.</summary>
    [JsonPropertyName("filterName")]
    [Key("filterName")]
    public required string FilterName { get; init; }

    /// <summary>Filter kind.</summary>
    [JsonPropertyName("filterKind")]
    [Key("filterKind")]
    public required string FilterKind { get; init; }

    /// <summary>Filter index position.</summary>
    [JsonPropertyName("filterIndex")]
    [Key("filterIndex")]
    public required int FilterIndex { get; init; }

    /// <summary>Whether the filter is enabled.</summary>
    [JsonPropertyName("filterEnabled")]
    [Key("filterEnabled")]
    public required bool FilterEnabled { get; init; }

    /// <summary>Filter settings object.</summary>
    [JsonPropertyName("filterSettings")]
    [Key("filterSettings")]
    public JsonElement? FilterSettings { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [IgnoreMember]
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public FilterStub() { }
}

/// <summary>
/// Represents a common structure for input data, often used in lists. Resilient to missing fields.
/// </summary>
/// <remarks>Generated from heuristics based on obs-websocket protocol.</remarks>
[MessagePackObject]
public sealed class InputStub
{
    /// <summary>Input name.</summary>
    [JsonPropertyName("inputName")]
    [Key("inputName")]
    public required string InputName { get; init; }

    /// <summary>Input UUID.</summary>
    [JsonPropertyName("inputUuid")]
    [Key("inputUuid")]
    public required string InputUuid { get; init; }

    /// <summary>Input kind.</summary>
    [JsonPropertyName("inputKind")]
    [Key("inputKind")]
    public required string InputKind { get; init; }

    /// <summary>Unversioned input kind.</summary>
    [JsonPropertyName("unversionedInputKind")]
    [Key("unversionedInputKind")]
    public required string UnversionedInputKind { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [IgnoreMember]
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public InputStub() { }
}

/// <summary>
/// Represents a common structure for transition data, often used in lists. Resilient to missing fields.
/// </summary>
/// <remarks>Generated from heuristics based on obs-websocket protocol.</remarks>
[MessagePackObject]
public sealed class TransitionStub
{
    /// <summary>Transition name.</summary>
    [JsonPropertyName("transitionName")]
    [Key("transitionName")]
    public required string TransitionName { get; init; }

    /// <summary>Transition UUID.</summary>
    [JsonPropertyName("transitionUuid")]
    [Key("transitionUuid")]
    public required string TransitionUuid { get; init; }

    /// <summary>Transition kind.</summary>
    [JsonPropertyName("transitionKind")]
    [Key("transitionKind")]
    public required string TransitionKind { get; init; }

    /// <summary>Whether the transition is configurable.</summary>
    [JsonPropertyName("transitionConfigurable")]
    [Key("transitionConfigurable")]
    public required bool TransitionConfigurable { get; init; }

    /// <summary>Whether the transition duration is fixed.</summary>
    [JsonPropertyName("transitionFixed")]
    [Key("transitionFixed")]
    public required bool TransitionFixed { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [IgnoreMember]
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public TransitionStub() { }
}

/// <summary>
/// Represents a common structure for output data, often used in lists. Resilient to missing fields.
/// </summary>
/// <remarks>Generated from heuristics based on obs-websocket protocol.</remarks>
[MessagePackObject]
public sealed class OutputStub
{
    /// <summary>Output name.</summary>
    [JsonPropertyName("outputName")]
    [Key("outputName")]
    public required string OutputName { get; init; }

    /// <summary>Output kind.</summary>
    [JsonPropertyName("outputKind")]
    [Key("outputKind")]
    public required string OutputKind { get; init; }

    /// <summary>Whether the output is active.</summary>
    [JsonPropertyName("outputActive")]
    [Key("outputActive")]
    public required bool OutputActive { get; init; }

    /// <summary>
    /// Output width in pixels, which an output that has never started may report as garbage.
    /// </summary>
    /// <remarks>
    /// Wider than a pixel count needs to be, because the wire value is wider. OBS fills this from
    /// <c>obs_output_get_width</c>, which returns <c>uint32_t</c> and is passed through unclamped,
    /// and an idle output can report a value above <see cref="int.MaxValue"/> — a live OBS 32.2.2
    /// sent 2586032160 for an inactive virtual camera. As an <see cref="int"/> that made the whole
    /// <c>GetOutputList</c> response unreadable, intermittently, and only for whoever happened to
    /// have such an output installed. Treat a value over 4096 as "not meaningful", not as a size.
    /// </remarks>
    [JsonPropertyName("outputWidth")]
    [Key("outputWidth")]
    public required long OutputWidth { get; init; }

    /// <summary>
    /// Output height in pixels, which an output that has never started may report as garbage. See
    /// <see cref="OutputWidth"/> for why this is 64 bits wide.
    /// </summary>
    [JsonPropertyName("outputHeight")]
    [Key("outputHeight")]
    public required long OutputHeight { get; init; }

    /// <summary>Output settings.</summary>
    [JsonPropertyName("outputSettings")]
    [Key("outputSettings")]
    public JsonElement? OutputSettings { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [IgnoreMember]
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public OutputStub() { }
}

/// <summary>
/// Represents a common structure for monitor data, often used in lists. Resilient to missing fields.
/// </summary>
/// <remarks>Generated from heuristics based on obs-websocket protocol.</remarks>
[MessagePackObject]
public sealed class MonitorStub
{
    /// <summary>Monitor name.</summary>
    [JsonPropertyName("monitorName")]
    [Key("monitorName")]
    public required string MonitorName { get; init; }

    /// <summary>Monitor index.</summary>
    [JsonPropertyName("monitorIndex")]
    [Key("monitorIndex")]
    public required int MonitorIndex { get; init; }

    /// <summary>Monitor width.</summary>
    [JsonPropertyName("monitorWidth")]
    [Key("monitorWidth")]
    public required int MonitorWidth { get; init; }

    /// <summary>Monitor height.</summary>
    [JsonPropertyName("monitorHeight")]
    [Key("monitorHeight")]
    public required int MonitorHeight { get; init; }

    /// <summary>Monitor position X.</summary>
    [JsonPropertyName("monitorPositionX")]
    [Key("monitorPositionX")]
    public required int MonitorPositionX { get; init; }

    /// <summary>Monitor position Y.</summary>
    [JsonPropertyName("monitorPositionY")]
    [Key("monitorPositionY")]
    public required int MonitorPositionY { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [IgnoreMember]
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public MonitorStub() { }
}

/// <summary>
/// Represents a common structure for input property list items. Resilient to missing fields.
/// </summary>
/// <remarks>Generated from heuristics based on obs-websocket protocol.</remarks>
[MessagePackObject]
public sealed class PropertyItemStub
{
    /// <summary>Item name.</summary>
    [JsonPropertyName("itemName")]
    [Key("itemName")]
    public string? ItemName { get; init; }

    /// <summary>Item value (can be any JSON type).</summary>
    [JsonPropertyName("itemValue")]
    [Key("itemValue")]
    public JsonElement? ItemValue { get; init; }

    /// <summary>Whether the item is enabled.</summary>
    [JsonPropertyName("itemEnabled")]
    [Key("itemEnabled")]
    public bool? ItemEnabled { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [IgnoreMember]
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public PropertyItemStub() { }
}
