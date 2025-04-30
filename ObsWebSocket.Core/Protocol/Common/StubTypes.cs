using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObsWebSocket.Core.Protocol.Common;

/// <summary>
/// Represents a common structure for scene data, often used in lists. Resilient to missing fields.
/// </summary>
/// <remarks>Generated from heuristics based on obs-websocket protocol.</remarks>
public sealed record SceneStub
{
    /// <summary>Scene name.</summary>
    [JsonPropertyName("sceneName")]
    public string? SceneName { get; init; }

    /// <summary>Scene UUID.</summary>
    [JsonPropertyName("sceneUuid")]
    public string? SceneUuid { get; init; }

    /// <summary>Scene index position.</summary>
    [JsonPropertyName("sceneIndex")]
    public double? SceneIndex { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public SceneStub() { }

    /// <summary>
    /// Initializes a new instance with all properties specified.
    /// </summary>
    public SceneStub(string? sceneName, string? sceneUuid, double? sceneIndex)
    {
        SceneName = sceneName;
        SceneUuid = sceneUuid;
        SceneIndex = sceneIndex;
    }
}

/// <summary>
/// Represents a common structure for scene item transform data. Resilient to missing fields.
/// </summary>
/// <remarks>Generated from heuristics based on obs-websocket protocol.</remarks>
public sealed record SceneItemTransformStub
{
    [JsonPropertyName("positionX")]
    public double? PositionX { get; init; }

    [JsonPropertyName("positionY")]
    public double? PositionY { get; init; }

    [JsonPropertyName("rotation")]
    public double? Rotation { get; init; }

    [JsonPropertyName("scaleX")]
    public double? ScaleX { get; init; }

    [JsonPropertyName("scaleY")]
    public double? ScaleY { get; init; }

    [JsonPropertyName("width")]
    public double? Width { get; init; }

    [JsonPropertyName("height")]
    public double? Height { get; init; }

    [JsonPropertyName("sourceWidth")]
    public double? SourceWidth { get; init; }

    [JsonPropertyName("sourceHeight")]
    public double? SourceHeight { get; init; }

    [JsonPropertyName("alignment")]
    public double? Alignment { get; init; }

    [JsonPropertyName("boundsType")]
    public string? BoundsType { get; init; }

    [JsonPropertyName("boundsAlignment")]
    public double? BoundsAlignment { get; init; }

    [JsonPropertyName("boundsWidth")]
    public double? BoundsWidth { get; init; }

    [JsonPropertyName("boundsHeight")]
    public double? BoundsHeight { get; init; }

    [JsonPropertyName("cropLeft")]
    public double? CropLeft { get; init; }

    [JsonPropertyName("cropTop")]
    public double? CropTop { get; init; }

    [JsonPropertyName("cropRight")]
    public double? CropRight { get; init; }

    [JsonPropertyName("cropBottom")]
    public double? CropBottom { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public SceneItemTransformStub() { }

    /// <summary>
    /// Initializes a new instance with all properties specified.
    /// </summary>
    public SceneItemTransformStub(
        double? positionX,
        double? positionY,
        double? rotation,
        double? scaleX,
        double? scaleY,
        double? width,
        double? height,
        double? sourceWidth,
        double? sourceHeight,
        double? alignment,
        string? boundsType,
        double? boundsAlignment,
        double? boundsWidth,
        double? boundsHeight,
        double? cropLeft,
        double? cropTop,
        double? cropRight,
        double? cropBottom
    )
    {
        PositionX = positionX;
        PositionY = positionY;
        Rotation = rotation;
        ScaleX = scaleX;
        ScaleY = scaleY;
        Width = width;
        Height = height;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        Alignment = alignment;
        BoundsType = boundsType;
        BoundsAlignment = boundsAlignment;
        BoundsWidth = boundsWidth;
        BoundsHeight = boundsHeight;
        CropLeft = cropLeft;
        CropTop = cropTop;
        CropRight = cropRight;
        CropBottom = cropBottom;
    }
}

/// <summary>
/// Represents a common structure for scene item data, often used in lists. Resilient to missing fields.
/// </summary>
/// <remarks>Generated from heuristics based on obs-websocket protocol.</remarks>
public sealed record SceneItemStub
{
    /// <summary>Scene item ID.</summary>
    [JsonPropertyName("sceneItemId")]
    public double? SceneItemId { get; init; }

    /// <summary>Scene item index position.</summary>
    [JsonPropertyName("sceneItemIndex")]
    public double? SceneItemIndex { get; init; }

    /// <summary>Name of the source associated with the scene item.</summary>
    [JsonPropertyName("sourceName")]
    public string? SourceName { get; init; }

    /// <summary>UUID of the source associated with the scene item.</summary>
    [JsonPropertyName("sourceUuid")]
    public string? SourceUuid { get; init; }

    /// <summary>Whether the scene item is enabled (visible).</summary>
    [JsonPropertyName("sceneItemEnabled")]
    public bool? SceneItemEnabled { get; init; }

    /// <summary>Whether the scene item is locked.</summary>
    [JsonPropertyName("sceneItemLocked")]
    public bool? SceneItemLocked { get; init; }

    /// <summary>Whether the source is a group.</summary>
    [JsonPropertyName("isGroup")]
    public bool? IsGroup { get; init; }

    /// <summary>Transform data for the scene item.</summary>
    [JsonPropertyName("sceneItemTransform")]
    public SceneItemTransformStub? SceneItemTransform { get; init; } // Made nullable for safety

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public SceneItemStub() { }

    /// <summary>
    /// Initializes a new instance with all properties specified.
    /// </summary>
    public SceneItemStub(
        double? sceneItemId,
        double? sceneItemIndex,
        string? sourceName,
        string? sourceUuid,
        bool? sceneItemEnabled,
        bool? sceneItemLocked,
        SceneItemTransformStub? sceneItemTransform,
        bool? isGroup = null
    )
    {
        SceneItemId = sceneItemId;
        SceneItemIndex = sceneItemIndex;
        SourceName = sourceName;
        SourceUuid = sourceUuid;
        SceneItemEnabled = sceneItemEnabled;
        SceneItemLocked = sceneItemLocked;
        SceneItemTransform = sceneItemTransform;
        IsGroup = isGroup;
    }
}

/// <summary>
/// Represents a common structure for filter data, often used in lists. Resilient to missing fields.
/// </summary>
/// <remarks>Generated from heuristics based on obs-websocket protocol.</remarks>
public sealed record FilterStub
{
    /// <summary>Filter name.</summary>
    [JsonPropertyName("filterName")]
    public string? FilterName { get; init; }

    /// <summary>Filter kind.</summary>
    [JsonPropertyName("filterKind")]
    public string? FilterKind { get; init; }

    /// <summary>Filter index position.</summary>
    [JsonPropertyName("filterIndex")]
    public double? FilterIndex { get; init; }

    /// <summary>Whether the filter is enabled.</summary>
    [JsonPropertyName("filterEnabled")]
    public bool? FilterEnabled { get; init; }

    /// <summary>Filter settings object.</summary>
    [JsonPropertyName("filterSettings")]
    public JsonElement? FilterSettings { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public FilterStub() { }

    /// <summary>
    /// Initializes a new instance with all properties specified.
    /// </summary>
    public FilterStub(
        string? filterName,
        string? filterKind,
        double? filterIndex,
        bool? filterEnabled,
        JsonElement? filterSettings = null
    )
    {
        FilterName = filterName;
        FilterKind = filterKind;
        FilterIndex = filterIndex;
        FilterEnabled = filterEnabled;
        FilterSettings = filterSettings;
    }
}

/// <summary>
/// Represents a common structure for input data, often used in lists. Resilient to missing fields.
/// </summary>
/// <remarks>Generated from heuristics based on obs-websocket protocol.</remarks>
public sealed record InputStub
{
    /// <summary>Input name.</summary>
    [JsonPropertyName("inputName")]
    public string? InputName { get; init; }

    /// <summary>Input UUID.</summary>
    [JsonPropertyName("inputUuid")]
    public string? InputUuid { get; init; }

    /// <summary>Input kind.</summary>
    [JsonPropertyName("inputKind")]
    public string? InputKind { get; init; }

    /// <summary>Unversioned input kind.</summary>
    [JsonPropertyName("unversionedInputKind")]
    public string? UnversionedInputKind { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public InputStub() { }

    /// <summary>
    /// Initializes a new instance with all properties specified.
    /// </summary>
    public InputStub(
        string? inputName,
        string? inputUuid,
        string? inputKind,
        string? unversionedInputKind
    )
    {
        InputName = inputName;
        InputUuid = inputUuid;
        InputKind = inputKind;
        UnversionedInputKind = unversionedInputKind;
    }
}

/// <summary>
/// Represents a common structure for transition data, often used in lists. Resilient to missing fields.
/// </summary>
/// <remarks>Generated from heuristics based on obs-websocket protocol.</remarks>
public sealed record TransitionStub
{
    /// <summary>Transition name.</summary>
    [JsonPropertyName("transitionName")]
    public string? TransitionName { get; init; }

    /// <summary>Transition UUID.</summary>
    [JsonPropertyName("transitionUuid")]
    public string? TransitionUuid { get; init; }

    /// <summary>Transition kind.</summary>
    [JsonPropertyName("transitionKind")]
    public string? TransitionKind { get; init; }

    /// <summary>Whether the transition is configurable.</summary>
    [JsonPropertyName("transitionConfigurable")]
    public bool? TransitionConfigurable { get; init; }

    /// <summary>Whether the transition duration is fixed.</summary>
    [JsonPropertyName("transitionFixed")]
    public bool? TransitionFixed { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public TransitionStub() { }

    /// <summary>
    /// Initializes a new instance with all properties specified.
    /// </summary>
    public TransitionStub(
        string? transitionName,
        string? transitionUuid,
        string? transitionKind,
        bool? transitionConfigurable,
        bool? transitionFixed
    )
    {
        TransitionName = transitionName;
        TransitionUuid = transitionUuid;
        TransitionKind = transitionKind;
        TransitionConfigurable = transitionConfigurable;
        TransitionFixed = transitionFixed;
    }
}

/// <summary>
/// Represents a common structure for output data, often used in lists. Resilient to missing fields.
/// </summary>
/// <remarks>Generated from heuristics based on obs-websocket protocol.</remarks>
public sealed record OutputStub
{
    /// <summary>Output name.</summary>
    [JsonPropertyName("outputName")]
    public string? OutputName { get; init; }

    /// <summary>Output kind.</summary>
    [JsonPropertyName("outputKind")]
    public string? OutputKind { get; init; }

    /// <summary>Whether the output is active.</summary>
    [JsonPropertyName("outputActive")]
    public bool? OutputActive { get; init; }

    /// <summary>Output width.</summary>
    [JsonPropertyName("outputWidth")]
    public double? OutputWidth { get; init; }

    /// <summary>Output height.</summary>
    [JsonPropertyName("outputHeight")]
    public double? OutputHeight { get; init; }

    /// <summary>Output settings.</summary>
    [JsonPropertyName("outputSettings")]
    public JsonElement? OutputSettings { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public OutputStub() { }

    /// <summary>
    /// Initializes a new instance with all properties specified.
    /// </summary>
    public OutputStub(
        string? outputName,
        string? outputKind,
        bool? outputActive,
        double? outputWidth,
        double? outputHeight,
        JsonElement? outputSettings = null
    )
    {
        OutputName = outputName;
        OutputKind = outputKind;
        OutputActive = outputActive;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        OutputSettings = outputSettings;
    }
}

/// <summary>
/// Represents a common structure for monitor data, often used in lists. Resilient to missing fields.
/// </summary>
/// <remarks>Generated from heuristics based on obs-websocket protocol.</remarks>
public sealed record MonitorStub
{
    /// <summary>Monitor name.</summary>
    [JsonPropertyName("monitorName")]
    public string? MonitorName { get; init; }

    /// <summary>Monitor index.</summary>
    [JsonPropertyName("monitorIndex")]
    public double? MonitorIndex { get; init; }

    /// <summary>Monitor width.</summary>
    [JsonPropertyName("monitorWidth")]
    public double? MonitorWidth { get; init; }

    /// <summary>Monitor height.</summary>
    [JsonPropertyName("monitorHeight")]
    public double? MonitorHeight { get; init; }

    /// <summary>Monitor position X.</summary>
    [JsonPropertyName("monitorPositionX")]
    public double? MonitorPositionX { get; init; }

    /// <summary>Monitor position Y.</summary>
    [JsonPropertyName("monitorPositionY")]
    public double? MonitorPositionY { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public MonitorStub() { }

    /// <summary>
    /// Initializes a new instance with all properties specified.
    /// </summary>
    public MonitorStub(
        string? monitorName,
        double? monitorIndex,
        double? monitorWidth,
        double? monitorHeight,
        double? monitorPositionX,
        double? monitorPositionY
    )
    {
        MonitorName = monitorName;
        MonitorIndex = monitorIndex;
        MonitorWidth = monitorWidth;
        MonitorHeight = monitorHeight;
        MonitorPositionX = monitorPositionX;
        MonitorPositionY = monitorPositionY;
    }
}

/// <summary>
/// Represents a common structure for input property list items. Resilient to missing fields.
/// </summary>
/// <remarks>Generated from heuristics based on obs-websocket protocol.</remarks>
public sealed record PropertyItemStub
{
    /// <summary>Item name.</summary>
    [JsonPropertyName("itemName")]
    public string? ItemName { get; init; }

    /// <summary>Item value (can be any JSON type).</summary>
    [JsonPropertyName("itemValue")]
    public JsonElement? ItemValue { get; init; }

    /// <summary>Whether the item is enabled.</summary>
    [JsonPropertyName("itemEnabled")]
    public bool? ItemEnabled { get; init; }

    /// <summary>Captures any extra fields not explicitly defined in the stub.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    /// <summary>Initializes a new instance for deserialization via <see cref="JsonConstructorAttribute"/>.</summary>
    [JsonConstructor]
    public PropertyItemStub() { }

    /// <summary>
    /// Initializes a new instance with all properties specified.
    /// </summary>
    public PropertyItemStub(string? itemName, JsonElement? itemValue, bool? itemEnabled)
    {
        ItemName = itemName;
        ItemValue = itemValue;
        ItemEnabled = itemEnabled;
    }
}
