using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using ObsWebSocket.Core.Events;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Common;
using ObsWebSocket.Core.Protocol.Common.FilterSettings;
using ObsWebSocket.Core.Protocol.Common.InputSettings;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Core;

/// <summary>
/// Conveniences for the <c>SceneItems</c> category, alongside its generated requests.
/// </summary>
public readonly partial struct SceneItemsGroup
{
    /// <summary>
    /// Sets or toggles the enabled (visibility) state of a scene item, identified by its numeric ID.
    /// </summary>
    /// <param name="sceneName">The name of the scene containing the item.</param>
    /// <param name="sceneItemId">The numeric ID of the scene item.</param>
    /// <param name="isEnabled">The desired state (true=enabled, false=disabled). If null, the state will be toggled.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The final enabled state of the scene item after the operation.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if OBS fails the operation (e.g., scene/item not found).</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task<bool> SetSceneItemEnabledAsync(
        string sceneName,
        int sceneItemId,
        bool? isEnabled = null, // If null, toggles; otherwise sets to the specified state
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(sceneName);
        client.EnsureConnected();

        bool targetState;
        if (isEnabled.HasValue)
        {
            targetState = isEnabled.Value;
        }
        else
        {
            // Need to get current state to toggle
            GetSceneItemEnabledResponseData currentStateResponse =
                await client
                    .SceneItems.GetSceneItemEnabledAsync(
                        new GetSceneItemEnabledRequestData(
                            sceneItemId: sceneItemId,
                            sceneName: sceneName
                        ),
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false)
                ?? throw new ObsWebSocketException(
                    $"Failed to get current enabled state for item ID {sceneItemId} in scene '{sceneName}'."
                );
            targetState = !currentStateResponse.SceneItemEnabled;
        }

        await client
            .SceneItems.SetSceneItemEnabledAsync(
                new SetSceneItemEnabledRequestData(
                    sceneItemId: sceneItemId,
                    sceneItemEnabled: targetState,
                    sceneName: sceneName
                ),
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);

        return targetState;
    }

    /// <summary>
    /// Sets or toggles the enabled (visibility) state of a scene item, identified by its source name within a scene.
    /// </summary>
    /// <param name="sceneName">The name of the scene containing the item.</param>
    /// <param name="sourceName">The name of the source corresponding to the scene item.</param>
    /// <param name="isEnabled">The desired state (true=enabled, false=disabled). If null, the state will be toggled.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The final enabled state of the scene item after the operation.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if OBS fails the operation (e.g., scene/item not found).</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    /// <exception cref="SceneItemNotFoundException">Thrown if the source name is not found within the specified scene.</exception>
    public async Task<bool> SetSceneItemEnabledAsync(
        string sceneName,
        string sourceName,
        bool? isEnabled = null, // If null, toggles; otherwise sets to the specified state
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(sceneName);
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        client.EnsureConnected();

        int? sceneItemId = await client
            .SceneItems.FindSceneItemIdAsync(sceneName, sourceName, cancellationToken)
            .ConfigureAwait(false);

        return sceneItemId.HasValue
            ? await client
                .SceneItems.SetSceneItemEnabledAsync(
                    sceneName,
                    sceneItemId.Value,
                    isEnabled,
                    cancellationToken
                )
                .ConfigureAwait(false)
            : throw new SceneItemNotFoundException(
                $"Source '{sourceName}' not found in scene '{sceneName}'. Cannot set enabled state."
            );
    }

    /// <summary>
    /// Returns the scene item id for a source within a scene, or <see langword="null"/> when the
    /// scene does not contain it.
    /// </summary>
    /// <param name="sceneName">The name of the scene to search.</param>
    /// <param name="sourceName">The name of the source to locate.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The scene item id, or <see langword="null"/> if the source is not in the scene.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task<int?> FindSceneItemIdAsync(
        string sceneName,
        string sourceName,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(sceneName);
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        client.EnsureConnected();

        try
        {
            GetSceneItemIdResponseData? response = await client
                .SceneItems.GetSceneItemIdAsync(
                    new GetSceneItemIdRequestData(sourceName: sourceName, sceneName: sceneName),
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);

            return response?.SceneItemId;
        }
        catch (ObsWebSocketRequestException ex)
            when (ex.StatusCode is RequestStatusCode.ResourceNotFound)
        {
            // Item or scene not found, which is the expected 'failure' for a 'TryGet' pattern
            return null;
        }
        // Let other ObsWebSocketExceptions or different exception types propagate
    }

    /// <summary>
    /// Returns the scene item id for a source within a scene as an <see cref="int"/>, or
    /// <see langword="null"/> when the scene does not contain it.
    /// </summary>
    /// <param name="sceneName">The name of the scene to search.</param>
    /// <param name="sourceName">The name of the source to locate.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    [Obsolete(
        "FindSceneItemIdAsync now returns int?, so this variant is redundant. This forwarder will be removed in a future release."
    )]
    public Task<int?> FindSceneItemIdInt32Async(
        string sceneName,
        string sourceName,
        CancellationToken cancellationToken = default
    ) => FindSceneItemIdAsync(sceneName, sourceName, cancellationToken);
}
