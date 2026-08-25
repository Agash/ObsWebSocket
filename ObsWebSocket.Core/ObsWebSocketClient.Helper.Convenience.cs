using ObsWebSocket.Core.Events;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Core;

/// <summary>
/// Output control, existence checks, volume, media transport, and event-wait conveniences.
/// </summary>
public static class ObsWebSocketClientConvenienceExtensions
{
    private static readonly TimeSpan s_defaultOutputTimeout = TimeSpan.FromSeconds(10);

    extension(ObsWebSocketClient client)
    {

        // ────────────────────────────────────────────────────────────────────────
        // Output control
        // ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Starts or stops recording and waits for OBS to confirm the state change.
        /// </summary>
        /// <param name="activate"><see langword="true"/> to start recording; <see langword="false"/> to stop it.</param>
        /// <param name="timeout">Maximum time to wait for the state-change event. Defaults to 10 seconds.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>
        /// The state reported by the event, or <see langword="null"/> if the timeout elapsed first.
        /// </returns>
        /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
        public async Task<OutputState?> SetRecordActiveAndWaitAsync(
            bool activate,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default
        )
        {
            client.EnsureConnected();

            // Set up the wait before issuing the command to avoid missing the event.
            Task<RecordStateChangedEventArgs> waitTask = client.WaitForEventAsync<RecordStateChangedEventArgs>(
                predicate: _ => true,
                timeout: timeout ?? s_defaultOutputTimeout,
                cancellationToken: cancellationToken
            );

            if (activate)
            {
                await client.StartRecordAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _ = await client.StopRecordAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                RecordStateChangedEventArgs ev = await waitTask.ConfigureAwait(false);
                return OutputStateExtensions.FromWireValue(ev.EventData.OutputState);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        /// <summary>
        /// Starts or stops streaming and waits for OBS to confirm the state change.
        /// </summary>
        /// <param name="activate"><see langword="true"/> to start streaming; <see langword="false"/> to stop it.</param>
        /// <param name="timeout">Maximum time to wait for the state-change event. Defaults to 10 seconds.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>
        /// The state reported by the event, or <see langword="null"/> if the timeout elapsed first.
        /// </returns>
        /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
        public async Task<OutputState?> SetStreamActiveAndWaitAsync(
            bool activate,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default
        )
        {
            client.EnsureConnected();

            Task<StreamStateChangedEventArgs> waitTask = client.WaitForEventAsync<StreamStateChangedEventArgs>(
                predicate: _ => true,
                timeout: timeout ?? s_defaultOutputTimeout,
                cancellationToken: cancellationToken
            );

            if (activate)
            {
                await client.StartStreamAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.StopStreamAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                StreamStateChangedEventArgs ev = await waitTask.ConfigureAwait(false);
                return OutputStateExtensions.FromWireValue(ev.EventData.OutputState);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        /// <summary>
        /// Returns whether recording is currently active.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
        public async Task<bool> IsRecordActiveAsync(
            CancellationToken cancellationToken = default
        )
        {
            client.EnsureConnected();
            GetRecordStatusResponseData? status = await client
                .GetRecordStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            return status?.OutputActive ?? false;
        }

        /// <summary>
        /// Returns whether streaming is currently active.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
        public async Task<bool> IsStreamActiveAsync(
            CancellationToken cancellationToken = default
        )
        {
            client.EnsureConnected();
            GetStreamStatusResponseData? status = await client
                .GetStreamStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            return status?.OutputActive ?? false;
        }

        // ────────────────────────────────────────────────────────────────────────
        // Existence checks
        // ────────────────────────────────────────────────────────────────────────

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
                .GetSceneListAsync(new GetSceneListRequestData(), cancellationToken)
                .ConfigureAwait(false);

            return scenes?.Scenes?.Any(s =>
                string.Equals(s.SceneName, sceneName, StringComparison.Ordinal)
            ) ?? false;
        }

        // ────────────────────────────────────────────────────────────────────────
        // Volume
        // ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Sets an input's volume in decibels. The underlying request accepts either decibels or
        /// a multiplier and fails when given neither.
        /// </summary>
        /// <param name="inputName">The name of the input.</param>
        /// <param name="volumeDb">The desired volume in dB. OBS accepts -100 through 26.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <exception cref="ObsWebSocketException">Thrown if OBS rejects the request.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
        public async Task SetInputVolumeDbAsync(
            string inputName,
            double volumeDb,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentException.ThrowIfNullOrEmpty(inputName);
            client.EnsureConnected();

            await client
                .SetInputVolumeAsync(
                    new SetInputVolumeRequestData { InputName = inputName, InputVolumeDb = volumeDb },
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Sets an input's volume as a linear multiplier, where <c>1.0</c> is unity gain.
        /// </summary>
        /// <param name="inputName">The name of the input.</param>
        /// <param name="volumeMul">The desired volume multiplier. OBS accepts 0 through 20.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <exception cref="ObsWebSocketException">Thrown if OBS rejects the request.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
        public async Task SetInputVolumeMulAsync(
            string inputName,
            double volumeMul,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentException.ThrowIfNullOrEmpty(inputName);
            client.EnsureConnected();

            await client
                .SetInputVolumeAsync(
                    new SetInputVolumeRequestData { InputName = inputName, InputVolumeMul = volumeMul },
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        // ────────────────────────────────────────────────────────────────────────
        // Media transport
        // ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Triggers a media action on an input using the typed <see cref="MediaInputAction"/> enum
        /// rather than a protocol string constant.
        /// </summary>
        /// <param name="inputName">The name of the media input.</param>
        /// <param name="action">The transport action to perform.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <exception cref="ObsWebSocketException">Thrown if OBS rejects the request.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
        public async Task TriggerMediaActionAsync(
            string inputName,
            MediaInputAction action,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentException.ThrowIfNullOrEmpty(inputName);
            client.EnsureConnected();

            await client
                .TriggerMediaInputActionAsync(
                    new TriggerMediaInputActionRequestData
                    {
                        InputName = inputName,
                        MediaAction = action.ToWireValue(),
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        /// <summary>Plays a media input.</summary>
        public Task PlayMediaAsync(
            string inputName,
            CancellationToken cancellationToken = default
        ) => client.TriggerMediaActionAsync(inputName, MediaInputAction.Play, cancellationToken);

        /// <summary>Pauses a media input.</summary>
        public Task PauseMediaAsync(
            string inputName,
            CancellationToken cancellationToken = default
        ) => client.TriggerMediaActionAsync(inputName, MediaInputAction.Pause, cancellationToken);

        /// <summary>Stops a media input.</summary>
        public Task StopMediaAsync(
            string inputName,
            CancellationToken cancellationToken = default
        ) => client.TriggerMediaActionAsync(inputName, MediaInputAction.Stop, cancellationToken);

        /// <summary>Restarts a media input from the beginning.</summary>
        public Task RestartMediaAsync(
            string inputName,
            CancellationToken cancellationToken = default
        ) => client.TriggerMediaActionAsync(inputName, MediaInputAction.Restart, cancellationToken);

        // ────────────────────────────────────────────────────────────────────────
        // WaitForEventAsync overloads
        // ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Waits for the next occurrence of a typed OBS event, with no timeout. The wait ends only
        /// when the event arrives or <paramref name="cancellationToken"/> fires.
        /// </summary>
        /// <typeparam name="TEventArgs">The event args type to wait for.</typeparam>
        /// <param name="cancellationToken">A token to cancel the wait.</param>
        /// <returns>The event args.</returns>
        public Task<TEventArgs> WaitForEventAsync<TEventArgs>(
            CancellationToken cancellationToken = default
        )
            where TEventArgs : ObsEventArgs =>
            client.WaitForEventAsync<TEventArgs>(
                static _ => true,
                Timeout.InfiniteTimeSpan,
                cancellationToken
            );

        /// <summary>
        /// Waits for the next occurrence of a typed OBS event, giving up after <paramref name="timeout"/>.
        /// </summary>
        /// <typeparam name="TEventArgs">The event args type to wait for.</typeparam>
        /// <param name="timeout">How long to wait before giving up.</param>
        /// <param name="cancellationToken">A token to cancel the wait.</param>
        /// <returns>The event args.</returns>
        /// <exception cref="TimeoutException">Thrown if the timeout elapses first.</exception>
        public Task<TEventArgs> WaitForEventAsync<TEventArgs>(
            TimeSpan timeout,
            CancellationToken cancellationToken = default
        )
            where TEventArgs : ObsEventArgs =>
            client.WaitForEventAsync<TEventArgs>(static _ => true, timeout, cancellationToken);

        /// <summary>
        /// Waits for the next matching occurrence of a typed OBS event, with no timeout.
        /// </summary>
        /// <typeparam name="TEventArgs">The event args type to wait for.</typeparam>
        /// <param name="predicate">Returns <see langword="true"/> for the event to stop on.</param>
        /// <param name="cancellationToken">A token to cancel the wait.</param>
        /// <returns>The matching event args.</returns>
        public Task<TEventArgs> WaitForEventAsync<TEventArgs>(
            Func<TEventArgs, bool> predicate,
            CancellationToken cancellationToken = default
        )
            where TEventArgs : ObsEventArgs =>
            client.WaitForEventAsync(predicate, Timeout.InfiniteTimeSpan, cancellationToken);

        // ────────────────────────────────────────────────────────────────────────
        // Typed batch execution
        // ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Sends a batch built with <see cref="ObsBatchBuilder"/>, returning results addressable
        /// by the references the builder handed out.
        /// </summary>
        /// <param name="batch">The batch to send.</param>
        /// <param name="executionType">How OBS should schedule the requests.</param>
        /// <param name="haltOnFailure">Whether OBS should stop at the first failing request.</param>
        /// <param name="timeoutMs">Optional override for the request timeout.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>The results, addressable by reference or by position.</returns>
        public async Task<BatchResults> CallBatchAsync(
            ObsBatchBuilder batch,
            RequestBatchExecutionType? executionType = null,
            bool? haltOnFailure = null,
            int? timeoutMs = null,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(batch);

            List<RequestResponsePayload<object>> results = await client
                .CallBatchAsync(
                    batch.Build(),
                    executionType,
                    haltOnFailure,
                    timeoutMs,
                    cancellationToken
                )
                .ConfigureAwait(false);

            // OBS mis-pairs result payloads under parallel execution.
            return new BatchResults(
                results,
                payloadsTrustworthy: executionType != RequestBatchExecutionType.Parallel
            );
        }

        /// <summary>
        /// Builds and sends a batch using the typed builder, so each request type is paired with
        /// its own data record instead of a loose string and object.
        /// </summary>
        /// <param name="build">Adds the requests to send.</param>
        /// <param name="executionType">How OBS should schedule the requests.</param>
        /// <param name="haltOnFailure">Whether OBS should stop at the first failing request.</param>
        /// <param name="timeoutMs">Optional override for the request timeout.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>One result per request, in order.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
        public Task<List<RequestResponsePayload<object>>> CallBatchAsync(
            Action<ObsBatchBuilder> build,
            RequestBatchExecutionType? executionType = null,
            bool? haltOnFailure = null,
            int? timeoutMs = null,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(build);

            ObsBatchBuilder builder = new();
            build(builder);
            return client.CallBatchAsync(
                builder.Build(),
                executionType,
                haltOnFailure,
                timeoutMs,
                cancellationToken
            );
        }

        // Scene item ids are Number on the wire, so the generated surface uses double.

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
            client.SetSceneItemEnabledAsync(
                sceneName,
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
                .FindSceneItemIdAsync(sceneName, sourceName, cancellationToken)
                .ConfigureAwait(false);
            return id is null ? null : checked((int)id.Value);
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
            client.SwitchSceneAsync(
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
            client.SwitchSceneAsync(
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
            client.SwitchSceneAndWaitAsync(
                sceneName,
                switchToProgram: true,
                timeout: timeout,
                cancellationToken: cancellationToken
            );
    }
}
