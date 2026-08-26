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
    [JsonPropertyName("sceneIndex")]
    [Key("sceneIndex")]
    public required int SceneIndex { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [IgnoreMember]
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public SceneStub() { }
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
    /// Alignment value.
    /// </summary>
    [JsonPropertyName("alignment")]
    [Key("alignment")]
    public required int Alignment { get; init; }

    /// <summary>
    /// Bounds type value.
    /// </summary>
    [JsonPropertyName("boundsType")]
    [Key("boundsType")]
    public required string BoundsType { get; init; }

    /// <summary>
    /// Bounds alignment value.
    /// </summary>
    [JsonPropertyName("boundsAlignment")]
    [Key("boundsAlignment")]
    public required int BoundsAlignment { get; init; }

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
/// Represents a common structure for scene item data, often used in lists. Resilient to missing fields.
/// </summary>
/// <remarks>Generated from heuristics based on obs-websocket protocol.</remarks>
[MessagePackObject]
public sealed class SceneItemStub
{
    /// <summary>Scene item ID.</summary>
    [JsonPropertyName("sceneItemId")]
    [Key("sceneItemId")]
    public required int SceneItemId { get; init; }

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

    /// <summary>Output width.</summary>
    [JsonPropertyName("outputWidth")]
    [Key("outputWidth")]
    public required int OutputWidth { get; init; }

    /// <summary>Output height.</summary>
    [JsonPropertyName("outputHeight")]
    [Key("outputHeight")]
    public required int OutputHeight { get; init; }

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
