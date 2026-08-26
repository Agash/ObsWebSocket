using System.Diagnostics.CodeAnalysis;

namespace ObsWebSocket.Core;

/// <summary>
/// How a request addresses one thing in OBS: by name, or by uuid.
/// </summary>
/// <remarks>
/// The protocol takes both as optional fields and resolves them in a fixed order. From
/// <c>Request::AcquireSource</c>: a uuid wins outright, a name is only read when no uuid was sent,
/// the canvas is only consulted on the name path, and neither field present is
/// <c>MissingRequestField</c>. So sending both is not an error, it silently ignores the name, and
/// sending neither compiles today and fails at runtime.
/// <para>
/// A handle is that choice made once and carried, rather than restated on every call. It holds
/// identity only: OBS state drifts, and a handle that cached a name or a scene item id would go
/// stale without saying so.
/// </para>
/// </remarks>
public interface IObsHandle
{
    /// <summary>The name this handle addresses by, or <see langword="null"/> when it holds a uuid.</summary>
    string? Name { get; }

    /// <summary>The uuid this handle addresses by, or <see langword="null"/> when it holds a name.</summary>
    /// <remarks>
    /// Kept as the wire string and never parsed. OBS produces RFC 4122 uuids, but a response is
    /// not the place to discover that one day it did not.
    /// </remarks>
    string? Uuid { get; }

    /// <summary>
    /// Whether this handle addresses by uuid, and so survives a rename.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Uuid))]
    bool IsResolved { get; }
}

/// <summary>
/// A canvas. Every canvas-scoped request takes a uuid and there is no <c>canvasName</c> field in
/// the protocol at all, so a name has to be resolved before it can be used.
/// </summary>
/// <remarks>
/// Omitting the canvas means the main canvas, which is why <see cref="Main"/> is a value rather
/// than a null check at every call site.
/// </remarks>
public sealed record CanvasHandle : IObsHandle
{
    private CanvasHandle(string? name, string? uuid)
    {
        Name = name;
        Uuid = uuid;
    }

    /// <summary>The main canvas, which is what OBS uses when no canvas uuid is sent.</summary>
    public static CanvasHandle Main { get; } = new(null, null);

    /// <inheritdoc/>
    public string? Name { get; }

    /// <inheritdoc/>
    public string? Uuid { get; }

    /// <inheritdoc/>
    [MemberNotNullWhen(true, nameof(Uuid))]
    public bool IsResolved => Uuid is not null;

    /// <summary>Addresses a canvas by name, which needs resolving before any request accepts it.</summary>
    public static CanvasHandle FromName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new CanvasHandle(name, null);
    }

    /// <summary>Addresses a canvas by uuid, with no lookup.</summary>
    public static CanvasHandle FromUuid(Guid uuid) => new(null, uuid.ToString("D"));

    /// <summary>Addresses a canvas by uuid as OBS wrote it.</summary>
    public static CanvasHandle FromUuid(string uuid)
    {
        ArgumentException.ThrowIfNullOrEmpty(uuid);
        return new CanvasHandle(null, uuid);
    }

    /// <summary>Addresses a canvas by name.</summary>
    public static implicit operator CanvasHandle(string name) => FromName(name);

    /// <summary>Addresses a canvas by uuid.</summary>
    public static implicit operator CanvasHandle(Guid uuid) => FromUuid(uuid);

    /// <summary>A scene on this canvas, addressed by name.</summary>
    public SceneHandle Scene(string name) => SceneHandle.FromName(name, this);

    /// <inheritdoc cref="ToString()"/>
    public override string ToString() =>
        IsResolved ? $"canvas {Uuid}"
        : Name is not null ? $"canvas '{Name}'"
        : "the main canvas";
}

/// <summary>
/// A scene, addressed by name or by uuid.
/// </summary>
/// <remarks>
/// A name is only unique within a canvas, which is why the canvas travels with a name handle and
/// is dropped from a uuid handle: OBS reads <c>canvasUuid</c> only on the name path and ignores it
/// otherwise.
/// </remarks>
public sealed record SceneHandle : IObsHandle
{
    private SceneHandle(string? name, string? uuid, CanvasHandle canvas)
    {
        Name = name;
        Uuid = uuid;
        Canvas = canvas;
    }

    /// <inheritdoc/>
    public string? Name { get; }

    /// <inheritdoc/>
    public string? Uuid { get; }

    /// <summary>
    /// The canvas a name is looked up in. Meaningless once the handle is resolved, because OBS
    /// does not read the canvas field when a uuid is present.
    /// </summary>
    public CanvasHandle Canvas { get; }

    /// <inheritdoc/>
    [MemberNotNullWhen(true, nameof(Uuid))]
    public bool IsResolved => Uuid is not null;

    /// <summary>Addresses a scene by name, optionally on a canvas other than the main one.</summary>
    public static SceneHandle FromName(string name, CanvasHandle? canvas = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new SceneHandle(name, null, canvas ?? CanvasHandle.Main);
    }

    /// <summary>Addresses a scene by uuid, with no lookup.</summary>
    public static SceneHandle FromUuid(Guid uuid) => FromUuid(uuid.ToString("D"));

    /// <summary>Addresses a scene by uuid as OBS wrote it.</summary>
    public static SceneHandle FromUuid(string uuid)
    {
        ArgumentException.ThrowIfNullOrEmpty(uuid);
        return new SceneHandle(null, uuid, CanvasHandle.Main);
    }

    /// <summary>Addresses a scene by name on the main canvas.</summary>
    public static implicit operator SceneHandle(string name) => FromName(name);

    /// <summary>Addresses a scene by uuid.</summary>
    public static implicit operator SceneHandle(Guid uuid) => FromUuid(uuid);

    /// <summary>
    /// A scene item in this scene, by the numeric id OBS assigned it.
    /// </summary>
    public SceneItemHandle Item(int sceneItemId) => SceneItemHandle.For(this, sceneItemId);

    /// <summary>
    /// A scene item in this scene, by the name of the source it shows. Needs resolving, because
    /// only <c>GetSceneItemId</c> knows the id.
    /// </summary>
    public UnresolvedSceneItem Item(string sourceName) =>
        new(this, sourceName ?? throw new ArgumentNullException(nameof(sourceName)));

    /// <summary>This scene addressed as a source, for the requests that take any source.</summary>
    public SourceHandle AsSource() =>
        IsResolved ? SourceHandle.FromUuid(Uuid) : SourceHandle.FromName(Name!, Canvas);

    /// <inheritdoc cref="ToString()"/>
    public override string ToString() => IsResolved ? $"scene {Uuid}" : $"scene '{Name}'";
}

/// <summary>
/// An input, addressed by name or by uuid.
/// </summary>
/// <remarks>
/// Input requests carry no canvas field: an input is not scoped to one.
/// </remarks>
public sealed record InputHandle : IObsHandle
{
    private InputHandle(string? name, string? uuid)
    {
        Name = name;
        Uuid = uuid;
    }

    /// <inheritdoc/>
    public string? Name { get; }

    /// <inheritdoc/>
    public string? Uuid { get; }

    /// <inheritdoc/>
    [MemberNotNullWhen(true, nameof(Uuid))]
    public bool IsResolved => Uuid is not null;

    /// <summary>Addresses an input by name.</summary>
    public static InputHandle FromName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new InputHandle(name, null);
    }

    /// <summary>Addresses an input by uuid, with no lookup.</summary>
    public static InputHandle FromUuid(Guid uuid) => FromUuid(uuid.ToString("D"));

    /// <summary>Addresses an input by uuid as OBS wrote it.</summary>
    public static InputHandle FromUuid(string uuid)
    {
        ArgumentException.ThrowIfNullOrEmpty(uuid);
        return new InputHandle(null, uuid);
    }

    /// <summary>Addresses an input by name.</summary>
    public static implicit operator InputHandle(string name) => FromName(name);

    /// <summary>Addresses an input by uuid.</summary>
    public static implicit operator InputHandle(Guid uuid) => FromUuid(uuid);

    /// <summary>A filter on this input, by name. Filter names are the identity; nothing to resolve.</summary>
    public FilterHandle Filter(string filterName) => FilterHandle.For(AsSource(), filterName);

    /// <summary>This input addressed as a source, for the requests that take any source.</summary>
    public SourceHandle AsSource() =>
        IsResolved ? SourceHandle.FromUuid(Uuid) : SourceHandle.FromName(Name!);

    /// <inheritdoc cref="ToString()"/>
    public override string ToString() => IsResolved ? $"input {Uuid}" : $"input '{Name}'";
}

/// <summary>
/// A source, which in OBS means either a scene or an input. The requests that take a source accept
/// both, and validate the kind themselves.
/// </summary>
public sealed record SourceHandle : IObsHandle
{
    private SourceHandle(string? name, string? uuid, CanvasHandle canvas)
    {
        Name = name;
        Uuid = uuid;
        Canvas = canvas;
    }

    /// <inheritdoc/>
    public string? Name { get; }

    /// <inheritdoc/>
    public string? Uuid { get; }

    /// <summary>The canvas a name is looked up in.</summary>
    public CanvasHandle Canvas { get; }

    /// <inheritdoc/>
    [MemberNotNullWhen(true, nameof(Uuid))]
    public bool IsResolved => Uuid is not null;

    /// <summary>Addresses a source by name.</summary>
    public static SourceHandle FromName(string name, CanvasHandle? canvas = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new SourceHandle(name, null, canvas ?? CanvasHandle.Main);
    }

    /// <summary>Addresses a source by uuid, with no lookup.</summary>
    public static SourceHandle FromUuid(Guid uuid) => FromUuid(uuid.ToString("D"));

    /// <summary>Addresses a source by uuid as OBS wrote it.</summary>
    public static SourceHandle FromUuid(string uuid)
    {
        ArgumentException.ThrowIfNullOrEmpty(uuid);
        return new SourceHandle(null, uuid, CanvasHandle.Main);
    }

    /// <summary>Addresses a source by name.</summary>
    public static implicit operator SourceHandle(string name) => FromName(name);

    /// <summary>Addresses a source by uuid.</summary>
    public static implicit operator SourceHandle(Guid uuid) => FromUuid(uuid);

    /// <summary>A filter on this source, by name.</summary>
    public FilterHandle Filter(string filterName) => FilterHandle.For(this, filterName);

    /// <inheritdoc cref="ToString()"/>
    public override string ToString() => IsResolved ? $"source {Uuid}" : $"source '{Name}'";
}

/// <summary>
/// A scene item: a scene, and the numeric id OBS gave one source inside it.
/// </summary>
/// <remarks>
/// The id is only meaningful within its scene, and only while the item exists. Removing the item
/// and adding it back gives a new one.
/// </remarks>
public sealed record SceneItemHandle
{
    private SceneItemHandle(SceneHandle scene, int sceneItemId)
    {
        Scene = scene;
        SceneItemId = sceneItemId;
    }

    /// <summary>The scene the item lives in.</summary>
    public SceneHandle Scene { get; }

    /// <summary>The numeric id OBS assigned the item.</summary>
    public int SceneItemId { get; }

    /// <summary>Builds a handle for an id already known.</summary>
    public static SceneItemHandle For(SceneHandle scene, int sceneItemId)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentOutOfRangeException.ThrowIfNegative(sceneItemId);
        return new SceneItemHandle(scene, sceneItemId);
    }

    /// <inheritdoc cref="ToString()"/>
    public override string ToString() => $"item {SceneItemId} in {Scene}";
}

/// <summary>
/// A scene item named by its source rather than its id, which OBS cannot act on until the id is
/// looked up.
/// </summary>
/// <remarks>
/// A separate type rather than a nullable id, so a scene item that has not been resolved cannot be
/// passed to a request that needs one.
/// </remarks>
public sealed record UnresolvedSceneItem(SceneHandle Scene, string SourceName);

/// <summary>
/// A filter on a source, addressed by name.
/// </summary>
/// <remarks>
/// Filters have no uuid in the protocol, so the name is the identity and there is nothing to
/// resolve. A rename moves the filter out from under the handle.
/// </remarks>
public sealed record FilterHandle
{
    private FilterHandle(SourceHandle source, string filterName)
    {
        Source = source;
        FilterName = filterName;
    }

    /// <summary>The source the filter is on.</summary>
    public SourceHandle Source { get; }

    /// <summary>The filter's name, which is its identity.</summary>
    public string FilterName { get; }

    /// <summary>Builds a handle for a filter on a source.</summary>
    public static FilterHandle For(SourceHandle source, string filterName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(filterName);
        return new FilterHandle(source, filterName);
    }

    /// <inheritdoc cref="ToString()"/>
    public override string ToString() => $"filter '{FilterName}' on {Source}";
}
