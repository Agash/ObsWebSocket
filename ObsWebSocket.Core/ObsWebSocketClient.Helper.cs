using System.Text.Json;
using Microsoft.Extensions.Logging;
using ObsWebSocket.Core.Events;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Generated; // Assuming generated enums are here
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Core; // Or ObsWebSocket.Core.Extensions

/// <summary>
/// Provides helpful extension methods for common OBS WebSocket tasks.
/// </summary>
public static partial class ObsWebSocketClientHelpers
{
    private static readonly JsonSerializerOptions s_helperJsonOptions = new(
        JsonSerializerDefaults.Web
    );

    /// <summary>
    /// Switches the active Program or Preview scene, optionally setting a specific transition and duration beforehand.
    /// Does not restore the previously active transition.
    /// </summary>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="sceneName">The name of the scene to switch to.</param>
    /// <param name="transitionName">Optional: The name of the transition to use.</param>
    /// <param name="transitionDurationMs">Optional: The duration for the transition (in milliseconds). Requires transitionName to be set.</param>
    /// <param name="switchToProgram">If true (default), switches the Program scene. If false, switches the Preview scene (requires Studio Mode).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if OBS fails to perform any step (e.g., scene/transition not found).</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public static async Task SwitchSceneAsync(
        this ObsWebSocketClient client,
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
                .SetCurrentSceneTransitionAsync(
                    new SetCurrentSceneTransitionRequestData(transitionName),
                    cancellationToken
                )
                .ConfigureAwait(false);

            // Set duration only if transition was also set
            if (transitionDurationMs.HasValue)
            {
                await client
                    .SetCurrentSceneTransitionDurationAsync(
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
                .SetCurrentProgramSceneAsync(
                    new SetCurrentProgramSceneRequestData(sceneName),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        else
        {
            await client
                .SetCurrentPreviewSceneAsync(
                    new SetCurrentPreviewSceneRequestData(sceneName),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Switches the active Program or Preview scene using an optional transition,
    /// and waits for the corresponding scene change event before returning.
    /// </summary>
    /// <param name="client">The ObsWebSocketClient instance.</param>
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
    public static async Task SwitchSceneAndWaitAsync(
        this ObsWebSocketClient client,
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
        Task<CurrentProgramSceneChangedEventArgs?>? programWaitTask = null;
        Task<CurrentPreviewSceneChangedEventArgs?>? previewWaitTask = null;
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
            await client
                .SwitchSceneAsync(
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

            ObsEventArgs? eventArgs = null; // Use base type for the result variable

            if (programWaitTask != null)
            {
                // Await the specific task type
                eventArgs = await programWaitTask.ConfigureAwait(false);
            }
            else if (previewWaitTask != null)
            {
                // Await the specific task type
                eventArgs = await previewWaitTask.ConfigureAwait(false);
            }
            else
            {
                // Should not happen if logic is correct
                throw new InvalidOperationException("Internal error: No wait task was assigned.");
            }

            // Check the result (which is now correctly typed as ObsEventArgs?)
            if (eventArgs == null) // WaitForEventAsync returns null on timeout or cancellation
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    client._logger.LogInformation(
                        "SwitchSceneAndWaitAsync canceled while waiting for {EventDescription}.",
                        eventDescription
                    );
                    cancellationToken.ThrowIfCancellationRequested(); // Ensure exception propagates
                }
                else
                {
                    throw new TimeoutException(
                        $"Timed out ({effectiveTimeout.TotalSeconds}s) waiting for {eventDescription}."
                    );
                }
            }
            else
            {
                client._logger.LogInformation(
                    "Successfully switched and confirmed {EventDescription}.",
                    eventDescription
                );
            }
            // ------------------------------------------
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
    /// Sets the text content of a Text (GDI+, Freetype 2, Pango) source.
    /// </summary>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="inputName">The name of the Text source input.</param>
    /// <param name="text">The text content to set.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if OBS fails to set the text (e.g., input not found, not a text source).</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public static async Task SetInputTextAsync(
        this ObsWebSocketClient client,
        string inputName,
        string text,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(inputName);
        ArgumentNullException.ThrowIfNull(text); // Allow empty string, but not null
        client.EnsureConnected();

        try
        {
            // Create the minimal settings object OBS expects for text sources
            var settingsPayload = new { text };
            JsonElement settingsElement = JsonSerializer.SerializeToElement(
                settingsPayload,
                s_helperJsonOptions
            );

            await client
                .SetInputSettingsAsync(
                    new SetInputSettingsRequestData(
                        inputSettings: settingsElement,
                        inputName: inputName,
                        overlay: true // Merge with existing settings
                    ),
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (JsonException jsonEx)
        {
            // Should be unlikely for this simple object, but handle defensively
            throw new ObsWebSocketException(
                $"Failed to serialize text setting for '{inputName}'.",
                jsonEx
            );
        }
        // Let ObsWebSocketException from the underlying call propagate
    }

    /// <summary>
    /// Checks if an input or scene source with the given name exists in OBS.
    /// </summary>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="sourceName">The name of the input or scene to check.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>True if a source (input or scene) with the specified name exists, false otherwise.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if an unexpected error occurs during API calls.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public static async Task<bool> SourceExistsAsync(
        this ObsWebSocketClient client,
        string sourceName,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        client.EnsureConnected();

        try
        {
            // Check inputs first
            GetInputListResponseData? inputListResponse = await client
                .GetInputListAsync(
                    new GetInputListRequestData(),
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
            if (
                inputListResponse?.Inputs?.Any(i =>
                    string.Equals(i.InputName, sourceName, StringComparison.Ordinal)
                ) ?? false
            )
            {
                return true;
            }

            // Check scenes if not found in inputs
            ObsWebSocket.Core.Protocol.Responses.GetSceneListResponseData? sceneListResponse =
                await client
                    .GetSceneListAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            return sceneListResponse?.Scenes?.Any(s =>
                    string.Equals(s.SceneName, sourceName, StringComparison.Ordinal)
                ) ?? false;
        }
        catch (ObsWebSocketException ex)
        {
            // Log the specific OBS error but return false as the source effectively doesn't exist or couldn't be verified
            client._logger.LogWarning(
                ex,
                "OBS error while checking if source '{SourceName}' exists. Assuming it doesn't.",
                sourceName
            );
            return false;
        }
        // Let other exceptions (like InvalidOperationException for disconnect) propagate
    }

    // Helper #4 (SwitchSceneAndWaitAsync) - Deferred due to complexity without reflection

    /// <summary>
    /// Sets the mute state for multiple audio inputs using a single batch request.
    /// </summary>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="inputMutes">An enumerable of tuples, where each tuple contains the input name (string) and desired mute state (bool: true=muted, false=unmuted).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A Task representing the completion of the batch request submission. Inspect logs for individual item failures.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if the batch request itself fails (e.g., timeout).</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    /// <exception cref="ArgumentNullException">Thrown if inputMutes is null.</exception>
    public static async Task SetInputMutesAsync(
        this ObsWebSocketClient client,
        IEnumerable<(string InputName, bool IsMuted)> inputMutes,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(inputMutes);
        client.EnsureConnected();

        List<BatchRequestItem> batchItems =
        [
            .. inputMutes.Select(im => new BatchRequestItem(
                RequestType: "SetInputMute",
                RequestData: new SetInputMuteRequestData(
                    inputName: im.InputName,
                    inputMuted: im.IsMuted
                )
            )),
        ];

        if (batchItems.Count == 0)
        {
            client._logger.LogDebug("SetInputMutesAsync called with empty list, nothing to do.");
            return; // Nothing to send
        }

        // Send batch, don't halt on failure
        List<RequestResponsePayload<object>> results = await client
            .CallBatchAsync(
                requests: batchItems,
                haltOnFailure: false,
                executionType: RequestBatchExecutionType.SerialRealtime, // Appropriate for simple state changes
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);

        // Optional: Log failures from results
        foreach (RequestResponsePayload<object> result in results)
        {
            if (!result.RequestStatus.Result)
            {
                // Attempt to find original input name (requires parsing RequestId or matching RequestData - complex)
                // For now, log the failed request type and ID
                client._logger.LogWarning(
                    "Failed batch item in SetInputMutesAsync: RequestType={ReqType}, RequestId={ReqId}, Code={Code}, Comment={Comment}",
                    result.RequestType,
                    result.RequestId,
                    result.RequestStatus.Code,
                    result.RequestStatus.Comment ?? "N/A"
                );
            }
        }
    }

    /// <summary>
    /// Sets or toggles the enabled (visibility) state of a scene item, identified by its numeric ID.
    /// </summary>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="sceneName">The name of the scene containing the item.</param>
    /// <param name="sceneItemId">The numeric ID of the scene item.</param>
    /// <param name="isEnabled">The desired state (true=enabled, false=disabled). If null, the state will be toggled.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The final enabled state of the scene item after the operation.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if OBS fails the operation (e.g., scene/item not found).</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public static async Task<bool> SetSceneItemEnabledAsync(
        this ObsWebSocketClient client,
        string sceneName,
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
                    .GetSceneItemEnabledAsync(
                        new GetSceneItemEnabledRequestData(sceneItemId, sceneName),
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false)
                ?? throw new ObsWebSocketException(
                    $"Failed to get current enabled state for item ID {sceneItemId} in scene '{sceneName}'."
                );
            targetState = !currentStateResponse.SceneItemEnabled;
        }

        await client
            .SetSceneItemEnabledAsync(
                new SetSceneItemEnabledRequestData(sceneItemId, targetState, sceneName),
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);

        return targetState;
    }

    /// <summary>
    /// Sets or toggles the enabled (visibility) state of a scene item, identified by its source name within a scene.
    /// </summary>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="sceneName">The name of the scene containing the item.</param>
    /// <param name="sourceName">The name of the source corresponding to the scene item.</param>
    /// <param name="isEnabled">The desired state (true=enabled, false=disabled). If null, the state will be toggled.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The final enabled state of the scene item after the operation.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if OBS fails the operation (e.g., scene/item not found).</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    /// <exception cref="SceneItemNotFoundException">Thrown if the source name is not found within the specified scene.</exception>
    public static async Task<bool> SetSceneItemEnabledAsync(
        this ObsWebSocketClient client,
        string sceneName,
        string sourceName,
        bool? isEnabled = null, // If null, toggles; otherwise sets to the specified state
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(sceneName);
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        client.EnsureConnected();

        double? sceneItemId = await client
            .TryGetSceneItemIdAsync(sceneName, sourceName, cancellationToken)
            .ConfigureAwait(false);

        return sceneItemId.HasValue
            ? await client
                .SetSceneItemEnabledAsync(
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
    /// Attempts to get the numeric ID of a scene item within a specific scene.
    /// Returns null if the scene or source is not found.
    /// </summary>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="sceneName">The name of the scene to search within.</param>
    /// <param name="sourceName">The name of the source corresponding to the scene item.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A Task resulting in the nullable scene item ID (double?). Returns null if the item or scene is not found.</returns>
    /// <exception cref="ObsWebSocketException">Thrown for OBS errors other than 'ResourceNotFound'.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public static async Task<double?> TryGetSceneItemIdAsync(
        this ObsWebSocketClient client,
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
                .GetSceneItemIdAsync(
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
    /// Retrieves the settings for a specific filter on a source and deserializes them into type T.
    /// </summary>
    /// <typeparam name="T">The C# type (class or record) to deserialize the settings into.</typeparam>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="sourceName">The name of the source.</param>
    /// <param name="filterName">The name of the filter.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The deserialized settings object of type T, or null if the source/filter is not found or deserialization fails.</returns>
    /// <exception cref="ObsWebSocketException">Thrown for OBS errors other than 'ResourceNotFound'.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public static async Task<T?> GetSourceFilterSettingsAsync<T>(
        this ObsWebSocketClient client,
        string sourceName,
        string filterName,
        CancellationToken cancellationToken = default
    )
        where T : class // Class constraint helps with null return
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        ArgumentException.ThrowIfNullOrEmpty(filterName);
        client.EnsureConnected();

        GetSourceFilterResponseData? filterInfo;
        try
        {
            filterInfo = await client
                .GetSourceFilterAsync(
                    new GetSourceFilterRequestData(sourceName: sourceName, filterName: filterName),
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (ObsWebSocketException ex)
            when (ex.Message.Contains("NotFound", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains(
                    $"code {(int)Core.Protocol.Generated.RequestStatus.ResourceNotFound}:",
                    StringComparison.Ordinal
                )
            )
        {
            // Source or filter not found
            return null;
        }
        // Let other exceptions propagate

        if (filterInfo?.FilterSettings == null)
        {
            // Filter exists but has no settings data (unlikely for most filters, but possible)
            return null;
        }

        try
        {
            return filterInfo.FilterSettings.Value.Deserialize<T>(s_helperJsonOptions);
        }
        catch (JsonException jsonEx)
        {
            client._logger.LogError(
                jsonEx,
                "Failed to deserialize filter settings for '{FilterName}' on '{SourceName}' to type {TypeName}. Raw JSON: {RawJson}",
                filterName,
                sourceName,
                typeof(T).Name,
                filterInfo.FilterSettings.Value.GetRawText()
            );
            return null;
        }
        catch (NotSupportedException nse) // Can happen with complex types or configuration issues
        {
            client._logger.LogError(
                nse,
                "Deserialization not supported for filter settings for '{FilterName}' on '{SourceName}' to type {TypeName}.",
                filterName,
                sourceName,
                typeof(T).Name
            );
            return null;
        }
    }

    /// <summary>
    /// Sets the settings for a specific filter on a source using a strongly-typed settings object.
    /// </summary>
    /// <typeparam name="T">The C# type (class or record) representing the filter settings.</typeparam>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="sourceName">The name of the source.</param>
    /// <param name="filterName">The name of the filter.</param>
    /// <param name="settings">The settings object to apply.</param>
    /// <param name="overlay">True (default) to merge settings, false to replace all settings with defaults first, then apply.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if OBS fails the operation (e.g., source/filter not found) or if serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    /// <exception cref="ArgumentNullException">Thrown if settings object is null.</exception>
    public static async Task SetSourceFilterSettingsAsync<T>(
        this ObsWebSocketClient client,
        string sourceName,
        string filterName,
        T settings,
        bool overlay = true, // Defaults to merging settings
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        ArgumentException.ThrowIfNullOrEmpty(filterName);
        ArgumentNullException.ThrowIfNull(settings);
        client.EnsureConnected();

        JsonElement settingsElement;
        try
        {
            settingsElement = JsonSerializer.SerializeToElement(settings, s_helperJsonOptions);
        }
        catch (JsonException jsonEx)
        {
            throw new ObsWebSocketException(
                $"Failed to serialize settings object of type '{typeof(T).Name}' for filter '{filterName}'.",
                jsonEx
            );
        }
        catch (NotSupportedException nse)
        {
            throw new ObsWebSocketException(
                $"Failed to serialize settings object of type '{typeof(T).Name}' for filter '{filterName}'. Check type compatibility.",
                nse
            );
        }

        await client
            .SetSourceFilterSettingsAsync(
                new SetSourceFilterSettingsRequestData(
                    filterSettings: settingsElement,
                    sourceName: sourceName,
                    filterName: filterName,
                    overlay: overlay
                ),
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Triggers an OBS hotkey by its canonical name (e.g., "OBSWebSocket.StartStream").
    /// </summary>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="hotkeyName">The canonical name of the hotkey.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if OBS fails to trigger the hotkey (e.g., hotkey not found).</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public static async Task TriggerHotkeyAsync(
        this ObsWebSocketClient client,
        string hotkeyName,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(hotkeyName);
        client.EnsureConnected();

        await client
            .TriggerHotkeyByNameAsync(
                new TriggerHotkeyByNameRequestData(hotkeyName: hotkeyName), // contextName defaults to null/Any
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a screenshot of a source and returns it as a byte array.
    /// </summary>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="sourceName">The name of the source (input or scene).</param>
    /// <param name="imageFormat">The desired image format (e.g., "png", "jpg", "bmp"). Use GetVersion for supported formats.</param>
    /// <param name="width">Optional width to scale the screenshot to.</param>
    /// <param name="height">Optional height to scale the screenshot to.</param>
    /// <param name="compressionQuality">Optional compression quality (0-100 for formats like jpg, -1 for default).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A byte array containing the image data, or null if the source was not found or an error occurred.</returns>
    /// <exception cref="ObsWebSocketException">Thrown for OBS errors other than 'ResourceNotFound' or Base64 decoding errors.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public static async Task<byte[]?> GetSourceScreenshotBytesAsync(
        this ObsWebSocketClient client,
        string sourceName,
        string imageFormat = "png", // Common default
        int? width = null,
        int? height = null,
        int? compressionQuality = -1, // Use -1 for OBS default quality
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        ArgumentException.ThrowIfNullOrEmpty(imageFormat);
        client.EnsureConnected();

        GetSourceScreenshotResponseData? response;
        try
        {
            response = await client
                .GetSourceScreenshotAsync(
                    new GetSourceScreenshotRequestData(
                        sourceName: sourceName,
                        imageFormat: imageFormat,
                        imageWidth: width,
                        imageHeight: height,
                        imageCompressionQuality: compressionQuality
                    ),
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (ObsWebSocketException ex)
            when (ex.Message.Contains("NotFound", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains(
                    $"code {(int)Core.Protocol.Generated.RequestStatus.ResourceNotFound}:",
                    StringComparison.Ordinal
                )
            )
        {
            client._logger.LogWarning(
                "Source '{SourceName}' not found for screenshot.",
                sourceName
            );
            return null;
        }
        // Let other exceptions propagate

        if (string.IsNullOrEmpty(response?.ImageData))
        {
            client._logger.LogWarning(
                "Received null or empty image data for screenshot of '{SourceName}'.",
                sourceName
            );
            return null;
        }

        try
        {
            return Convert.FromBase64String(response.ImageData);
        }
        catch (FormatException formatEx)
        {
            client._logger.LogError(
                formatEx,
                "Failed to decode Base64 image data for screenshot of '{SourceName}'.",
                sourceName
            );
            // Wrap in ObsWebSocketException? Or just return null? Returning null seems reasonable for a helper.
            return null;
        }
    }

    /// <summary>
    /// Ensures the specified Scene Collection is currently active. If not, attempts to switch to it.
    /// </summary>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="targetSceneCollectionName">The name of the desired scene collection.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>True if the target scene collection is active after the call; false if the switch failed (e.g., not found).</returns>
    /// <exception cref="ObsWebSocketException">Thrown for unexpected OBS errors during the process.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public static async Task<bool> EnsureSceneCollectionActiveAsync(
        this ObsWebSocketClient client,
        string targetSceneCollectionName,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(targetSceneCollectionName);
        client.EnsureConnected();

        GetSceneCollectionListResponseData? currentResponse = await client
            .GetSceneCollectionListAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (
            string.Equals(
                currentResponse?.CurrentSceneCollectionName,
                targetSceneCollectionName,
                StringComparison.Ordinal
            )
        )
        {
            return true; // Already active
        }

        // Need to switch
        try
        {
            await client
                .SetCurrentSceneCollectionAsync(
                    new SetCurrentSceneCollectionRequestData(targetSceneCollectionName),
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
            return true; // Switch command sent successfully
        }
        catch (ObsWebSocketException ex)
            when (ex.Message.Contains("NotFound", StringComparison.OrdinalIgnoreCase)
                || // General not found
                ex.Message.Contains(
                    $"code {(int)Core.Protocol.Generated.RequestStatus.ResourceNotFound}:",
                    StringComparison.Ordinal
                )
                || // Specific code
                ex.Message.Contains("InvalidParameter", StringComparison.OrdinalIgnoreCase) // Might be InvalidParameter if name doesn't exist
            )
        {
            client._logger.LogWarning(
                "Failed to set scene collection to '{TargetName}': Not found or invalid.",
                targetSceneCollectionName
            );
            return false; // Switch failed because target doesn't exist
        }
        // Let other exceptions propagate
    }

    /// <summary>
    /// Ensures the specified Profile is currently active. If not, attempts to switch to it.
    /// </summary>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="targetProfileName">The name of the desired profile.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>True if the target profile is active after the call; false if the switch failed (e.g., not found).</returns>
    /// <exception cref="ObsWebSocketException">Thrown for unexpected OBS errors during the process.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public static async Task<bool> EnsureProfileActiveAsync(
        this ObsWebSocketClient client,
        string targetProfileName,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(targetProfileName);
        client.EnsureConnected();

        GetProfileListResponseData? currentResponse = await client
            .GetProfileListAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (
            string.Equals(
                currentResponse?.CurrentProfileName,
                targetProfileName,
                StringComparison.Ordinal
            )
        )
        {
            return true; // Already active
        }

        // Need to switch
        try
        {
            await client
                .SetCurrentProfileAsync(
                    new SetCurrentProfileRequestData(targetProfileName),
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
            return true; // Switch command sent successfully
        }
        catch (ObsWebSocketException ex)
            when (ex.Message.Contains("NotFound", StringComparison.OrdinalIgnoreCase)
                || // General not found
                ex.Message.Contains(
                    $"code {(int)Core.Protocol.Generated.RequestStatus.ResourceNotFound}:",
                    StringComparison.Ordinal
                )
                || // Specific code
                ex.Message.Contains("InvalidParameter", StringComparison.OrdinalIgnoreCase) // Might be InvalidParameter if name doesn't exist
            )
        {
            client._logger.LogWarning(
                "Failed to set profile to '{TargetName}': Not found or invalid.",
                targetProfileName
            );
            return false; // Switch failed because target doesn't exist
        }
        // Let other exceptions propagate
    }

    // Helper #14 (WaitForEventAsync<TEventArgs>) - Deferred due to complexity/reflection constraints.

    // Internal Helper for SetSceneItemEnabledAsync (Source Name overload)
    /// <summary>Exception thrown when a scene item cannot be found by name within a scene.</summary>
    [Serializable]
    public class SceneItemNotFoundException : ObsWebSocketException
    {
        /// <summary>
        /// The name of the scene where the item was expected to be found.
        /// </summary>
        public string? SceneName { get; }

        /// <summary>
        /// The name of the source that was expected to be found within the scene.
        /// </summary>
        public string? SourceName { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SceneItemNotFoundException"/> class with a specified error message.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="sceneName"></param>
        /// <param name="sourceName"></param>
        public SceneItemNotFoundException(
            string message,
            string? sceneName = null,
            string? sourceName = null
        )
            : base(message)
        {
            SceneName = sceneName;
            SourceName = sourceName;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SceneItemNotFoundException"/> class with a specified error message and inner exception.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="innerException"></param>
        /// <param name="sceneName"></param>
        /// <param name="sourceName"></param>
        public SceneItemNotFoundException(
            string message,
            Exception? innerException,
            string? sceneName = null,
            string? sourceName = null
        )
            : base(message, innerException)
        {
            SceneName = sceneName;
            SourceName = sourceName;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SceneItemNotFoundException"/> class with no message or inner exception.
        /// </summary>
        public SceneItemNotFoundException() { }

        /// <summary>
        /// Initializes a new instance of the <see cref="SceneItemNotFoundException"/> class with a specified error message.
        /// </summary>
        /// <param name="message"></param>
        public SceneItemNotFoundException(string message)
            : base(message) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="SceneItemNotFoundException"/> class with a specified error message and inner exception.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="inner"></param>
        public SceneItemNotFoundException(string message, Exception inner)
            : base(message, inner) { }
    }
}
