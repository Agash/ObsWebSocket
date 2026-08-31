using ObsWebSocket.Core.Protocol.Common;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Core;

// Turning a name into a uuid is a round trip, so it is never done implicitly.
//
// Resolution lives on the category group rather than on the handle, because a handle holds no
// client: it has to be constructible from a bare string for the request overloads to accept one.
//
// There is no narrow lookup in the protocol. Nothing answers "what is the uuid of the scene called
// X", so resolving a scene means GetSceneList and resolving an input means GetInputList. Both are
// cheap by construction: OBS builds each entry from a handful of field reads, and the websocket
// frame costs more than the enumeration. The list also pays for itself, because a miss can say
// what does exist.

public readonly partial struct ScenesGroup
{
    /// <summary>
    /// Looks up a scene's uuid, so the handle survives a rename and needs no canvas.
    /// </summary>
    /// <param name="scene">The scene to resolve. Already-resolved handles are returned unchanged.</param>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    /// <exception cref="ObsWebSocketResourceNotFoundException">
    /// Thrown when no scene has that name, listing the scenes that do exist.
    /// </exception>
    public async ValueTask<SceneHandle> ResolveAsync(
        SceneHandle scene,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.IsResolved)
        {
            return scene;
        }

        GetSceneListResponseData scenes = await this.GetSceneListAsync(
                new GetSceneListRequestData(canvasUuid: scene.Canvas.Uuid),
                cancellationToken
            )
            .ConfigureAwait(false);

        SceneStub? match = scenes.Scenes.Find(s =>
            string.Equals(s.SceneName, scene.Name, StringComparison.Ordinal)
        );

        return match is not null
            ? SceneHandle.FromUuid(match.SceneUuid)
            : throw ObsWebSocketResourceNotFoundException.For(
                "scene",
                scene.Name!,
                scenes.Scenes.ConvertAll(s => s.SceneName),
                scene.Canvas
            );
    }
}

public readonly partial struct InputsGroup
{
    /// <summary>
    /// Looks up an input's uuid, so the handle survives a rename.
    /// </summary>
    /// <param name="input">The input to resolve. Already-resolved handles are returned unchanged.</param>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    /// <exception cref="ObsWebSocketResourceNotFoundException">
    /// Thrown when no input has that name, listing the inputs that do exist.
    /// </exception>
    public async ValueTask<InputHandle> ResolveAsync(
        InputHandle input,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.IsResolved)
        {
            return input;
        }

        GetInputListResponseData inputs = await this.GetInputListAsync(
                new GetInputListRequestData(),
                cancellationToken
            )
            .ConfigureAwait(false);

        InputStub? match = inputs.Inputs.Find(i =>
            string.Equals(i.InputName, input.Name, StringComparison.Ordinal)
        );

        return match is not null
            ? InputHandle.FromUuid(match.InputUuid)
            : throw ObsWebSocketResourceNotFoundException.For(
                "input",
                input.Name!,
                inputs.Inputs.ConvertAll(i => i.InputName),
                null
            );
    }
}

public readonly partial struct CanvasesGroup
{
    /// <summary>
    /// Looks up a canvas's uuid.
    /// </summary>
    /// <remarks>
    /// The one lookup the protocol cannot express itself: no request takes a canvas name, so this
    /// is the only way to address a canvas you know by name. It is what obs-websocket-js was
    /// considering adding as a helper for the same reason.
    /// </remarks>
    /// <param name="canvas">The canvas to resolve. The main canvas and resolved handles come back unchanged.</param>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    /// <exception cref="ObsWebSocketResourceNotFoundException">
    /// Thrown when no canvas has that name, listing the canvases that do exist.
    /// </exception>
    public async ValueTask<CanvasHandle> ResolveAsync(
        CanvasHandle canvas,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (canvas.IsResolved || canvas.Name is null)
        {
            return canvas;
        }

        GetCanvasListResponseData canvases = await this.GetCanvasListAsync(cancellationToken)
            .ConfigureAwait(false);

        CanvasStub? match = canvases.Canvases.Find(c =>
            string.Equals(c.CanvasName, canvas.Name, StringComparison.Ordinal)
        );

        return match is not null
            ? CanvasHandle.FromUuid(match.CanvasUuid)
            : throw ObsWebSocketResourceNotFoundException.For(
                "canvas",
                canvas.Name,
                canvases.Canvases.ConvertAll(c => c.CanvasName),
                null
            );
    }
}

public readonly partial struct SourcesGroup
{
    /// <summary>
    /// Looks up a source's uuid. A source is a scene or an input, so this may take two lookups.
    /// </summary>
    /// <remarks>
    /// Inputs are checked first, because the requests that take a bare source are mostly about
    /// inputs, so the scene list is usually never fetched at all.
    /// </remarks>
    /// <param name="source">The source to resolve. Already-resolved handles are returned unchanged.</param>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    /// <exception cref="ObsWebSocketResourceNotFoundException">
    /// Thrown when neither the inputs nor the scenes have that name.
    /// </exception>
    public async ValueTask<SourceHandle> ResolveAsync(
        SourceHandle source,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.IsResolved)
        {
            return source;
        }

        GetInputListResponseData inputs = await client
            .Inputs.GetInputListAsync(new GetInputListRequestData(), cancellationToken)
            .ConfigureAwait(false);

        InputStub? input = inputs.Inputs.Find(i =>
            string.Equals(i.InputName, source.Name, StringComparison.Ordinal)
        );
        if (input is not null)
        {
            return SourceHandle.FromUuid(input.InputUuid);
        }

        GetSceneListResponseData scenes = await client
            .Scenes.GetSceneListAsync(
                new GetSceneListRequestData(canvasUuid: source.Canvas.Uuid),
                cancellationToken
            )
            .ConfigureAwait(false);

        SceneStub? scene = scenes.Scenes.Find(s =>
            string.Equals(s.SceneName, source.Name, StringComparison.Ordinal)
        );

        return scene is not null
            ? SourceHandle.FromUuid(scene.SceneUuid)
            : throw ObsWebSocketResourceNotFoundException.For(
                "source",
                source.Name!,
                [
                    .. inputs.Inputs.ConvertAll(i => i.InputName),
                    .. scenes.Scenes.ConvertAll(s => s.SceneName),
                ],
                source.Canvas
            );
    }
}

public readonly partial struct SceneItemsGroup
{
    /// <summary>
    /// Looks up the numeric id OBS gave a source inside a scene.
    /// </summary>
    /// <remarks>
    /// The one resolution that is not a convenience. OBS addresses scene items by a number nothing
    /// else tells you, so <c>GetSceneItemId</c> is the only way in.
    /// </remarks>
    /// <param name="item">The scene and source name to look up.</param>
    /// <param name="searchOffset">
    /// Which match to take when a source appears more than once in the scene. 0 is the first from
    /// the bottom; -1 is the topmost.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    /// <exception cref="ObsWebSocketResourceNotFoundException">
    /// Thrown when the scene holds no such source, listing the sources it does hold.
    /// </exception>
    public async ValueTask<SceneItemHandle> ResolveAsync(
        UnresolvedSceneItem item,
        int searchOffset = 0,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(item);

        try
        {
            GetSceneItemIdResponseData found = await this.GetSceneItemIdAsync(
                    new GetSceneItemIdRequestData(
                        sourceName: item.SourceName,
                        canvasUuid: item.Scene.Canvas.Uuid,
                        sceneName: item.Scene.Name,
                        sceneUuid: item.Scene.Uuid,
                        searchOffset: searchOffset
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);

            return SceneItemHandle.For(item.Scene, found.SceneItemId);
        }
        catch (ObsWebSocketRequestException ex)
            when (ex.StatusCode == Protocol.Generated.RequestStatusCode.ResourceNotFound)
        {
            // OBS says only that it did not find it. The scene's contents are one more request and
            // turn that into something the caller can act on.
            List<string> present = [];
            try
            {
                GetSceneItemListResponseData items = await this.GetSceneItemListAsync(
                        new GetSceneItemListRequestData(
                            canvasUuid: item.Scene.Canvas.Uuid,
                            sceneName: item.Scene.Name,
                            sceneUuid: item.Scene.Uuid
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                present = items.SceneItems.ConvertAll(i => i.SourceName);
            }
            catch (ObsWebSocketRequestException)
            {
                // Deliberately not logged: the scene may be gone too, and the original failure is
                // the one worth reporting.
            }

            throw ObsWebSocketResourceNotFoundException.For(
                $"source in {item.Scene}",
                item.SourceName,
                present,
                item.Scene.Canvas,
                ex
            );
        }
    }
}
