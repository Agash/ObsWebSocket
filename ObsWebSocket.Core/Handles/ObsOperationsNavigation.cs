namespace ObsWebSocket.Core;

// Getting from one addressed thing to another without going back through the client.
//
// The operations types are generated from the protocol, which knows nothing about a scene
// containing items or an input carrying filters. That relationship is real and worth navigating,
// so it is written here rather than inferred.

public readonly partial struct SceneOperations
{
    /// <summary>
    /// Looks up this scene's uuid, so later requests survive a rename.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    /// <exception cref="ObsWebSocketResourceNotFoundException">
    /// Thrown when no scene has that name, listing the scenes that do.
    /// </exception>
    public async ValueTask<SceneOperations> ResolveAsync(
        CancellationToken cancellationToken = default
    ) =>
        new(
            client,
            await client.Scenes.ResolveAsync(handle, cancellationToken).ConfigureAwait(false)
        );

    /// <summary>A scene item in this scene, by the numeric id OBS assigned it.</summary>
    /// <param name="sceneItemId">The id, which <c>GetSceneItemList</c> or an event reports.</param>
    public SceneItemOperations Item(long sceneItemId) => new(client, handle.Item(sceneItemId));

    /// <summary>
    /// A scene item in this scene, by the name of the source it shows.
    /// </summary>
    /// <remarks>
    /// Unlike the other lookups this one is not a convenience: OBS addresses scene items by a
    /// number that only <c>GetSceneItemId</c> reports.
    /// </remarks>
    /// <param name="sourceName">The name of the source the item shows.</param>
    /// <param name="searchOffset">
    /// Which match to take when the source appears more than once. 0 is the first from the bottom;
    /// -1 is the topmost.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    /// <exception cref="ObsWebSocketResourceNotFoundException">
    /// Thrown when the scene holds no such source, listing the sources it does hold.
    /// </exception>
    public async ValueTask<SceneItemOperations> ItemAsync(
        string sourceName,
        int searchOffset = 0,
        CancellationToken cancellationToken = default
    ) =>
        new(
            client,
            await client
                .SceneItems.ResolveAsync(handle.Item(sourceName), searchOffset, cancellationToken)
                .ConfigureAwait(false)
        );

    /// <summary>This scene addressed as a source, for the requests that take any source.</summary>
    public SourceOperations AsSource() => new(client, handle.AsSource());
}

public readonly partial struct InputOperations
{
    /// <summary>
    /// Looks up this input's uuid, so later requests survive a rename.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    /// <exception cref="ObsWebSocketResourceNotFoundException">
    /// Thrown when no input has that name, listing the inputs that do.
    /// </exception>
    public async ValueTask<InputOperations> ResolveAsync(
        CancellationToken cancellationToken = default
    ) =>
        new(
            client,
            await client.Inputs.ResolveAsync(handle, cancellationToken).ConfigureAwait(false)
        );

    /// <summary>A filter on this input, by name, which is a filter's whole identity.</summary>
    /// <param name="filterName">The filter's name.</param>
    public FilterOperations Filter(string filterName) => new(client, handle.Filter(filterName));

    /// <summary>This input addressed as a source, for the requests that take any source.</summary>
    public SourceOperations AsSource() => new(client, handle.AsSource());
}

public readonly partial struct SourceOperations
{
    /// <summary>
    /// Looks up this source's uuid. A source is a scene or an input, so this may take two lookups.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    /// <exception cref="ObsWebSocketResourceNotFoundException">
    /// Thrown when neither the inputs nor the scenes have that name.
    /// </exception>
    public async ValueTask<SourceOperations> ResolveAsync(
        CancellationToken cancellationToken = default
    ) =>
        new(
            client,
            await client.Sources.ResolveAsync(handle, cancellationToken).ConfigureAwait(false)
        );

    /// <summary>A filter on this source, by name.</summary>
    /// <param name="filterName">The filter's name.</param>
    public FilterOperations Filter(string filterName) => new(client, handle.Filter(filterName));
}

public readonly partial struct SceneItemOperations
{
    /// <summary>The scene this item lives in.</summary>
    public SceneOperations Scene => new(client, handle.Scene);
}

public readonly partial struct FilterOperations
{
    /// <summary>The source this filter is on.</summary>
    public SourceOperations Source => new(client, handle.Source);
}
