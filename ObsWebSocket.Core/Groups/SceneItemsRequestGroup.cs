using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ObsWebSocket.Core.Events;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Common;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Protocol.Common.FilterSettings;
using ObsWebSocket.Core.Protocol.Common.InputSettings;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Core;

/// <summary>
/// Conveniences for the <c>SceneItems</c> category, alongside its generated requests.
/// </summary>
public readonly partial struct SceneItemsRequestGroup
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
    public async Task<bool> SetSceneItemEnabledAsync(string sceneName,
        double sceneItemId, // Use double as sceneItemId is Number in protocol
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
                        new GetSceneItemEnabledRequestData(sceneItemId: sceneItemId, sceneName: sceneName),
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false)
                ?? throw new ObsWebSocketException(
                    $"Failed to get current enabled state for item ID {sceneItemId} in scene '{sceneName}'."
                );
            targetState = !currentStateResponse.SceneItemEnabled;
        }

        await client
            .SceneItems.SetSceneItemEnabledAsync(new SetSceneItemEnabledRequestData(
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
    public async Task<bool> SetSceneItemEnabledAsync(string sceneName,
        string sourceName,
        bool? isEnabled = null, // If null, toggles; otherwise sets to the specified state
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(sceneName);
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        client.EnsureConnected();

        double? sceneItemId = await client
            .SceneItems.FindSceneItemIdAsync(sceneName, sourceName, cancellationToken)
            .ConfigureAwait(false);

        return sceneItemId.HasValue
            ? await client
                .SceneItems.SetSceneItemEnabledAsync(sceneName,
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
    /// Attempts to get the numeric ID of a scene item within a specific scene.
    /// Returns null if the scene or source is not found.
    /// </summary>
    /// <param name="sceneName">The name of the scene to search within.</param>
    /// <param name="sourceName">The name of the source corresponding to the scene item.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A Task resulting in the nullable scene item ID (double?). Returns null if the item or scene is not found.</returns>
    /// <exception cref="ObsWebSocketException">Thrown for OBS errors other than 'ResourceNotFound'.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    [Obsolete("Renamed to FindSceneItemIdAsync. Async methods cannot use the out-parameter Try pattern, so the Try prefix was misleading. This forwarder will be removed in a future release.")]
    public Task<double?> TryGetSceneItemIdAsync(string sceneName,
        string sourceName,
        CancellationToken cancellationToken = default
    ) => client.SceneItems.FindSceneItemIdAsync(sceneName, sourceName, cancellationToken);

    /// <summary>
    /// Returns the scene item id for a source within a scene, or <see langword="null"/> when the
    /// scene does not contain it.
    /// </summary>
    /// <param name="sceneName">The name of the scene to search.</param>
    /// <param name="sourceName">The name of the source to locate.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The scene item id, or <see langword="null"/> if the source is not in the scene.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task<double?> FindSceneItemIdAsync(string sceneName,
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

            // If response is not null, return the ID. The underlying GetSceneItemIdAsync
            // should guarantee the response isn't null on success.
            return response?.SceneItemId;
        }
        catch (ObsWebSocketException ex)
            when (ex.Message.Contains("NotFound", StringComparison.OrdinalIgnoreCase)
                || // General not found
                ex.Message.Contains(
                    $"code {(int)Core.Protocol.Generated.RequestStatus.ResourceNotFound}:",
                    StringComparison.Ordinal
                ) // Specific code check
            )
        {
            // Item or scene not found, which is the expected 'failure' for a 'TryGet' pattern
            return null;
        }
        // Let other ObsWebSocketExceptions or different exception types propagate
    }

        /// <summary>
        /// Sets or toggles a scene item's enabled state using an integer item id.
        /// </summary>
        /// <param name="sceneName">The name of the scene containing the item.</param>
        /// <param name="sceneItemId">The numeric id of the scene item.</param>
        /// <param name="isEnabled">The desired state, or <see langword="null"/> to toggle.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>The resulting enabled state.</returns>
        public Task<bool> SetSceneItemEnabledAsync(
            string sceneName,
            int sceneItemId,
            bool? isEnabled = null,
            CancellationToken cancellationToken = default
        ) =>
            client.SceneItems.SetSceneItemEnabledAsync(sceneName,
                (double)sceneItemId,
                isEnabled,
                cancellationToken
            );

        /// <summary>
        /// Returns the scene item id for a source within a scene as an <see cref="int"/>, or
        /// <see langword="null"/> when the scene does not contain it.
        /// </summary>
        /// <param name="sceneName">The name of the scene to search.</param>
        /// <param name="sourceName">The name of the source to locate.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        public async Task<int?> FindSceneItemIdInt32Async(
            string sceneName,
            string sourceName,
            CancellationToken cancellationToken = default
        )
        {
            double? id = await client
                .SceneItems.FindSceneItemIdAsync(sceneName, sourceName, cancellationToken)
                .ConfigureAwait(false);
            return id is null ? null : checked((int)id.Value);
        }
}
