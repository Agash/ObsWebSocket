using ObsWebSocket.Core.Events;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Core;

/// <summary>
/// Convenience helpers that round out the surface established by the original helper set:
/// output control that waits for confirmation, existence checks, unambiguous volume setters,
/// and media transport shorthands.
/// </summary>
public static class ObsWebSocketClientConvenienceExtensions
{
    private static readonly TimeSpan s_defaultOutputTimeout = TimeSpan.FromSeconds(10);

    // ────────────────────────────────────────────────────────────────────────
    // Output control, mirroring SetVirtualCamActiveAndWaitAsync
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts or stops recording and waits for OBS to confirm the state change.
    /// </summary>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="activate"><see langword="true"/> to start recording; <see langword="false"/> to stop it.</param>
    /// <param name="timeout">Maximum time to wait for the state-change event. Defaults to 10 seconds.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The state reported by the event, or <see langword="null"/> if the timeout elapsed first.
    /// </returns>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public static async Task<OutputState?> SetRecordActiveAndWaitAsync(
        this ObsWebSocketClient client,
        bool activate,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        client.EnsureConnected();

        // Set up the wait before issuing the command to avoid missing the event.
        Task<RecordStateChangedEventArgs?> waitTask = client.WaitForEventAsync<RecordStateChangedEventArgs>(
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

        RecordStateChangedEventArgs? ev = await waitTask.ConfigureAwait(false);
        return ev is null ? null : OutputStateExtensions.FromWireValue(ev.EventData.OutputState);
    }

    /// <summary>
    /// Starts or stops streaming and waits for OBS to confirm the state change.
    /// </summary>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="activate"><see langword="true"/> to start streaming; <see langword="false"/> to stop it.</param>
    /// <param name="timeout">Maximum time to wait for the state-change event. Defaults to 10 seconds.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The state reported by the event, or <see langword="null"/> if the timeout elapsed first.
    /// </returns>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public static async Task<OutputState?> SetStreamActiveAndWaitAsync(
        this ObsWebSocketClient client,
        bool activate,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        client.EnsureConnected();

        Task<StreamStateChangedEventArgs?> waitTask = client.WaitForEventAsync<StreamStateChangedEventArgs>(
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

        StreamStateChangedEventArgs? ev = await waitTask.ConfigureAwait(false);
        return ev is null ? null : OutputStateExtensions.FromWireValue(ev.EventData.OutputState);
    }

    /// <summary>
    /// Returns whether recording is currently active.
    /// </summary>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public static async Task<bool> IsRecordActiveAsync(
        this ObsWebSocketClient client,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        client.EnsureConnected();
        GetRecordStatusResponseData? status = await client
            .GetRecordStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        return status?.OutputActive ?? false;
    }

    /// <summary>
    /// Returns whether streaming is currently active.
    /// </summary>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public static async Task<bool> IsStreamActiveAsync(
        this ObsWebSocketClient client,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        client.EnsureConnected();
        GetStreamStatusResponseData? status = await client
            .GetStreamStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        return status?.OutputActive ?? false;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Existence checks, mirroring SourceExistsAsync
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks whether a scene with the given name exists.
    /// </summary>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="sceneName">The scene name to look for.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public static async Task<bool> SceneExistsAsync(
        this ObsWebSocketClient client,
        string sceneName,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(client);
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
    // Volume, where the request record accepts either unit and neither is required
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets an input's volume in decibels. OBS accepts either decibels or a multiplier on the
    /// same request and rejects it when neither is present, so these helpers pick one for you.
    /// </summary>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="inputName">The name of the input.</param>
    /// <param name="volumeDb">The desired volume in dB. OBS accepts -100 through 26.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if OBS rejects the request.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public static async Task SetInputVolumeDbAsync(
        this ObsWebSocketClient client,
        string inputName,
        double volumeDb,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(client);
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
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="inputName">The name of the input.</param>
    /// <param name="volumeMul">The desired volume multiplier. OBS accepts 0 through 20.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if OBS rejects the request.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public static async Task SetInputVolumeMulAsync(
        this ObsWebSocketClient client,
        string inputName,
        double volumeMul,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(client);
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
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="inputName">The name of the media input.</param>
    /// <param name="action">The transport action to perform.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if OBS rejects the request.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public static async Task TriggerMediaActionAsync(
        this ObsWebSocketClient client,
        string inputName,
        MediaInputAction action,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(client);
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
    public static Task PlayMediaAsync(
        this ObsWebSocketClient client,
        string inputName,
        CancellationToken cancellationToken = default
    ) => client.TriggerMediaActionAsync(inputName, MediaInputAction.Play, cancellationToken);

    /// <summary>Pauses a media input.</summary>
    public static Task PauseMediaAsync(
        this ObsWebSocketClient client,
        string inputName,
        CancellationToken cancellationToken = default
    ) => client.TriggerMediaActionAsync(inputName, MediaInputAction.Pause, cancellationToken);

    /// <summary>Stops a media input.</summary>
    public static Task StopMediaAsync(
        this ObsWebSocketClient client,
        string inputName,
        CancellationToken cancellationToken = default
    ) => client.TriggerMediaActionAsync(inputName, MediaInputAction.Stop, cancellationToken);

    /// <summary>Restarts a media input from the beginning.</summary>
    public static Task RestartMediaAsync(
        this ObsWebSocketClient client,
        string inputName,
        CancellationToken cancellationToken = default
    ) => client.TriggerMediaActionAsync(inputName, MediaInputAction.Restart, cancellationToken);

    // ────────────────────────────────────────────────────────────────────────
    // WaitForEventAsync overloads for the common cases
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Waits for the next occurrence of a typed OBS event, with no timeout. The wait ends only
    /// when the event arrives or <paramref name="cancellationToken"/> fires.
    /// </summary>
    /// <typeparam name="TEventArgs">The event args type to wait for.</typeparam>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    /// <returns>The event args, or <see langword="null"/> if the wait was canceled.</returns>
    public static Task<TEventArgs?> WaitForEventAsync<TEventArgs>(
        this ObsWebSocketClient client,
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
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="timeout">How long to wait before giving up.</param>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    /// <returns>The event args, or <see langword="null"/> on timeout or cancellation.</returns>
    public static Task<TEventArgs?> WaitForEventAsync<TEventArgs>(
        this ObsWebSocketClient client,
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    )
        where TEventArgs : ObsEventArgs =>
        client.WaitForEventAsync<TEventArgs>(static _ => true, timeout, cancellationToken);

    /// <summary>
    /// Waits for the next matching occurrence of a typed OBS event, with no timeout.
    /// </summary>
    /// <typeparam name="TEventArgs">The event args type to wait for.</typeparam>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="predicate">Returns <see langword="true"/> for the event to stop on.</param>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    /// <returns>The matching event args, or <see langword="null"/> if the wait was canceled.</returns>
    public static Task<TEventArgs?> WaitForEventAsync<TEventArgs>(
        this ObsWebSocketClient client,
        Func<TEventArgs, bool> predicate,
        CancellationToken cancellationToken = default
    )
        where TEventArgs : ObsEventArgs =>
        client.WaitForEventAsync(predicate, Timeout.InfiniteTimeSpan, cancellationToken);

    // ────────────────────────────────────────────────────────────────────────
    // Typed batch execution
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds and sends a batch using the typed builder, so each request type is paired with
    /// its own data record instead of a loose string and object.
    /// </summary>
    /// <param name="client">The ObsWebSocketClient instance.</param>
    /// <param name="build">Adds the requests to send.</param>
    /// <param name="executionType">How OBS should schedule the requests.</param>
    /// <param name="haltOnFailure">Whether OBS should stop at the first failing request.</param>
    /// <param name="timeoutMs">Optional override for the request timeout.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>One result per request, in order.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public static Task<List<RequestResponsePayload<object>>> CallBatchAsync(
        this ObsWebSocketClient client,
        Action<ObsBatchBuilder> build,
        RequestBatchExecutionType? executionType = null,
        bool? haltOnFailure = null,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(client);
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
}
