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
/// Conveniences for the <c>Scenes</c> category, alongside its generated requests.
/// </summary>
public readonly partial struct ScenesGroup
{
    /// <summary>
    /// Switches the active Program or Preview scene, optionally setting a specific transition and duration beforehand.
    /// Does not restore the previously active transition.
    /// </summary>
    /// <param name="sceneName">The name of the scene to switch to.</param>
    /// <param name="transitionName">Optional: The name of the transition to use.</param>
    /// <param name="transitionDurationMs">Optional: The duration for the transition (in milliseconds). Requires transitionName to be set.</param>
    /// <param name="switchToProgram">If true (default), switches the Program scene. If false, switches the Preview scene (requires Studio Mode).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if OBS fails to perform any step (e.g., scene/transition not found).</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    private async Task SwitchSceneCoreAsync(
        string sceneName,
        string? transitionName = null,
        int? transitionDurationMs = null,
        bool switchToProgram = true,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(sceneName); // Throws if not connected
        client.EnsureConnected();

        // Set transition if specified
        if (!string.IsNullOrEmpty(transitionName))
        {
            await client
                .Transitions.SetCurrentSceneTransitionAsync(
                    new SetCurrentSceneTransitionRequestData(transitionName: transitionName),
                    cancellationToken
                )
                .ConfigureAwait(false);

            // Set duration only if transition was also set
            if (transitionDurationMs.HasValue)
            {
                await client
                    .Transitions.SetCurrentSceneTransitionDurationAsync(
                        new SetCurrentSceneTransitionDurationRequestData(
                            transitionDurationMs.Value
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
        }
        else if (transitionDurationMs.HasValue)
        {
            // Optionally log a warning if duration is set without transition name, as it might be ignored by OBS.
            // OBS behavior might vary here, but typically duration applies to the *current* transition.
            // For clarity, we only explicitly set duration if a transition name is also given.
            // Consider if setting duration alone should be allowed or throw an ArgumentException.
        }

        // Perform the scene switch
        if (switchToProgram)
        {
            await client
                .Scenes.SetCurrentProgramSceneAsync(
                    new SetCurrentProgramSceneRequestData(sceneName: sceneName),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        else
        {
            await client
                .Scenes.SetCurrentPreviewSceneAsync(
                    new SetCurrentPreviewSceneRequestData(sceneName: sceneName),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Switches the active Program or Preview scene using an optional transition,
    /// and waits for the corresponding scene change event before returning.
    /// </summary>
    /// <param name="sceneName">The name of the scene to switch to.</param>
    /// <param name="transitionName">Optional: The name of the transition to use. Applicable only when switching the Program scene.</param>
    /// <param name="transitionDurationMs">Optional: The duration for the transition (in milliseconds). Requires transitionName to be set. Applicable only when switching the Program scene.</param>
    /// <param name="switchToProgram">If true (default), switches the Program scene and waits for the scene change. If false, switches the Preview scene (requires Studio Mode) and waits for the preview scene change.</param>
    /// <param name="timeout">Optional: Maximum time to wait for the completion event after triggering the switch. Defaults based on client configuration.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if OBS fails to perform the switch or if the underlying wait fails unexpectedly.</exception>
    /// <exception cref="TimeoutException">Thrown if the expected event confirming the switch completion is not received within the timeout period.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected, or if trying to switch Preview scene when Studio Mode is disabled.</exception>
    /// <exception cref="OperationCanceledException">Thrown if the operation is canceled via the cancellationToken.</exception>
    private async Task SwitchSceneAndWaitCoreAsync(
        string sceneName,
        string? transitionName = null,
        int? transitionDurationMs = null,
        bool switchToProgram = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(sceneName);
        client.EnsureConnected(); // Ensure client is connected

        // Determine default timeout if not provided
        int baseWaitMs =
            transitionDurationMs.HasValue && transitionDurationMs > 0 && switchToProgram
                ? transitionDurationMs.Value + 2000 // Add a 2-second buffer if transition likely
                : client._options.Value.RequestTimeoutMs + 2000; // Or default request timeout + buffer
        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromMilliseconds(baseWaitMs);

        // --- Corrected Event Waiting Setup ---
        // We need separate task variables because Task<T> is not covariant.
        Task<CurrentProgramSceneChangedEventArgs>? programWaitTask = null;
        Task<CurrentPreviewSceneChangedEventArgs>? previewWaitTask = null;
        string eventDescription;

        if (switchToProgram)
        {
            eventDescription = $"CurrentProgramSceneChanged to '{sceneName}'";
            // Start the wait BEFORE triggering the action.
            programWaitTask = client.WaitForEventAsync<CurrentProgramSceneChangedEventArgs>(
                predicate: args => args.EventData.SceneName == sceneName,
                timeout: effectiveTimeout,
                cancellationToken: cancellationToken
            );
        }
        else
        {
            eventDescription = $"CurrentPreviewSceneChanged to '{sceneName}'";
            // Start the wait BEFORE triggering the action.
            previewWaitTask = client.WaitForEventAsync<CurrentPreviewSceneChangedEventArgs>(
                predicate: args => args.EventData.SceneName == sceneName,
                timeout: effectiveTimeout,
                cancellationToken: cancellationToken
            );
        }
        // ---------------------------------------

        try
        {
            // Trigger the scene switch using the non-waiting helper
            // This call happens *after* WaitForEventAsync has set up its subscription
            await SwitchSceneCoreAsync(
                    sceneName: sceneName,
                    transitionName: switchToProgram ? transitionName : null,
                    transitionDurationMs: switchToProgram ? transitionDurationMs : null,
                    switchToProgram: switchToProgram,
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);

            client._logger.LogDebug(
                "Switch triggered for '{SceneName}', waiting for {EventDescription}...",
                sceneName,
                eventDescription
            );

            if (programWaitTask is not null)
            {
                _ = await programWaitTask.ConfigureAwait(false);
            }
            else if (previewWaitTask is not null)
            {
                _ = await previewWaitTask.ConfigureAwait(false);
            }
            else
            {
                throw new InvalidOperationException("Internal error: No wait task was assigned.");
            }

            client._logger.LogInformation(
                "Successfully switched and confirmed {EventDescription}.",
                eventDescription
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            client._logger.LogInformation(
                "SwitchSceneAndWaitAsync operation was canceled externally for scene '{SceneName}'.",
                sceneName
            );
            throw; // Re-throw cancellation
        }
        catch (Exception ex)
        {
            client._logger.LogError(
                ex,
                "Error during SwitchSceneAndWaitAsync for scene '{SceneName}'.",
                sceneName
            );
            throw;
        }
        // The finally block within WaitForEventAsync handles unsubscribing the temporary event handler.
    }

    /// <summary>
    /// Checks whether a scene with the given name exists.
    /// </summary>
    /// <param name="sceneName">The scene name to look for.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task<bool> SceneExistsAsync(
        string sceneName,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(sceneName);
        client.EnsureConnected();

        GetSceneListResponseData? scenes = await client
            .Scenes.GetSceneListAsync(new(), cancellationToken)
            .ConfigureAwait(false);

        return scenes?.Scenes?.Any(s =>
                string.Equals(s.SceneName, sceneName, StringComparison.Ordinal)
            )
            ?? false;
    }

    /// <summary>Switches the Program scene.</summary>
    /// <param name="sceneName">The scene to switch to.</param>
    /// <param name="transitionName">Optional transition to use for this switch only.</param>
    /// <param name="transitionDurationMs">Optional transition duration for this switch only.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public Task SwitchProgramSceneAsync(
        string sceneName,
        string? transitionName = null,
        int? transitionDurationMs = null,
        CancellationToken cancellationToken = default
    ) =>
        SwitchSceneCoreAsync(
            sceneName,
            transitionName,
            transitionDurationMs,
            switchToProgram: true,
            cancellationToken
        );

    /// <summary>Switches the Preview scene. Requires Studio Mode.</summary>
    /// <param name="sceneName">The scene to switch to.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public Task SwitchPreviewSceneAsync(
        string sceneName,
        CancellationToken cancellationToken = default
    ) =>
        SwitchSceneCoreAsync(
            sceneName,
            switchToProgram: false,
            cancellationToken: cancellationToken
        );

    /// <summary>Switches the Program scene and waits for OBS to confirm it.</summary>
    /// <param name="sceneName">The scene to switch to.</param>
    /// <param name="timeout">How long to wait for confirmation.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="TimeoutException">Thrown if the confirmation does not arrive in time.</exception>
    public Task SwitchProgramSceneAndWaitAsync(
        string sceneName,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default
    ) =>
        SwitchSceneAndWaitCoreAsync(
            sceneName,
            switchToProgram: true,
            timeout: timeout,
            cancellationToken: cancellationToken
        );

    /// <summary>Switches the Preview scene and waits for OBS to confirm it. Requires Studio Mode.</summary>
    /// <param name="sceneName">The scene to switch to.</param>
    /// <param name="timeout">How long to wait for confirmation.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public Task SwitchPreviewSceneAndWaitAsync(
        string sceneName,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default
    ) =>
        SwitchSceneAndWaitCoreAsync(
            sceneName,
            switchToProgram: false,
            timeout: timeout,
            cancellationToken: cancellationToken
        );

    /// <summary>
    /// Switches the Program or Preview scene, depending on <paramref name="switchToProgram"/>.
    /// </summary>
    /// <param name="sceneName">The scene to switch to.</param>
    /// <param name="transitionName">Optional transition to use for this switch only.</param>
    /// <param name="transitionDurationMs">Optional transition duration for this switch only.</param>
    /// <param name="switchToProgram">Program when true, Preview when false.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    [Obsolete(
        "Call SwitchProgramSceneAsync or SwitchPreviewSceneAsync, which say which scene they switch. This forwarder will be removed in a future release."
    )]
    public Task SwitchSceneAsync(
        string sceneName,
        string? transitionName = null,
        int? transitionDurationMs = null,
        bool switchToProgram = true,
        CancellationToken cancellationToken = default
    ) =>
        SwitchSceneCoreAsync(
            sceneName,
            transitionName,
            transitionDurationMs,
            switchToProgram,
            cancellationToken
        );

    /// <summary>
    /// Switches the Program or Preview scene and waits for OBS to confirm it.
    /// </summary>
    /// <param name="sceneName">The scene to switch to.</param>
    /// <param name="transitionName">Optional transition to use for this switch only.</param>
    /// <param name="transitionDurationMs">Optional transition duration for this switch only.</param>
    /// <param name="switchToProgram">Program when true, Preview when false.</param>
    /// <param name="timeout">How long to wait for confirmation.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    [Obsolete(
        "Call SwitchProgramSceneAndWaitAsync or SwitchPreviewSceneAndWaitAsync, which say which scene they switch. This forwarder will be removed in a future release."
    )]
    public Task SwitchSceneAndWaitAsync(
        string sceneName,
        string? transitionName = null,
        int? transitionDurationMs = null,
        bool switchToProgram = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default
    ) =>
        SwitchSceneAndWaitCoreAsync(
            sceneName,
            transitionName,
            transitionDurationMs,
            switchToProgram,
            timeout,
            cancellationToken
        );
}
