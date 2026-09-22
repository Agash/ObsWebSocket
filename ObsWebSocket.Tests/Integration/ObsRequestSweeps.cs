using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Events;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Common;
using ObsWebSocket.Core.Protocol.Common.FilterSettings;
using ObsWebSocket.Core.Protocol.Common.InputSettings;
using ObsWebSocket.Core.Protocol.Common.NestedTypes;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Tests.Integration;

/// <summary>
/// Sends every request the protocol defines against a live OBS and reports what each one did.
/// </summary>
/// <remarks>
/// Each sweep returns one entry per check rather than asserting, so a run reports every failure
/// instead of stopping at the first.
/// </remarks>
internal static class ObsRequestSweeps
{
    private const string FixtureFilterName = "__obsws_rsweep_filter";

    /// <summary>
    /// Validates all three settings API modes for both InputSettings and FilterSettings.
    /// </summary>
    /// <remarks>
    /// The browser source and the gain filter are created here and removed again, so a fresh OBS
    /// install exercises the same checks as a populated one. Discovering an existing input instead
    /// made the result depend on the machine: the run reported the modes as failing when all that
    /// was missing was a source to try them on.
    /// </remarks>
    internal static async Task<
        List<(string Label, bool Pass, string Detail)>
    > ValidateSettingsModesAsync(
        ObsWebSocketClient client,
        GetInputListResponseData? inputs,
        ObsSourceKinds kinds,
        CancellationToken cancellationToken
    )
    {
        List<(string Label, bool Pass, string Detail)> results = [];
        if (inputs is null)
        {
            results.Add(("Settings [all modes]", false, "GetInputList returned null"));
            return results;
        }

        if (kinds.Browser is null || kinds.GainFilter is null)
        {
            results.Add(
                ("Settings [all modes]", true, $"skipped: no {kinds.Describe()} kind on this OBS")
            );
            return results;
        }

        // ── InputSettings ─────────────────────────────────────────────────────
        const string FixtureInputName = "__obsws_settings_browser";
        const string FixtureFilter = "__obsws_settings_gain";

        string? browserInputName = null;
        string? filterSourceName = null;
        string? gainFilterName = null;

        try
        {
            GetCurrentProgramSceneResponseData programScene = await client
                .Scenes.GetCurrentProgramSceneAsync(cancellationToken)
                .ConfigureAwait(false);

            await client
                .Inputs.CreateInputAsync(
                    inputKind: kinds.Browser,
                    inputName: FixtureInputName,
                    settings: new BrowserSourceSettings(
                        Url: "https://obsproject.com",
                        Width: 800,
                        Height: 600
                    ),
                    sceneName: programScene.SceneName,
                    sceneItemEnabled: true,
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
            browserInputName = FixtureInputName;

            await client
                .Filters.CreateSourceFilterAsync(
                    new CreateSourceFilterRequestData(
                        filterKind: kinds.GainFilter,
                        filterName: FixtureFilter,
                        sourceName: FixtureInputName
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
            filterSourceName = FixtureInputName;
            gainFilterName = FixtureFilter;
        }
        catch (ObsWebSocketException ex)
        {
            results.Add(("Settings fixtures", false, $"could not be created: {ex.Message}"));
        }

        try
        {
            results.AddRange(
                await RunSettingsModeChecksAsync(
                        client,
                        browserInputName,
                        filterSourceName,
                        gainFilterName,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            );
        }
        finally
        {
            // Removing the input takes its filter and its scene item with it.
            if (browserInputName is not null)
            {
                try
                {
                    await client
                        .Inputs.RemoveInputAsync(
                            new(inputName: browserInputName),
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
                }
                catch (ObsWebSocketException)
                {
                    // Nothing useful to do about a fixture that will not go away. The next run
                    // recreates it by the same name and OBS rejects the duplicate visibly.
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Exercises the modern conveniences against a scene and input this method creates itself,
    /// so the run does not depend on any particular OBS layout. Everything it makes is removed
    /// again, whether the checks pass or not.
    /// </summary>
    internal static async Task<
        List<(string Label, bool Pass, string Detail)>
    > ValidateModernApisAsync(
        ObsWebSocketClient client,
        HealthCheckService healthChecks,
        ObsSourceKinds kinds,
        GetOutputListResponseData outputs,
        CancellationToken cancellationToken
    )
    {
        List<(string Label, bool Pass, string Detail)> results = [];

        string suffix = Guid.NewGuid().ToString("N")[..8];
        string sceneName = $"__obsws_validation_{suffix}";
        string inputName = $"__obsws_input_{suffix}";

        GetSceneListResponseData? sceneList = await client
            .Scenes.GetSceneListAsync(new(), cancellationToken)
            .ConfigureAwait(false);
        string originalScene = sceneList?.CurrentProgramSceneName ?? string.Empty;

        bool sceneCreated = false;
        bool inputCreated = false;

        try
        {
            await client
                .Scenes.CreateSceneAsync(new CreateSceneRequestData(sceneName), cancellationToken)
                .ConfigureAwait(false);
            sceneCreated = true;

            results.Add(
                await TrySettingsCheckAsync(
                        "SceneExistsAsync",
                        async () =>
                        {
                            bool present = await client
                                .Scenes.SceneExistsAsync(sceneName, cancellationToken)
                                .ConfigureAwait(false);
                            bool absent = await client
                                .Scenes.SceneExistsAsync(sceneName + "__nope", cancellationToken)
                                .ConfigureAwait(false);
                            return (present && !absent, $"present={present}, absent={!absent}");
                        }
                    )
                    .ConfigureAwait(false)
            );

            // A media source carries audio, so the volume and media transport helpers apply.
            _ = await client
                .Inputs.CreateInputAsync(
                    "ffmpeg_source",
                    inputName,
                    new MediaSourceSettings(IsLocalFile: true),
                    sceneName: sceneName,
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
            inputCreated = true;

            results.Add(
                await TrySettingsCheckAsync(
                        "FindSceneItemIdAsync",
                        async () =>
                        {
                            double? hit = await client
                                .SceneItems.FindSceneItemIdAsync(
                                    sceneName,
                                    inputName,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            double? miss = await client
                                .SceneItems.FindSceneItemIdAsync(
                                    sceneName,
                                    "__not_here__",
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            return (
                                hit is not null && miss is null,
                                $"hit={hit}, miss={(miss is null ? "null" : "unexpected")}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "SetSceneItemEnabledAsync (toggle)",
                        async () =>
                        {
                            bool off = await client
                                .SceneItems.SetSceneItemEnabledAsync(
                                    sceneName,
                                    inputName,
                                    false,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            bool toggled = await client
                                .SceneItems.SetSceneItemEnabledAsync(
                                    sceneName,
                                    inputName,
                                    null,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            return (!off && toggled, $"set false -> {off}, toggled -> {toggled}");
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "SetInputVolumeDbAsync",
                        async () =>
                        {
                            await client
                                .Inputs.SetInputVolumeDbAsync(inputName, -6, cancellationToken)
                                .ConfigureAwait(false);
                            GetInputVolumeResponseData? volume = await client
                                .Inputs.GetInputVolumeAsync(
                                    new GetInputVolumeRequestData(inputName: inputName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            double db = volume?.InputVolumeDb ?? double.NaN;
                            return (Math.Abs(db + 6) < 0.5, $"db={db:0.##}");
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Media transport (typed enum)",
                        async () =>
                        {
                            await client
                                .MediaInputs.TriggerMediaActionAsync(
                                    inputName,
                                    MediaInputAction.Stop,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            // Read the state back, so this proves the action landed rather than only that
                            // the request was accepted.
                            GetMediaInputStatusResponseData? status = await client
                                .MediaInputs.GetMediaInputStatusAsync(
                                    new GetMediaInputStatusRequestData(inputName: inputName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            string? state = status?.MediaState;
                            bool stopped =
                                state is not null
                                    && state.Contains("STOPPED", StringComparison.Ordinal)
                                || state is not null
                                    && state.Contains("NONE", StringComparison.Ordinal);

                            return (
                                stopped,
                                $"sent {MediaInputAction.Stop.ToWireValue()}, state={state}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Event stream (await foreach)",
                        async () =>
                        {
                            using CancellationTokenSource streamCts =
                                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            streamCts.CancelAfter(TimeSpan.FromSeconds(10));

                            List<string> observed = [];
                            Task consume = Task.Run(
                                async () =>
                                {
                                    try
                                    {
                                        await foreach (
                                            CurrentProgramSceneChangedEventArgs sceneEvent in client
                                                .Scenes.CurrentProgramSceneChangedStream(
                                                    cancellationToken: streamCts.Token
                                                )
                                                .ConfigureAwait(false)
                                        )
                                        {
                                            observed.Add(
                                                sceneEvent.EventData.SceneName ?? string.Empty
                                            );
                                            if (observed.Count >= 2)
                                            {
                                                await streamCts.CancelAsync().ConfigureAwait(false);
                                            }
                                        }
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        // Expected once both switches are seen or the window elapses.
                                    }
                                },
                                CancellationToken.None
                            );

                            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                            await client
                                .Scenes.SwitchProgramSceneAsync(
                                    sceneName,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);
                            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
                            if (!string.IsNullOrEmpty(originalScene))
                            {
                                await client
                                    .Scenes.SwitchProgramSceneAsync(
                                        originalScene,
                                        cancellationToken: cancellationToken
                                    )
                                    .ConfigureAwait(false);
                            }

                            await consume.ConfigureAwait(false);
                            return (
                                observed.Count >= 2,
                                $"observed {observed.Count}: {string.Join(" -> ", observed)}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "WaitForEventAsync (timeout overload)",
                        async () =>
                        {
                            Task<SceneItemEnableStateChangedEventArgs> wait =
                                client.WaitForEventAsync<SceneItemEnableStateChangedEventArgs>(
                                    TimeSpan.FromSeconds(5),
                                    cancellationToken
                                );
                            _ = await client
                                .SceneItems.SetSceneItemEnabledAsync(
                                    sceneName,
                                    inputName,
                                    false,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            try
                            {
                                SceneItemEnableStateChangedEventArgs observed =
                                    await wait.ConfigureAwait(false);
                                return (true, $"enabled={observed.EventData.SceneItemEnabled}");
                            }
                            catch (TimeoutException)
                            {
                                return (false, "timed out");
                            }
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Typed batch builder",
                        async () =>
                        {
                            ObsBatchBuilder batch = new();
                            BatchRef<GetVersionResponseData> versionRef =
                                batch.General.GetVersion();
                            _ = batch.General.Sleep(new SleepRequestData(sleepMillis: 25));
                            BatchRef<GetSceneListResponseData> scenesRef =
                                batch.Scenes.GetSceneList(new GetSceneListRequestData());
                            BatchRef<GetStatsResponseData> statsRef = batch.General.GetStats();

                            BatchResults typedBatch = await client
                                .CallBatchAsync(
                                    batch,
                                    executionType: RequestBatchExecutionType.SerialRealtime,
                                    haltOnFailure: false,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            // Each result is read through the reference its request handed back, so neither
                            // the position nor the response type is restated here.
                            GetVersionResponseData version = typedBatch.Get(versionRef);
                            GetSceneListResponseData scenes = typedBatch.Get(scenesRef);
                            GetStatsResponseData stats = typedBatch.Get(statsRef);

                            return (
                                typedBatch.Count == 4
                                    && typedBatch.AllSucceeded()
                                    && version.ObsVersion is not null
                                    && scenes.Scenes is not null,
                                $"{typedBatch.Count} result(s), OBS {version.ObsVersion}, {scenes.Scenes?.Count} scene(s), {stats.ActiveFps:0} fps"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Batch order and duplicates",
                        async () =>
                        {
                            // Repeats one request type with different payloads and interleaves others, so a
                            // result can only be matched to its request by position.
                            GetSceneListResponseData? allScenes = await client
                                .Scenes.GetSceneListAsync(
                                    new GetSceneListRequestData(),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            string otherScene = allScenes!
                                .Scenes!.Select(scene => scene.SceneName!)
                                .First(name =>
                                    !string.Equals(name, sceneName, StringComparison.Ordinal)
                                );

                            // The same request type appears three times with two different payloads, so a
                            // result can only be matched to its request through the reference it returned.
                            ObsBatchBuilder mixedBatch = new();
                            BatchRef<GetSceneItemListResponseData> firstRef =
                                mixedBatch.SceneItems.GetSceneItemList(
                                    new GetSceneItemListRequestData(sceneName: sceneName)
                                );
                            BatchRef<GetVersionResponseData> versionRef =
                                mixedBatch.General.GetVersion();
                            BatchRef<GetSceneItemListResponseData> secondRef =
                                mixedBatch.SceneItems.GetSceneItemList(
                                    new GetSceneItemListRequestData(sceneName: otherScene)
                                );
                            BatchRef<GetSceneItemListResponseData> thirdRef =
                                mixedBatch.SceneItems.GetSceneItemList(
                                    new GetSceneItemListRequestData(sceneName: sceneName)
                                );
                            _ = mixedBatch.General.GetStats();

                            BatchResults mixed = await client
                                .CallBatchAsync(
                                    mixedBatch,
                                    executionType: RequestBatchExecutionType.SerialRealtime,
                                    haltOnFailure: false,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            if (mixed.Count != 5 || !mixed.AllSucceeded())
                            {
                                return (
                                    false,
                                    $"expected 5 successes, got {mixed.Count} with {mixed.GetFailures().Count()} failure(s)"
                                );
                            }

                            GetSceneItemListResponseData first = mixed.Get(firstRef);
                            GetVersionResponseData version = mixed.Get(versionRef);
                            GetSceneItemListResponseData second = mixed.Get(secondRef);
                            GetSceneItemListResponseData third = mixed.Get(thirdRef);

                            // The two lookups of the same scene must agree, and differ from the other scene.
                            int firstCount = first.SceneItems?.Count ?? -1;
                            int secondCount = second.SceneItems?.Count ?? -1;
                            int thirdCount = third.SceneItems?.Count ?? -1;
                            bool repeatsAgree = firstCount == thirdCount;
                            bool distinguishable =
                                firstCount != secondCount
                                || !string.Equals(sceneName, otherScene, StringComparison.Ordinal);

                            return (
                                repeatsAgree && distinguishable && version.ObsVersion is not null,
                                $"[{firstCount}, v{version.ObsVersion}, {secondCount}, {thirdCount}] repeats agree = {repeatsAgree}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Batch partial failure",
                        async () =>
                        {
                            // haltOnFailure false, so the good requests either side of a bad one still run.
                            ObsBatchBuilder partialBatch = new();
                            BatchRef<GetVersionResponseData> goodRef =
                                partialBatch.General.GetVersion();
                            BatchRef<GetSceneItemListResponseData> badRef =
                                partialBatch.SceneItems.GetSceneItemList(
                                    new GetSceneItemListRequestData(sceneName: "__no_such_scene__")
                                );
                            _ = partialBatch.General.GetStats();

                            BatchResults partial = await client
                                .CallBatchAsync(
                                    partialBatch,
                                    executionType: RequestBatchExecutionType.SerialRealtime,
                                    haltOnFailure: false,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            RequestResponsePayload<object>[] failures = [.. partial.GetFailures()];
                            if (partial.Count != 3 || failures.Length != 1)
                            {
                                return (
                                    false,
                                    $"{partial.Count} result(s), {failures.Length} failure(s)"
                                );
                            }

                            // GetRequiredData surfaces the OBS status rather than a null payload.
                            string caught;
                            try
                            {
                                _ = partial.Get(badRef);
                                caught = "no exception";
                            }
                            catch (ObsWebSocketRequestException ex)
                            {
                                caught = $"code {(int?)ex.StatusCode}";
                            }

                            // TryGet reports the failure without throwing.
                            bool tryGetReportedFailure = !partial.TryGet(badRef, out _);
                            bool neighboursOk =
                                tryGetReportedFailure
                                && partial.Get(goodRef).ObsVersion is not null;

                            return (
                                neighboursOk
                                    && caught.StartsWith("code ", StringComparison.Ordinal),
                                $"1 failed ({caught}), neighbours ran"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Event stream buffering",
                        async () =>
                        {
                            // A stream keeps the newest events when a consumer falls behind rather than
                            // stalling the receive loop, so a small capacity drops the oldest.
                            using CancellationTokenSource cts =
                                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            cts.CancelAfter(TimeSpan.FromSeconds(10));

                            IAsyncEnumerator<SceneItemEnableStateChangedEventArgs> enumerator =
                                client
                                    .SceneItems.SceneItemEnableStateChangedStream(
                                        capacity: 2,
                                        cancellationToken: cts.Token
                                    )
                                    .GetAsyncEnumerator(cts.Token);

                            try
                            {
                                ValueTask<bool> pending = enumerator.MoveNextAsync();

                                // Toggle more times than the buffer holds.
                                for (int i = 0; i < 4; i++)
                                {
                                    _ = await client
                                        .SceneItems.SetSceneItemEnabledAsync(
                                            sceneName,
                                            inputName,
                                            i % 2 == 0,
                                            cancellationToken
                                        )
                                        .ConfigureAwait(false);
                                }

                                bool first = await pending.ConfigureAwait(false);
                                return (
                                    first,
                                    first
                                        ? "buffered and delivered under capacity pressure"
                                        : "no event"
                                );
                            }
                            finally
                            {
                                await enumerator.DisposeAsync().ConfigureAwait(false);
                            }
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Single-request values (non-batch)",
                        async () =>
                        {
                            // The same response types that come back empty inside a batch, fetched singly.
                            GetSceneItemListResponseData? items = await client
                                .SceneItems.GetSceneItemListAsync(
                                    new GetSceneItemListRequestData(sceneName: sceneName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            GetStatsResponseData? st = await client
                                .General.GetStatsAsync(cancellationToken)
                                .ConfigureAwait(false);
                            GetVersionResponseData? ver = await client
                                .General.GetVersionAsync(cancellationToken)
                                .ConfigureAwait(false);

                            int itemCount = items?.SceneItems?.Count ?? -1;
                            double fps = st?.ActiveFps ?? 0;

                            return (
                                itemCount >= 0 && fps > 0 && ver?.ObsVersion is not null,
                                $"items={itemCount}, {fps:0} fps, v={ver?.ObsVersion}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Batch parallel execution",
                        async () =>
                        {
                            // OBS pairs each result with another request's response data under parallel
                            // execution, so reading by reference must refuse rather than return the wrong
                            // request's payload.
                            ObsBatchBuilder par = new();
                            BatchRef<GetVersionResponseData> v = par.General.GetVersion();
                            _ = par.SceneItems.GetSceneItemList(
                                new GetSceneItemListRequestData(sceneName: sceneName)
                            );

                            BatchResults r = await client
                                .CallBatchAsync(
                                    par,
                                    executionType: RequestBatchExecutionType.Parallel,
                                    haltOnFailure: false,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            string guarded;
                            try
                            {
                                _ = r.Get(v);
                                guarded = "returned data";
                            }
                            catch (ObsWebSocketException ex)
                            {
                                guarded = ex.Message.Contains("Parallel", StringComparison.Ordinal)
                                    ? "refused"
                                    : "threw: " + ex.Message;
                            }

                            return (
                                r.Count == 2
                                    && guarded == "refused"
                                    && !r.TryGet(v, out GetVersionResponseData? _),
                                $"{r.Count} raw result(s), reference {guarded}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Batch halt on failure",
                        async () =>
                        {
                            ObsBatchBuilder halt = new();
                            BatchRef<GetVersionResponseData> first = halt.General.GetVersion();
                            BatchRef<GetSceneItemListResponseData> bad =
                                halt.SceneItems.GetSceneItemList(
                                    new GetSceneItemListRequestData(sceneName: "__no_such_scene__")
                                );
                            BatchRef<GetStatsResponseData> never = halt.General.GetStats();

                            BatchResults r = await client
                                .CallBatchAsync(
                                    halt,
                                    executionType: RequestBatchExecutionType.SerialRealtime,
                                    haltOnFailure: true,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            bool firstOk = r.Get(first).ObsVersion is not null;
                            bool badRejected = !r.TryGet(bad, out GetSceneItemListResponseData? _);

                            // The third request never ran, so reading it explains itself rather than
                            // returning someone else's result.
                            string neverMsg;
                            try
                            {
                                _ = r.Get(never);
                                neverMsg = "returned a result";
                            }
                            catch (ObsWebSocketException ex)
                            {
                                neverMsg = ex.Message.Contains(
                                    "never ran",
                                    StringComparison.Ordinal
                                )
                                    ? "explained"
                                    : "threw: " + ex.Message;
                            }

                            return (
                                firstOk && badRejected && neverMsg == "explained",
                                $"{r.Count} result(s), unrun request {neverMsg}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "SetInputVolumeMulAsync",
                        async () =>
                        {
                            GetInputVolumeResponseData? before = await client
                                .Inputs.GetInputVolumeAsync(
                                    new GetInputVolumeRequestData(inputName: inputName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            double original = before!.InputVolumeMul;

                            await client
                                .Inputs.SetInputVolumeMulAsync(inputName, 0.5, cancellationToken)
                                .ConfigureAwait(false);
                            GetInputVolumeResponseData? after = await client
                                .Inputs.GetInputVolumeAsync(
                                    new GetInputVolumeRequestData(inputName: inputName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            double mul = after!.InputVolumeMul;

                            await client
                                .Inputs.SetInputVolumeMulAsync(
                                    inputName,
                                    original,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            return (Math.Abs(mul - 0.5) < 0.01, $"mul={mul:0.###}");
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "SwitchProgramSceneAsync",
                        async () =>
                        {
                            await client
                                .Scenes.SwitchProgramSceneAsync(
                                    sceneName,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);
                            GetSceneListResponseData? mid = await client
                                .Scenes.GetSceneListAsync(
                                    new GetSceneListRequestData(),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            bool switched = string.Equals(
                                mid?.CurrentProgramSceneName,
                                sceneName,
                                StringComparison.Ordinal
                            );

                            await client
                                .Scenes.SwitchProgramSceneAsync(
                                    originalScene,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);
                            GetSceneListResponseData? restored = await client
                                .Scenes.GetSceneListAsync(
                                    new GetSceneListRequestData(),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            return (
                                switched
                                    && string.Equals(
                                        restored?.CurrentProgramSceneName,
                                        originalScene,
                                        StringComparison.Ordinal
                                    ),
                                $"switched={switched}, restored to '{restored?.CurrentProgramSceneName}'"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "FindSceneItemIdAsync",
                        async () =>
                        {
                            long? id = await client
                                .SceneItems.FindSceneItemIdAsync(
                                    sceneName,
                                    inputName,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            long? miss = await client
                                .SceneItems.FindSceneItemIdAsync(
                                    sceneName,
                                    "__absent__",
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            return (
                                id is not null && miss is null,
                                $"id={id}, miss={(miss is null ? "null" : "unexpected")}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Screenshot helpers",
                        async () =>
                        {
                            byte[]? bytes = await client
                                .Sources.GetSourceScreenshotBytesAsync(
                                    sceneName,
                                    "png",
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            string path = Path.Combine(
                                Path.GetTempPath(),
                                $"obsws_{Guid.NewGuid():N}.png"
                            );
                            try
                            {
                                await client
                                    .Sources.SaveSourceScreenshotToFileAsync(
                                        sceneName,
                                        path,
                                        "png",
                                        cancellationToken: cancellationToken
                                    )
                                    .ConfigureAwait(false);

                                // A PNG starts with the eight byte signature, so this checks real image data
                                // rather than merely that the call returned.
                                byte[] written = await File.ReadAllBytesAsync(
                                        path,
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false);
                                bool pngOnDisk =
                                    written.Length > 8
                                    && written[0] == 0x89
                                    && written[1] == 0x50
                                    && written[2] == 0x4E
                                    && written[3] == 0x47;
                                bool pngInMemory =
                                    bytes is { Length: > 8 }
                                    && bytes[0] == 0x89
                                    && bytes[1] == 0x50
                                    && bytes[2] == 0x4E
                                    && bytes[3] == 0x47;

                                return (
                                    pngInMemory && pngOnDisk,
                                    $"{bytes?.Length ?? 0} bytes in memory, {written.Length} on disk"
                                );
                            }
                            finally
                            {
                                if (File.Exists(path))
                                {
                                    File.Delete(path);
                                }
                            }
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Ensure profile and scene collection",
                        async () =>
                        {
                            // Asking for the one already active proves the check without disrupting OBS,
                            // since switching either of these reloads the whole configuration.
                            GetProfileListResponseData? profiles = await client
                                .Config.GetProfileListAsync(cancellationToken)
                                .ConfigureAwait(false);
                            GetSceneCollectionListResponseData? collections = await client
                                .Config.GetSceneCollectionListAsync(cancellationToken)
                                .ConfigureAwait(false);

                            bool profileOk = await client
                                .Config.EnsureProfileActiveAsync(
                                    profiles!.CurrentProfileName!,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            bool collectionOk = await client
                                .Config.EnsureSceneCollectionActiveAsync(
                                    collections!.CurrentSceneCollectionName!,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            bool absent = await client
                                .Config.EnsureProfileActiveAsync(
                                    "__no_such_profile__",
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            return (
                                profileOk && collectionOk && !absent,
                                $"profile={profiles.CurrentProfileName}, collection={collections.CurrentSceneCollectionName}, absent reported {absent}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Media transport shorthands",
                        async () =>
                        {
                            await client
                                .MediaInputs.PlayMediaAsync(inputName, cancellationToken)
                                .ConfigureAwait(false);
                            await client
                                .MediaInputs.PauseMediaAsync(inputName, cancellationToken)
                                .ConfigureAwait(false);
                            await client
                                .MediaInputs.RestartMediaAsync(inputName, cancellationToken)
                                .ConfigureAwait(false);
                            await client
                                .MediaInputs.StopMediaAsync(inputName, cancellationToken)
                                .ConfigureAwait(false);

                            GetMediaInputStatusResponseData? status = await client
                                .MediaInputs.GetMediaInputStatusAsync(
                                    new GetMediaInputStatusRequestData(inputName: inputName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            return (status is not null, $"state={status?.MediaState}");
                        }
                    )
                    .ConfigureAwait(false)
            );

            // Disabled: toggling the virtual camera takes down this OBS install. The fault is in
            // the Stream Deck plugin (streamdeckpluginobs32.dll appears at the fault address and
            // in every frame above it), not in OBS or in this library. Re-enable once that plugin
            // is removed.
            // results.Add(
            // await TrySettingsCheckAsync(
            // "Virtual camera toggle",
            // async () =>
            // {
            // bool before = await client
            // .Outputs.IsVirtualCamActiveAsync(cancellationToken)
            // .ConfigureAwait(false);

            // bool? turnedOn = await client
            // .Outputs.SetVirtualCamActiveAndWaitAsync(
            // !before,
            // cancellationToken: cancellationToken
            // )
            // .ConfigureAwait(false);
            // bool observed = await client
            // .Outputs.IsVirtualCamActiveAsync(cancellationToken)
            // .ConfigureAwait(false);

            // // Put it back the way it was found.
            // _ = await client
            // .Outputs.SetVirtualCamActiveAndWaitAsync(
            // before,
            // cancellationToken: cancellationToken
            // )
            // .ConfigureAwait(false);
            // bool restored = await client
            // .Outputs.IsVirtualCamActiveAsync(cancellationToken)
            // .ConfigureAwait(false);

            // return (
            // turnedOn == !before && observed == !before && restored == before,
            // $"{before} -> {observed} -> {restored}"
            // );
            // }
            // )
            // .ConfigureAwait(false)
            // );

            results.Add(
                await TrySettingsCheckAsync(
                        "Integer fields round trip",
                        async () =>
                        {
                            // The protocol calls every number "Number", so these fields used to
                            // arrive as double. Writing one and reading it back proves the
                            // retype survives the wire in both directions, which matters most
                            // for MessagePack, where an int and a float are different encodings.
                            long itemId =
                                await client
                                    .SceneItems.FindSceneItemIdAsync(
                                        sceneName,
                                        inputName,
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false)
                                ?? throw new InvalidOperationException(
                                    $"'{inputName}' is not in '{sceneName}'."
                                );

                            GetSceneItemIndexResponseData originalIndex = await client
                                .SceneItems.GetSceneItemIndexAsync(
                                    new GetSceneItemIndexRequestData(
                                        sceneItemId: itemId,
                                        sceneName: sceneName
                                    ),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            await client
                                .SceneItems.SetSceneItemIndexAsync(
                                    new SetSceneItemIndexRequestData(
                                        sceneItemId: itemId,
                                        sceneItemIndex: 0,
                                        sceneName: sceneName
                                    ),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            GetSceneItemIndexResponseData afterIndex = await client
                                .SceneItems.GetSceneItemIndexAsync(
                                    new GetSceneItemIndexRequestData(
                                        sceneItemId: itemId,
                                        sceneName: sceneName
                                    ),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            await client
                                .SceneItems.SetSceneItemIndexAsync(
                                    new SetSceneItemIndexRequestData(
                                        sceneItemId: itemId,
                                        sceneItemIndex: originalIndex.SceneItemIndex,
                                        sceneName: sceneName
                                    ),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            // A negative value, since OBS accepts negative sync offsets and a
                            // sign error would otherwise go unnoticed.
                            GetInputAudioSyncOffsetResponseData originalOffset = await client
                                .Inputs.GetInputAudioSyncOffsetAsync(
                                    new GetInputAudioSyncOffsetRequestData(inputName: inputName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            await client
                                .Inputs.SetInputAudioSyncOffsetAsync(
                                    new SetInputAudioSyncOffsetRequestData(
                                        inputAudioSyncOffset: -125,
                                        inputName: inputName
                                    ),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            GetInputAudioSyncOffsetResponseData afterOffset = await client
                                .Inputs.GetInputAudioSyncOffsetAsync(
                                    new GetInputAudioSyncOffsetRequestData(inputName: inputName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            await client
                                .Inputs.SetInputAudioSyncOffsetAsync(
                                    new SetInputAudioSyncOffsetRequestData(
                                        inputAudioSyncOffset: originalOffset.InputAudioSyncOffset,
                                        inputName: inputName
                                    ),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            return (
                                afterIndex.SceneItemIndex == 0
                                    && afterOffset.InputAudioSyncOffset == -125,
                                $"index {originalIndex.SceneItemIndex} -> {afterIndex.SceneItemIndex}, "
                                    + $"syncOffset {originalOffset.InputAudioSyncOffset} -> {afterOffset.InputAudioSyncOffset}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Preview scene helpers",
                        async () =>
                        {
                            // Preview only exists in Studio Mode, so turn it on for the check and
                            // put it back however it was found.
                            GetStudioModeEnabledResponseData studio = await client
                                .Ui.GetStudioModeEnabledAsync(cancellationToken)
                                .ConfigureAwait(false);
                            if (!studio.StudioModeEnabled)
                            {
                                await client
                                    .Ui.SetStudioModeEnabledAsync(new(true), cancellationToken)
                                    .ConfigureAwait(false);
                            }

                            try
                            {
                                // Every switch waits for the event confirming it. OBS points
                                // Preview at the Program scene while enabling Studio Mode, and
                                // that lands after StudioModeStateChanged, so a switch that does
                                // not wait for its own confirmation gets silently undone.
                                await client
                                    .Scenes.SwitchPreviewSceneAndWaitAsync(
                                        sceneName,
                                        cancellationToken: cancellationToken
                                    )
                                    .ConfigureAwait(false);
                                GetCurrentPreviewSceneResponseData preview = await client
                                    .Scenes.GetCurrentPreviewSceneAsync(cancellationToken)
                                    .ConfigureAwait(false);

                                // The plain overload, confirmed by waiting on the event directly.
                                Task<CurrentPreviewSceneChangedEventArgs> back =
                                    client.WaitForEventAsync<CurrentPreviewSceneChangedEventArgs>(
                                        e =>
                                            string.Equals(
                                                e.EventData.SceneName,
                                                originalScene,
                                                StringComparison.Ordinal
                                            ),
                                        TimeSpan.FromSeconds(5),
                                        cancellationToken
                                    );
                                await client
                                    .Scenes.SwitchPreviewSceneAsync(
                                        originalScene,
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false);
                                _ = await back.ConfigureAwait(false);

                                GetCurrentPreviewSceneResponseData restored = await client
                                    .Scenes.GetCurrentPreviewSceneAsync(cancellationToken)
                                    .ConfigureAwait(false);

                                bool ok =
                                    string.Equals(
                                        preview.SceneName,
                                        sceneName,
                                        StringComparison.Ordinal
                                    )
                                    && string.Equals(
                                        restored.SceneName,
                                        originalScene,
                                        StringComparison.Ordinal
                                    );

                                return (
                                    ok,
                                    $"wanted {sceneName} got {preview.SceneName}, "
                                        + $"then wanted {originalScene} got {restored.SceneName}"
                                );
                            }
                            finally
                            {
                                if (!studio.StudioModeEnabled)
                                {
                                    await client
                                        .Ui.SetStudioModeEnabledAsync(new(false), cancellationToken)
                                        .ConfigureAwait(false);
                                }
                            }
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "SourceExistsAsync",
                        async () =>
                        {
                            bool present = await client
                                .Sources.SourceExistsAsync(inputName, cancellationToken)
                                .ConfigureAwait(false);
                            bool absent = await client
                                .Sources.SourceExistsAsync("__absent__", cancellationToken)
                                .ConfigureAwait(false);

                            return (present && !absent, $"present={present}, absent={absent}");
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "SetInputMutesAsync",
                        async () =>
                        {
                            GetInputMuteResponseData before = await client
                                .Inputs.GetInputMuteAsync(
                                    new(inputName: inputName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            // One real input beside one that does not exist, so the returned
                            // results have to show a success next to a failure.
                            BatchResults muteResults = await client
                                .Inputs.SetInputMutesAsync(
                                    [(inputName, !before.InputMuted), ("__absent__", true)],
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            GetInputMuteResponseData after = await client
                                .Inputs.GetInputMuteAsync(
                                    new(inputName: inputName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            await client
                                .Inputs.SetInputMutesAsync(
                                    [(inputName, before.InputMuted)],
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            bool ok =
                                muteResults.Count == 2
                                && muteResults[0].RequestStatus.Result
                                && !muteResults[1].RequestStatus.Result
                                && after.InputMuted == !before.InputMuted;

                            return (
                                ok,
                                $"{muteResults.Count} results, muted {before.InputMuted} -> {after.InputMuted}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Transition settings read",
                        async () =>
                        {
                            GetCurrentSceneTransitionResponseData current = await client
                                .Transitions.GetCurrentSceneTransitionAsync(cancellationToken)
                                .ConfigureAwait(false);
                            // The typed Get*SettingsAsync helpers deserialize into a settings
                            // record; the generated request is the way to read the raw JSON.
                            // A transition with nothing to configure, such as Fade, legitimately
                            // reports no settings, so the name and kind are what is asserted.
                            JsonElement? settings = current.TransitionSettings;

                            return (
                                !string.IsNullOrEmpty(current.TransitionName)
                                    && !string.IsNullOrEmpty(current.TransitionKind),
                                $"{current.TransitionName} ({current.TransitionKind}), "
                                    + $"settings {(settings is null ? "none" : "present")}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Canvas screenshot helper",
                        async () =>
                        {
                            byte[]? bytes = await client
                                .Sources.GetSourceScreenshotOnCanvasBytesAsync(
                                    sceneName,
                                    "png",
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            bool png =
                                bytes is { Length: > 8 }
                                && bytes[0] == 0x89
                                && bytes[1] == 0x50
                                && bytes[2] == 0x4E
                                && bytes[3] == 0x47;

                            return (png, $"{bytes?.Length ?? 0} bytes at canvas size");
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Parallel batch, verdict without attribution",
                        async () =>
                        {
                            // A parallel batch of writes is the case Parallel is actually good
                            // for: OBS mispairs the rows, but a verdict over all of them does not
                            // depend on which row is which.
                            ObsBatchBuilder par = new();
                            _ = par.Inputs.SetInputMute(
                                new SetInputMuteRequestData(inputName: inputName, inputMuted: true)
                            );
                            _ = par.Inputs.SetInputMute(
                                new SetInputMuteRequestData(inputName: inputName, inputMuted: false)
                            );

                            BatchResults ok = await client
                                .CallBatchAsync(
                                    par,
                                    executionType: RequestBatchExecutionType.Parallel,
                                    haltOnFailure: false,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            // Same again with one request that cannot succeed, so the count of
                            // failures is checked as well as the all-succeeded verdict.
                            ObsBatchBuilder mixed = new();
                            _ = mixed.Inputs.SetInputMute(
                                new SetInputMuteRequestData(inputName: inputName, inputMuted: false)
                            );
                            _ = mixed.Inputs.SetInputMute(
                                new SetInputMuteRequestData(
                                    inputName: "__absent__",
                                    inputMuted: true
                                )
                            );

                            BatchResults partial = await client
                                .CallBatchAsync(
                                    mixed,
                                    executionType: RequestBatchExecutionType.Parallel,
                                    haltOnFailure: false,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            int failures = partial.GetFailures().Count();

                            return (
                                ok.AllSucceeded() && !partial.AllSucceeded() && failures == 1,
                                $"all-ok verdict {ok.AllSucceeded()}, mixed verdict "
                                    + $"{partial.AllSucceeded()} with {failures} failure(s)"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Concurrent requests keep their own results",
                        async () =>
                        {
                            // The answer to "how do I run things in parallel" once a parallel
                            // batch is ruled out for reads. The client multiplexes on request id.
                            Task<GetVersionResponseData> version = client.General.GetVersionAsync(
                                cancellationToken
                            );
                            Task<GetVideoSettingsResponseData> video =
                                client.Config.GetVideoSettingsAsync(cancellationToken);
                            Task<GetSceneItemListResponseData> itemsHere =
                                client.SceneItems.GetSceneItemListAsync(
                                    new GetSceneItemListRequestData(sceneName: sceneName),
                                    cancellationToken
                                );
                            Task<GetSceneItemListResponseData> itemsThere =
                                client.SceneItems.GetSceneItemListAsync(
                                    new GetSceneItemListRequestData(sceneName: originalScene),
                                    cancellationToken
                                );

                            await Task.WhenAll(version, video, itemsHere, itemsThere)
                                .ConfigureAwait(false);

                            // Each answer has to match what the same request returns on its own.
                            GetVersionResponseData serialVersion = await client
                                .General.GetVersionAsync(cancellationToken)
                                .ConfigureAwait(false);
                            GetSceneItemListResponseData serialHere = await client
                                .SceneItems.GetSceneItemListAsync(
                                    new GetSceneItemListRequestData(sceneName: sceneName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            bool ok =
                                string.Equals(
                                    version.Result.ObsVersion,
                                    serialVersion.ObsVersion,
                                    StringComparison.Ordinal
                                )
                                && video.Result.FpsNumerator > 0
                                && itemsHere.Result.SceneItems?.Count
                                    == serialHere.SceneItems?.Count;

                            return (
                                ok,
                                $"v={version.Result.ObsVersion}, fps={video.Result.FpsNumerator}, "
                                    + $"items {itemsHere.Result.SceneItems?.Count} here vs "
                                    + $"{itemsThere.Result.SceneItems?.Count} there"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Low level Add and raw results",
                        async () =>
                        {
                            // Hand rolled batch: Add covers anything the generated methods do not,
                            // and the raw payload helpers read it back.
                            ObsBatchBuilder raw = new();
                            _ = raw.Add("GetVersion");
                            _ = raw.Add("GetStats");

                            BatchResults rawResults = await client
                                .CallBatchAsync(
                                    raw,
                                    executionType: RequestBatchExecutionType.SerialRealtime,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            GetVersionResponseData? v = rawResults
                                .Raw[0]
                                .GetData<GetVersionResponseData>();

                            // And the same request without any batch at all.
                            GetVersionResponseData? direct = await client
                                .CallAsync<GetVersionResponseData>(
                                    "GetVersion",
                                    null,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            bool ok =
                                rawResults.Count == 2
                                && v is not null
                                && direct is not null
                                && string.Equals(
                                    v.ObsVersion,
                                    direct.ObsVersion,
                                    StringComparison.Ordinal
                                );

                            return (
                                ok,
                                $"raw batch {rawResults.Count}, CallAsync {direct?.ObsVersion}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Scene item list reindexed",
                        async () =>
                        {
                            // Reindexing asks OBS for the basic scene item list, a different shape
                            // from every other sceneItems array, so the event needs its own stub.
                            GetSceneItemListResponseData items = await client
                                .SceneItems.GetSceneItemListAsync(
                                    new GetSceneItemListRequestData(sceneName: sceneName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            if (items.SceneItems.Count == 0)
                            {
                                return (false, "no scene items to reindex");
                            }

                            long id = items.SceneItems[0].SceneItemId;
                            int index = items.SceneItems[0].SceneItemIndex;

                            Task<SceneItemListReindexedEventArgs> reindexed =
                                client.WaitForEventAsync<SceneItemListReindexedEventArgs>(
                                    timeout: TimeSpan.FromSeconds(5),
                                    cancellationToken: cancellationToken
                                );

                            await client
                                .SceneItems.SetSceneItemIndexAsync(
                                    new SetSceneItemIndexRequestData(
                                        sceneItemId: id,
                                        sceneItemIndex: index,
                                        sceneName: sceneName
                                    ),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            SceneItemListReindexedEventArgs args = await reindexed.ConfigureAwait(
                                false
                            );

                            SceneItemOrderStub? moved = args.EventData.SceneItems.Find(i =>
                                i.SceneItemId == id
                            );

                            return (
                                moved is not null
                                    && string.Equals(
                                        args.EventData.SceneName,
                                        sceneName,
                                        StringComparison.Ordinal
                                    ),
                                $"{args.EventData.SceneItems.Count} item(s) reindexed, "
                                    + $"item {id} at index {moved?.SceneItemIndex}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Input volume meters",
                        async () =>
                        {
                            // High rate event with its own stub. It was read as an InputStub and
                            // failed on the kind fields it never sends, so it never fired at all.
                            // A collection with no audio input reports no meters at all, which
                            // says nothing about whether the payload reads correctly.
                            GetInputListResponseData present = await client
                                .Inputs.GetInputListAsync(new(), cancellationToken)
                                .ConfigureAwait(false);
                            string? seeded = null;
                            if (
                                !present.Inputs.Exists(i =>
                                    i.InputKind.Contains("wasapi", StringComparison.Ordinal)
                                )
                            )
                            {
                                // OBS meters only the inputs it considers active, which means
                                // the ones in the program scene. Anywhere else reports nothing.
                                GetSceneListResponseData live = await client
                                    .Scenes.GetSceneListAsync(new(), cancellationToken)
                                    .ConfigureAwait(false);
                                seeded = $"__obsws_meter_{Guid.NewGuid():N}"[..22];
                                await client
                                    .Inputs.CreateInputAsync(
                                        new(
                                            inputName: seeded,
                                            inputKind: kinds.AudioCapture!,
                                            sceneName: live.CurrentProgramSceneName ?? sceneName
                                        ),
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false);
                            }

                            EventSubscription? before = client.CurrentEventSubscriptions;
                            await client
                                .ReidentifyAsync(
                                    (uint)(
                                        (before ?? EventSubscription.All)
                                        | EventSubscription.InputVolumeMeters
                                    ),
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            try
                            {
                                InputVolumeMetersEventArgs meters = await client
                                    .WaitForEventAsync<InputVolumeMetersEventArgs>(
                                        timeout: TimeSpan.FromSeconds(5),
                                        cancellationToken: cancellationToken
                                    )
                                    .ConfigureAwait(false);

                                InputVolumeMeterStub? first =
                                    meters.EventData.Inputs.Count > 0
                                        ? meters.EventData.Inputs[0]
                                        : null;

                                // An input with no audio channels reports an empty level list, so
                                // the levels are checked where they exist rather than required.
                                bool levelsWellFormed = meters.EventData.Inputs.TrueForAll(i =>
                                    i.InputLevelsMul.TrueForAll(channel => channel.Count == 3)
                                );
                                int channels = meters.EventData.Inputs.Sum(i =>
                                    i.InputLevelsMul.Count
                                );

                                bool ok =
                                    first is not null
                                    && !string.IsNullOrEmpty(first.InputName)
                                    && Guid.TryParse(first.InputUuid, out _)
                                    && levelsWellFormed;

                                return (
                                    ok,
                                    $"{meters.EventData.Inputs.Count} input(s), first '{first?.InputName}', "
                                        + $"{channels} channel(s) total, three levels each = "
                                        + $"{levelsWellFormed}"
                                );
                            }
                            finally
                            {
                                if (before is not null)
                                {
                                    await client
                                        .ReidentifyAsync(
                                            (uint)before.Value,
                                            cancellationToken: CancellationToken.None
                                        )
                                        .ConfigureAwait(false);
                                }

                                if (seeded is not null)
                                {
                                    await client
                                        .Inputs.RemoveInputAsync(
                                            new(inputName: seeded),
                                            CancellationToken.None
                                        )
                                        .ConfigureAwait(false);
                                }
                            }
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Handles address by name and by uuid",
                        async () =>
                        {
                            // The point of a uuid handle is that it survives a rename. Both forms
                            // have to reach the same scene for that to be worth anything.
                            SceneHandle byName = sceneName;
                            SceneOperations resolved = await client
                                .Scene(byName)
                                .ResolveAsync(cancellationToken)
                                .ConfigureAwait(false);
                            SceneHandle byUuid = resolved.Handle;

                            GetSceneItemListResponseData viaName = await client
                                .Scene(byName)
                                .GetItemListAsync(cancellationToken)
                                .ConfigureAwait(false);
                            GetSceneItemListResponseData viaUuid = await client
                                .Scene(byUuid)
                                .GetItemListAsync(cancellationToken)
                                .ConfigureAwait(false);

                            string renamed = sceneName + "_renamed";
                            await client
                                .Scene(byUuid)
                                .SetNameAsync(renamed, cancellationToken)
                                .ConfigureAwait(false);

                            bool uuidStillWorks;
                            try
                            {
                                _ = await client
                                    .Scene(byUuid)
                                    .GetItemListAsync(cancellationToken)
                                    .ConfigureAwait(false);
                                uuidStillWorks = true;
                            }
                            catch (ObsWebSocketRequestException)
                            {
                                uuidStillWorks = false;
                            }

                            bool nameNowMisses;
                            try
                            {
                                _ = await client
                                    .Scene(byName)
                                    .GetItemListAsync(cancellationToken)
                                    .ConfigureAwait(false);
                                nameNowMisses = false;
                            }
                            catch (ObsWebSocketRequestException)
                            {
                                nameNowMisses = true;
                            }

                            await client
                                .Scene(byUuid)
                                .SetNameAsync(sceneName, CancellationToken.None)
                                .ConfigureAwait(false);

                            return (
                                byUuid.IsResolved
                                    && viaName.SceneItems.Count == viaUuid.SceneItems.Count
                                    && uuidStillWorks
                                    && nameNowMisses,
                                $"resolved to {byUuid.Uuid}, both read {viaUuid.SceneItems.Count} "
                                    + $"item(s); after a rename the uuid still resolves "
                                    + $"({uuidStillWorks}) and the name does not ({nameNowMisses})"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "A miss says what does exist",
                        async () =>
                        {
                            // OBS answers ResourceNotFound and the name you already gave it. The
                            // list is in hand from the lookup, so the client can do better.
                            ObsWebSocketResourceNotFoundException ex =
                                await ExpectThrowAsync<ObsWebSocketResourceNotFoundException>(() =>
                                        client
                                            .Scenes.ResolveAsync(
                                                "__obsws_no_such_scene",
                                                cancellationToken
                                            )
                                            .AsTask()
                                    )
                                    .ConfigureAwait(false);

                            return (
                                ex.Available.Count > 0
                                    && ex.Message.Contains(
                                        "__obsws_no_such_scene",
                                        StringComparison.Ordinal
                                    )
                                    && ex.Available.Contains(sceneName, StringComparer.Ordinal),
                                $"named {ex.Available.Count} scene(s), including the one the run made"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "A scene item resolves by source name",
                        async () =>
                        {
                            // The one lookup that is not a convenience: OBS addresses scene items
                            // by a number nothing else reports.
                            SceneItemOperations item = await client
                                .Scene(sceneName)
                                .ItemAsync(inputName, cancellationToken: cancellationToken)
                                .ConfigureAwait(false);

                            GetSceneItemEnabledResponseData enabled = await item.GetEnabledAsync(
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            // Navigating back up reaches the scene the item is in.
                            GetSceneItemListResponseData siblings = await item
                                .Scene.GetItemListAsync(cancellationToken)
                                .ConfigureAwait(false);

                            return (
                                item.Handle.SceneItemId >= 0 && siblings.SceneItems.Count > 0,
                                $"'{inputName}' is item {item.Handle.SceneItemId}, enabled "
                                    + $"{enabled.SceneItemEnabled}, among {siblings.SceneItems.Count} "
                                    + "in its scene"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Canvases category",
                        async () =>
                        {
                            // The only request in its category. Its array carries no item type in
                            // the protocol definition, so the stub is taken from the request
                            // handler and has to be checked against a real OBS on both transports.
                            GetCanvasListResponseData canvases = await client
                                .Canvases.GetCanvasListAsync(cancellationToken)
                                .ConfigureAwait(false);

                            CanvasStub? main = canvases.Canvases.Find(c => c.CanvasFlags.Main);
                            bool ok =
                                canvases.Canvases.Count > 0
                                && main is not null
                                && !string.IsNullOrEmpty(main.CanvasName)
                                && Guid.TryParse(main.CanvasUuid, out _)
                                && main.CanvasVideoSettings.BaseWidth > 0
                                && main.CanvasVideoSettings.BaseHeight > 0
                                && main.CanvasVideoSettings.FpsNumerator > 0;

                            return (
                                ok,
                                $"{canvases.Canvases.Count} canvas(es), main '{main?.CanvasName}' "
                                    + $"{main?.CanvasVideoSettings.BaseWidth}x{main?.CanvasVideoSettings.BaseHeight} "
                                    + $"@ {main?.CanvasVideoSettings.FpsNumerator}/{main?.CanvasVideoSettings.FpsDenominator}, "
                                    + $"flags MAIN={main?.CanvasFlags.Main} MIX_AUDIO={main?.CanvasFlags.MixAudio}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Request with neither payload",
                        async () =>
                        {
                            // The one generated shape nothing else exercises: no request data and
                            // no response. Pointing Preview at the scene already in Program makes
                            // the transition a no-op, so the check is safe to run.
                            GetStudioModeEnabledResponseData studio = await client
                                .Ui.GetStudioModeEnabledAsync(cancellationToken)
                                .ConfigureAwait(false);
                            if (!studio.StudioModeEnabled)
                            {
                                await client
                                    .Ui.SetStudioModeEnabledAsync(new(true), cancellationToken)
                                    .ConfigureAwait(false);
                            }

                            try
                            {
                                GetSceneListResponseData before = await client
                                    .Scenes.GetSceneListAsync(new(), cancellationToken)
                                    .ConfigureAwait(false);
                                string program = before.CurrentProgramSceneName!;

                                // Plain switch, not the waiting variant: OBS raises no
                                // CurrentPreviewSceneChanged when the preview is already that
                                // scene, which enabling Studio Mode has just made it.
                                await client
                                    .Scenes.SwitchPreviewSceneAsync(program, cancellationToken)
                                    .ConfigureAwait(false);

                                await client
                                    .Transitions.TriggerStudioModeTransitionAsync(cancellationToken)
                                    .ConfigureAwait(false);

                                GetSceneListResponseData after = await client
                                    .Scenes.GetSceneListAsync(new(), cancellationToken)
                                    .ConfigureAwait(false);

                                return (
                                    string.Equals(
                                        after.CurrentProgramSceneName,
                                        program,
                                        StringComparison.Ordinal
                                    ),
                                    $"transitioned, program still '{after.CurrentProgramSceneName}'"
                                );
                            }
                            finally
                            {
                                if (!studio.StudioModeEnabled)
                                {
                                    await client
                                        .Ui.SetStudioModeEnabledAsync(new(false), cancellationToken)
                                        .ConfigureAwait(false);
                                }
                            }
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Nested request object",
                        async () =>
                        {
                            // keyModifiers is the one generated nested record, a distinct shape
                            // from the flat request payloads. F13 is not bound by default, so the
                            // press does nothing.
                            await client
                                .General.TriggerHotkeyByKeySequenceAsync(
                                    new TriggerHotkeyByKeySequenceRequestData(
                                        keyId: "OBS_KEY_F13",
                                        keyModifiers: new TriggerHotkeyByKeySequenceRequestData_KeyModifiers(
                                            shift: false,
                                            control: true,
                                            alt: false,
                                            command: false
                                        )
                                    ),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            return (true, "nested keyModifiers accepted");
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Remaining stub types",
                        async () =>
                        {
                            // Monitor, output and transition stubs are generated the same way as
                            // the scene and input ones, so one read each covers the shape.
                            GetMonitorListResponseData monitors = await client
                                .Ui.GetMonitorListAsync(cancellationToken)
                                .ConfigureAwait(false);
                            GetSceneTransitionListResponseData transitions = await client
                                .Transitions.GetSceneTransitionListAsync(cancellationToken)
                                .ConfigureAwait(false);
                            bool ok =
                                monitors.Monitors.Count > 0
                                && transitions.Transitions.Count > 0
                                && outputs.Outputs.Count > 0
                                && monitors.Monitors[0].MonitorWidth > 0
                                && !string.IsNullOrEmpty(transitions.Transitions[0].TransitionName)
                                && !string.IsNullOrEmpty(outputs.Outputs[0].OutputName);

                            return (
                                ok,
                                $"{monitors.Monitors.Count} monitor(s) first {monitors.Monitors[0].MonitorWidth}px, "
                                    + $"{transitions.Transitions.Count} transition(s), {outputs.Outputs.Count} output(s)"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Classic handler and subscriptions",
                        async () =>
                        {
                            // The stream path is covered above; this is the += path, plus the
                            // negotiated subscription flags the client reports.
                            TaskCompletionSource seen = new(
                                TaskCreationOptions.RunContinuationsAsynchronously
                            );
                            string? observed = null;
                            void Handler(object? sender, SceneCreatedEventArgs e)
                            {
                                observed = e.EventData.SceneName;
                                _ = seen.TrySetResult();
                            }

                            client.Scenes.SceneCreated += Handler;
                            string probe = $"__obsws_handler_{Guid.NewGuid():N}"[..24];
                            try
                            {
                                await client
                                    .Scenes.CreateSceneAsync(
                                        new CreateSceneRequestData(sceneName: probe),
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false);
                                await seen
                                    .Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            finally
                            {
                                client.Scenes.SceneCreated -= Handler;
                                await client
                                    .Scenes.RemoveSceneAsync(
                                        new RemoveSceneRequestData(sceneName: probe),
                                        CancellationToken.None
                                    )
                                    .ConfigureAwait(false);
                            }

                            EventSubscription? subs = client.CurrentEventSubscriptions;

                            return (
                                string.Equals(observed, probe, StringComparison.Ordinal)
                                    && subs is not null
                                    && subs.Value.HasFlag(EventSubscription.Scenes),
                                $"handler saw '{observed}', subscriptions {subs}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Every way of sending a request",
                        async () =>
                        {
                            // One request, reached six ways, so no path is left unexercised.
                            // 1. The generated request on its category group.
                            GetVersionResponseData viaGroup = await client
                                .General.GetVersionAsync(cancellationToken)
                                .ConfigureAwait(false);

                            // 2. The low level typed call, for a reference type response.
                            GetVersionResponseData? viaCall = await client
                                .CallAsync<GetVersionResponseData>(
                                    "GetVersion",
                                    null,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            // 3. The low level untyped call, for a value type response.
                            JsonElement? viaValue = await client
                                .CallAsyncValue<JsonElement>(
                                    "GetVersion",
                                    null,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);
                            string? viaRawJson = viaValue?.GetProperty("obsVersion").GetString();

                            // 4. A hand built JsonElement as the request payload.
                            using JsonDocument requestBody = JsonDocument.Parse(
                                $$"""{"sceneName":"{{sceneName}}"}"""
                            );
                            JsonElement? viaJsonBody = await client
                                .CallAsyncValue<JsonElement>(
                                    "GetSceneItemList",
                                    requestBody.RootElement,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);
                            int itemsViaJsonBody =
                                viaJsonBody?.GetProperty("sceneItems").GetArrayLength() ?? -1;

                            // 5. The typed batch builder.
                            ObsBatchBuilder builder = new();
                            BatchRef<GetVersionResponseData> batched = builder.General.GetVersion();
                            BatchResults built = await client
                                .CallBatchAsync(
                                    builder,
                                    executionType: RequestBatchExecutionType.SerialRealtime,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);
                            string viaBatch = built.Get(batched).ObsVersion;

                            // 6. A hand rolled batch item, with a JsonElement payload.
                            using JsonDocument batchBody = JsonDocument.Parse(
                                $$"""{"sceneName":"{{sceneName}}"}"""
                            );
                            List<RequestResponsePayload<object>> viaRawBatch = await client
                                .CallBatchAsync(
                                    [
                                        new BatchRequestItem("GetVersion", null),
                                        new BatchRequestItem(
                                            "GetSceneItemList",
                                            batchBody.RootElement
                                        ),
                                    ],
                                    executionType: RequestBatchExecutionType.SerialRealtime,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);
                            string? viaRawBatchVersion = viaRawBatch[0]
                                .GetData<GetVersionResponseData>()
                                ?.ObsVersion;

                            // 7. A consumer's own JsonSerializerContext, so a type this library
                            // has never heard of is sent without hand building a JsonElement.
                            JsonElement? viaConsumerContext = await client
                                .CallAsyncValue<JsonElement>(
                                    "GetSceneItemList",
                                    new ConsumerSceneRequest(sceneName),
                                    ExampleRequestContext.Default.ConsumerSceneRequest,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);
                            int itemsViaContext =
                                viaConsumerContext?.GetProperty("sceneItems").GetArrayLength()
                                ?? -1;

                            // And the one shape that is not supported, asserted as unsupported:
                            // an anonymous object has no metadata in the serializer context.
                            string anonymous;
                            try
                            {
                                _ = await client
                                    .CallAsyncValue<JsonElement>(
                                        "GetSceneItemList",
                                        new { sceneName },
                                        cancellationToken: cancellationToken
                                    )
                                    .ConfigureAwait(false);
                                anonymous = "unexpectedly accepted";
                            }
                            catch (ObsWebSocketSerializationException)
                            {
                                anonymous = "refused";
                            }

                            string expected = viaGroup.ObsVersion;
                            bool allAgree =
                                viaCall?.ObsVersion == expected
                                && viaRawJson == expected
                                && viaBatch == expected
                                && viaRawBatchVersion == expected
                                && itemsViaJsonBody >= 0
                                && itemsViaContext == itemsViaJsonBody
                                && anonymous == "refused";

                            return (
                                allAgree,
                                $"seven paths agree on {expected}, JsonElement body and consumer "
                                    + $"context both read {itemsViaJsonBody} item(s), anonymous "
                                    + $"object {anonymous}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Typed exception on a rejected request",
                        async () =>
                        {
                            try
                            {
                                _ = await client
                                    .SceneItems.GetSceneItemListAsync(
                                        new GetSceneItemListRequestData(
                                            sceneName: "__no_such_scene__"
                                        ),
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false);
                                return (false, "no exception");
                            }
                            catch (ObsWebSocketRequestException ex)
                            {
                                return (
                                    ex.StatusCode == RequestStatusCode.ResourceNotFound
                                        && ex.RequestType == "GetSceneItemList",
                                    $"{ex.RequestType} code {(int?)ex.StatusCode}"
                                );
                            }
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Output state helpers",
                        async () =>
                        {
                            bool recording = await client
                                .Record.IsRecordActiveAsync(cancellationToken)
                                .ConfigureAwait(false);
                            bool streaming = await client
                                .Stream.IsStreamActiveAsync(cancellationToken)
                                .ConfigureAwait(false);
                            bool virtualCam = await client
                                .Outputs.IsVirtualCamActiveAsync(cancellationToken)
                                .ConfigureAwait(false);

                            // Each helper has to agree with the request it wraps.
                            GetRecordStatusResponseData? recordStatus = await client
                                .Record.GetRecordStatusAsync(cancellationToken)
                                .ConfigureAwait(false);
                            GetStreamStatusResponseData? streamStatus = await client
                                .Stream.GetStreamStatusAsync(cancellationToken)
                                .ConfigureAwait(false);
                            GetVirtualCamStatusResponseData? camStatus = await client
                                .Outputs.GetVirtualCamStatusAsync(cancellationToken)
                                .ConfigureAwait(false);

                            bool agrees =
                                recording == recordStatus?.OutputActive
                                && streaming == streamStatus?.OutputActive
                                && virtualCam == camStatus?.OutputActive;

                            return (
                                agrees,
                                $"record={recording}, stream={streaming}, virtualCam={virtualCam}, agrees={agrees}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );
        }
        finally
        {
            // Always put OBS back the way it was found.
            try
            {
                if (!string.IsNullOrEmpty(originalScene))
                {
                    await client
                        .Scenes.SwitchProgramSceneAsync(
                            originalScene,
                            cancellationToken: CancellationToken.None
                        )
                        .ConfigureAwait(false);
                }

                if (inputCreated)
                {
                    await client
                        .Inputs.RemoveInputAsync(
                            new RemoveInputRequestData(inputName: inputName),
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
                }

                if (sceneCreated)
                {
                    await client
                        .Scenes.RemoveSceneAsync(
                            new RemoveSceneRequestData(sceneName: sceneName),
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
                }
            }
            catch (ObsWebSocketException ex)
            {
                results.Add(("Cleanup", false, ex.Message));
            }
        }

        return results;
    }

    internal static async Task<(string Label, bool Pass, string Detail)> TrySettingsCheckAsync(
        string label,
        Func<Task<(bool Pass, string Detail)>> action
    )
    {
        try
        {
            (bool pass, string detail) = await action().ConfigureAwait(false);
            return (label, pass, detail);
        }
        catch (Exception ex)
        {
            string msg = ex.Message.Length > 100 ? ex.Message[..100] : ex.Message;
            return (label, false, msg);
        }
    }

    /// <summary>
    /// Calls every read-only request in the protocol and reports the ones whose response could not
    /// be deserialized.
    /// </summary>
    /// <remarks>
    /// The targeted checks elsewhere cover behaviour; this covers surface. A response record that
    /// does not match what OBS sends is invisible until something reads it, and three of them were
    /// shipping. Requests OBS declines for the state of the machine (no replay buffer, no group,
    /// an input of the wrong kind) are reported as untested rather than as failures, so the count
    /// says how much of the surface was actually exercised.
    /// </remarks>
    /// <summary>
    /// Waits for OBS to apply a profile change, which it does after answering the request.
    /// </summary>
    internal static async Task<bool> WaitForProfileAsync(
        ObsWebSocketClient client,
        Func<GetProfileListResponseData, bool> applied,
        CancellationToken cancellationToken
    )
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            GetProfileListResponseData profiles = await client
                .Config.GetProfileListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (applied(profiles))
            {
                return true;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Settings for the fixtures' media source, pointing at the clip shipped next to the binary.
    /// </summary>
    /// <remarks>
    /// A media source with no file is never playing, and OBS answers the cursor requests with
    /// <c>604</c>, so they are sent but never exercised.
    /// </remarks>
    internal static JsonElement MediaFixtureSettings()
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "local_file",
                Path.Combine(AppContext.BaseDirectory, "fixtures", "media-fixture.mp4")
            );
            writer.WriteBoolean("is_local_file", true);
            writer.WriteBoolean("looping", true);
            writer.WriteEndObject();
            writer.Flush();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    internal static async Task<
        List<(string Label, bool Pass, string Detail)>
    > SweepEveryReadRequestAsync(
        ObsWebSocketClient client,
        ObsSourceKinds kinds,
        GetOutputListResponseData outputs,
        CancellationToken cancellationToken
    )
    {
        List<string> unreadable = [];
        List<string> untested = [];
        HashSet<string> read = new(StringComparer.Ordinal);

        async Task Probe(string name, Func<Task> call)
        {
            try
            {
                await call().ConfigureAwait(false);
                _ = read.Add(name);
            }
            catch (ObsWebSocketSerializationException ex)
            {
                unreadable.Add($"{name}: {ex.InnerException?.Message ?? ex.Message}");
            }
            catch (ObsWebSocketRequestException ex)
            {
                // OBS declined for the state of the machine, so the response shape was never
                // exercised. Not a defect, but not coverage either.
                untested.Add($"{name} ({ex.StatusCode})");
            }
        }

        // Discover targets, so the sweep needs no fixture of its own.
        GetSceneListResponseData scenes = await client
            .Scenes.GetSceneListAsync(new(), cancellationToken)
            .ConfigureAwait(false);
        string sceneName = scenes.CurrentProgramSceneName ?? scenes.Scenes[0].SceneName;

        GetInputListResponseData inputs = await client
            .Inputs.GetInputListAsync(new(), cancellationToken)
            .ConfigureAwait(false);
        string inputName = inputs.Inputs[0].InputName;

        GetInputKindListResponseData inputKinds = await client
            .Inputs.GetInputKindListAsync(new(), cancellationToken)
            .ConfigureAwait(false);
        string inputKind = inputKinds.InputKinds[0];

        GetSourceFilterKindListResponseData filterKinds = await client
            .Filters.GetSourceFilterKindListAsync(cancellationToken)
            .ConfigureAwait(false);
        string filterKind = filterKinds.SourceFilterKinds[0];

        string outputName = outputs.Outputs[0].OutputName;

        GetGroupListResponseData groups = await client
            .Scenes.GetGroupListAsync(cancellationToken)
            .ConfigureAwait(false);
        string? groupName = groups.Groups.Count > 0 ? groups.Groups[0] : null;

        // The discovery calls above are read requests too, and naming them here rather than
        // counting them keeps the total honest when the discovery changes.
        read.UnionWith([
            "GetSceneList",
            "GetInputList",
            "GetSceneItemList",
            "GetInputKindList",
            "GetSourceFilterKindList",
            "GetOutputList",
            "GetGroupList",
            "GetStudioModeEnabled",
        ]);

        // Four of these requests need OBS to be in a particular state, not just to be asked
        // nicely. Without the fixture they answer InvalidResourceState and the response shape is
        // never exercised, which is coverage the report would otherwise claim.
        string fixtureSuffix = Guid.NewGuid().ToString("N")[..8];
        string readScene = $"__obsws_rsweep_{fixtureSuffix}";
        string audioInput = $"__obsws_rsweep_audio_{fixtureSuffix}";
        string mediaInput = $"__obsws_rsweep_media_{fixtureSuffix}";

        GetStudioModeEnabledResponseData studioBefore = await client
            .Ui.GetStudioModeEnabledAsync(cancellationToken)
            .ConfigureAwait(false);

        await client
            .Scenes.CreateSceneAsync(new(sceneName: readScene), cancellationToken)
            .ConfigureAwait(false);
        await client
            .Inputs.CreateInputAsync(
                new(inputName: audioInput, inputKind: kinds.AudioCapture!, sceneName: readScene),
                cancellationToken
            )
            .ConfigureAwait(false);
        await client
            .Inputs.CreateInputAsync(
                new(
                    inputName: mediaInput,
                    inputKind: "ffmpeg_source",
                    inputSettings: MediaFixtureSettings(),
                    sceneName: readScene
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        await client
            .Filters.CreateSourceFilterAsync(
                new(
                    filterName: FixtureFilterName,
                    filterKind: "color_filter_v2",
                    sourceName: mediaInput
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!studioBefore.StudioModeEnabled)
        {
            await client
                .Ui.SetStudioModeEnabledAsync(new(true), cancellationToken)
                .ConfigureAwait(false);
        }

        // From the fixture, not from the program scene: a fresh OBS profile opens on an empty
        // scene, and the seven scene item requests then go unexercised.
        GetSceneItemListResponseData fixtureItems = await client
            .SceneItems.GetSceneItemListAsync(new(sceneName: readScene), cancellationToken)
            .ConfigureAwait(false);
        long sceneItemId = fixtureItems.SceneItems[0].SceneItemId;
        string itemSourceName = fixtureItems.SceneItems[0].SourceName;

        try
        {
            await Probe(
                    "GetCanvasList",
                    () => client.Canvases.GetCanvasListAsync(cancellationToken)
                )
                .ConfigureAwait(false);

            await Probe(
                    "GetPersistentData",
                    () =>
                        client.Config.GetPersistentDataAsync(
                            new(
                                realm: "OBS_WEBSOCKET_DATA_REALM_PROFILE",
                                slotName: "__obsws_sweep"
                            ),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetSceneCollectionList",
                    () => client.Config.GetSceneCollectionListAsync(cancellationToken)
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetProfileList",
                    () => client.Config.GetProfileListAsync(cancellationToken)
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetProfileParameter",
                    () =>
                        client.Config.GetProfileParameterAsync(
                            new(parameterCategory: "General", parameterName: "Name"),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetVideoSettings",
                    () => client.Config.GetVideoSettingsAsync(cancellationToken)
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetStreamServiceSettings",
                    () => client.Config.GetStreamServiceSettingsAsync(cancellationToken)
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetRecordDirectory",
                    () => client.Config.GetRecordDirectoryAsync(cancellationToken)
                )
                .ConfigureAwait(false);

            await Probe(
                    "GetSourceFilterDefaultSettings",
                    () =>
                        client.Filters.GetSourceFilterDefaultSettingsAsync(
                            new(filterKind: filterKind),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetSourceFilterList",
                    () =>
                        client.Filters.GetSourceFilterListAsync(
                            new(sourceName: mediaInput),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetSourceFilter",
                    () =>
                        client.Filters.GetSourceFilterAsync(
                            new(filterName: FixtureFilterName, sourceName: mediaInput),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            await Probe("GetVersion", () => client.General.GetVersionAsync(cancellationToken))
                .ConfigureAwait(false);
            await Probe("GetStats", () => client.General.GetStatsAsync(cancellationToken))
                .ConfigureAwait(false);
            await Probe("GetHotkeyList", () => client.General.GetHotkeyListAsync(cancellationToken))
                .ConfigureAwait(false);

            await Probe(
                    "GetSpecialInputs",
                    () => client.Inputs.GetSpecialInputsAsync(cancellationToken)
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetInputDefaultSettings",
                    () =>
                        client.Inputs.GetInputDefaultSettingsAsync(
                            new(inputKind: inputKind),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetInputSettings",
                    () =>
                        client.Inputs.GetInputSettingsAsync(
                            new(inputName: inputName),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetInputMute",
                    () =>
                        client.Inputs.GetInputMuteAsync(
                            new(inputName: audioInput),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetInputVolume",
                    () =>
                        client.Inputs.GetInputVolumeAsync(
                            new(inputName: audioInput),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetInputAudioBalance",
                    () =>
                        client.Inputs.GetInputAudioBalanceAsync(
                            new(inputName: audioInput),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetInputAudioSyncOffset",
                    () =>
                        client.Inputs.GetInputAudioSyncOffsetAsync(
                            new(inputName: audioInput),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetInputAudioMonitorType",
                    () =>
                        client.Inputs.GetInputAudioMonitorTypeAsync(
                            new(inputName: audioInput),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetInputAudioTracks",
                    () =>
                        client.Inputs.GetInputAudioTracksAsync(
                            new(inputName: audioInput),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetInputDeinterlaceMode",
                    () =>
                        client.Inputs.GetInputDeinterlaceModeAsync(
                            new(inputName: mediaInput),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetInputDeinterlaceFieldOrder",
                    () =>
                        client.Inputs.GetInputDeinterlaceFieldOrderAsync(
                            new(inputName: mediaInput),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetInputPropertiesListPropertyItems",
                    () =>
                        client.Inputs.GetInputPropertiesListPropertyItemsAsync(
                            new(propertyName: "device_id", inputName: audioInput),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            await Probe(
                    "GetMediaInputStatus",
                    () =>
                        client.MediaInputs.GetMediaInputStatusAsync(
                            new(inputName: mediaInput),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            await Probe(
                    "GetVirtualCamStatus",
                    () => client.Outputs.GetVirtualCamStatusAsync(cancellationToken)
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetReplayBufferStatus",
                    () => client.Outputs.GetReplayBufferStatusAsync(cancellationToken)
                )
                .ConfigureAwait(false);
            // GetLastReplayBufferReplay is left untested rather than prepared for. Starting and
            // stopping the replay buffer to save a clip crashed OBS: the dump lands in
            // GetOutputList, on obs_encoder_get_width against the encoder the buffer had just
            // freed. Enumerating outputs while one is being torn down is not something a client
            // can make safe, and this sweep is not worth an OBS restart per run.
            await Probe(
                    "GetLastReplayBufferReplay",
                    () => client.Outputs.GetLastReplayBufferReplayAsync(cancellationToken)
                )
                .ConfigureAwait(false);

            await Probe(
                    "GetOutputStatus",
                    () =>
                        client.Outputs.GetOutputStatusAsync(
                            new(outputName: outputName),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetOutputSettings",
                    () =>
                        client.Outputs.GetOutputSettingsAsync(
                            new(outputName: outputName),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            await Probe(
                    "GetRecordStatus",
                    () => client.Record.GetRecordStatusAsync(cancellationToken)
                )
                .ConfigureAwait(false);

            if (groupName is not null)
            {
                await Probe(
                        "GetGroupSceneItemList",
                        () =>
                            client.SceneItems.GetGroupSceneItemListAsync(
                                new(sceneName: groupName),
                                cancellationToken
                            )
                    )
                    .ConfigureAwait(false);
            }
            else
            {
                // There is no CreateGroup request in the protocol, so a group can only come from a
                // scene collection that already has one.
                untested.Add("GetGroupSceneItemList (no group; the protocol cannot create one)");
            }

            await Probe(
                    "GetSceneItemId",
                    () =>
                        client.SceneItems.GetSceneItemIdAsync(
                            new(sourceName: itemSourceName, sceneName: readScene),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetSceneItemSource",
                    () =>
                        client.SceneItems.GetSceneItemSourceAsync(
                            new(sceneItemId: sceneItemId, sceneName: readScene),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetSceneItemTransform",
                    () =>
                        client.SceneItems.GetSceneItemTransformAsync(
                            new(sceneItemId: sceneItemId, sceneName: readScene),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetSceneItemEnabled",
                    () =>
                        client.SceneItems.GetSceneItemEnabledAsync(
                            new(sceneItemId: sceneItemId, sceneName: readScene),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetSceneItemLocked",
                    () =>
                        client.SceneItems.GetSceneItemLockedAsync(
                            new(sceneItemId: sceneItemId, sceneName: readScene),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetSceneItemIndex",
                    () =>
                        client.SceneItems.GetSceneItemIndexAsync(
                            new(sceneItemId: sceneItemId, sceneName: readScene),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetSceneItemBlendMode",
                    () =>
                        client.SceneItems.GetSceneItemBlendModeAsync(
                            new(sceneItemId: sceneItemId, sceneName: readScene),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            await Probe(
                    "GetCurrentProgramScene",
                    () => client.Scenes.GetCurrentProgramSceneAsync(cancellationToken)
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetCurrentPreviewScene",
                    () => client.Scenes.GetCurrentPreviewSceneAsync(cancellationToken)
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetSceneSceneTransitionOverride",
                    () =>
                        client.Scenes.GetSceneSceneTransitionOverrideAsync(
                            new(sceneName: sceneName),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            await Probe(
                    "GetSourceActive",
                    () =>
                        client.Sources.GetSourceActiveAsync(
                            new(sourceName: inputName),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetSourceScreenshot",
                    () =>
                        client.Sources.GetSourceScreenshotAsync(
                            new(imageFormat: "png", sourceName: sceneName),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            await Probe(
                    "GetStreamStatus",
                    () => client.Stream.GetStreamStatusAsync(cancellationToken)
                )
                .ConfigureAwait(false);

            await Probe(
                    "GetTransitionKindList",
                    () => client.Transitions.GetTransitionKindListAsync(cancellationToken)
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetSceneTransitionList",
                    () => client.Transitions.GetSceneTransitionListAsync(cancellationToken)
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetCurrentSceneTransition",
                    () => client.Transitions.GetCurrentSceneTransitionAsync(cancellationToken)
                )
                .ConfigureAwait(false);
            await Probe(
                    "GetCurrentSceneTransitionCursor",
                    () => client.Transitions.GetCurrentSceneTransitionCursorAsync(cancellationToken)
                )
                .ConfigureAwait(false);

            await Probe(
                    "GetStudioModeEnabled",
                    () => client.Ui.GetStudioModeEnabledAsync(cancellationToken)
                )
                .ConfigureAwait(false);
            await Probe("GetMonitorList", () => client.Ui.GetMonitorListAsync(cancellationToken))
                .ConfigureAwait(false);
        }
        finally
        {
            if (!studioBefore.StudioModeEnabled)
            {
                await client
                    .Ui.SetStudioModeEnabledAsync(new(false), CancellationToken.None)
                    .ConfigureAwait(false);
            }

            foreach (string fixture in new[] { audioInput, mediaInput })
            {
                await client
                    .Inputs.RemoveInputAsync(new(inputName: fixture), CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await client
                .Scenes.RemoveSceneAsync(new(sceneName: readScene), CancellationToken.None)
                .ConfigureAwait(false);
        }

        return
        [
            (
                "Every read request deserializes",
                unreadable.Count == 0 && read.Count + untested.Count >= 60,
                unreadable.Count > 0 ? string.Join(" | ", unreadable.Take(3))
                : read.Count + untested.Count < 60
                    ? $"only {read.Count + untested.Count} of 60 accounted for; a probe is missing"
                : untested.Count == 0 ? $"all {read.Count} of 60 read"
                : $"{read.Count} of 60 read; untested: {string.Join(", ", untested)}"
            ),
        ];
    }

    /// <summary>
    /// Sends every write request that can be exercised without taking the machine somewhere it
    /// cannot come back from, and reports the ones that could not be serialized.
    /// </summary>
    /// <remarks>
    /// Most write requests answer with no payload, so what this covers is the request side. That
    /// is not a formality: <c>SetInputAudioTracks</c> could not be sent over MessagePack at all,
    /// for the same missing formatter that made <c>GetInputAudioTracks</c> unreadable.
    /// <para>
    /// Everything runs against a scene, an input and a filter this method creates and removes.
    /// Requests that can only touch global state read the current value and write it back, so the
    /// call is real and the setting is unchanged.
    /// </para>
    /// <para>
    /// Deliberately not sent, because the cost of running them is not a shape bug: anything that
    /// starts a stream, a recording, the replay buffer or the virtual camera; profile and scene
    /// collection switching, which reloads OBS underneath the run; the dialog and projector
    /// requests, which open windows; <c>TriggerHotkeyByName</c> and
    /// <c>PressInputPropertiesButton</c>, which do whatever the target happens to do;
    /// <c>CallVendorRequest</c>, which needs a plugin; and <c>Sleep</c>, which is batch only.
    /// </para>
    /// </remarks>
    internal static async Task<
        List<(string Label, bool Pass, string Detail)>
    > SweepEveryWriteRequestAsync(
        ObsWebSocketClient client,
        ObsSourceKinds kinds,
        GetOutputListResponseData outputs,
        CancellationToken cancellationToken
    )
    {
        List<string> unsendable = [];
        List<string> declined = [];
        int sent = 0;

        async Task Probe(string name, Func<Task> call)
        {
            try
            {
                await call().ConfigureAwait(false);
                sent++;
            }
            catch (ObsWebSocketSerializationException ex)
            {
                unsendable.Add($"{name}: {ex.InnerException?.Message ?? ex.Message}");
            }
            catch (ObsWebSocketRequestException ex)
            {
                declined.Add($"{name} ({ex.StatusCode})");
            }
        }

        string suffix = Guid.NewGuid().ToString("N")[..8];
        string sceneName = $"__obsws_wsweep_{suffix}";
        string renamedScene = $"{sceneName}_r";
        string inputName = $"__obsws_wsweep_in_{suffix}";
        string renamedInput = $"{inputName}_r";
        string filterName = "__obsws_wsweep_filter";
        string renamedFilter = $"{filterName}_r";

        GetSceneListResponseData scenesBefore = await client
            .Scenes.GetSceneListAsync(new(), cancellationToken)
            .ConfigureAwait(false);
        string originalProgramScene = scenesBefore.CurrentProgramSceneName!;

        string outputName = outputs.Outputs[0].OutputName;

        // ── Fixture ──────────────────────────────────────────────────────────
        await Probe(
                "CreateScene",
                () => client.Scenes.CreateSceneAsync(new(sceneName: sceneName), cancellationToken)
            )
            .ConfigureAwait(false);
        await Probe(
                "CreateInput",
                () =>
                    client.Inputs.CreateInputAsync(
                        new(
                            inputName: inputName,
                            inputKind: "color_source_v3",
                            sceneName: sceneName
                        ),
                        cancellationToken
                    )
            )
            .ConfigureAwait(false);

        // A colour source has no audio and cannot be deinterlaced. Pointing every audio and media
        // request at one is how six read requests came back declined rather than exercised.
        string audioInput = $"__obsws_wsweep_audio_{suffix}";
        string mediaInput = $"__obsws_wsweep_media_{suffix}";
        await Probe(
                "CreateInput (audio)",
                () =>
                    client.Inputs.CreateInputAsync(
                        new(
                            inputName: audioInput,
                            inputKind: kinds.AudioCapture!,
                            sceneName: sceneName
                        ),
                        cancellationToken
                    )
            )
            .ConfigureAwait(false);
        await Probe(
                "CreateInput (media)",
                () =>
                    client.Inputs.CreateInputAsync(
                        new(
                            inputName: mediaInput,
                            inputKind: "ffmpeg_source",
                            inputSettings: MediaFixtureSettings(),
                            sceneName: sceneName
                        ),
                        cancellationToken
                    )
            )
            .ConfigureAwait(false);

        try
        {
            GetSceneItemListResponseData fixtureItems = await client
                .SceneItems.GetSceneItemListAsync(new(sceneName: sceneName), cancellationToken)
                .ConfigureAwait(false);
            long itemId = fixtureItems.SceneItems[0].SceneItemId;

            // ── Scenes ───────────────────────────────────────────────────────
            await Probe(
                    "SetCurrentProgramScene",
                    () =>
                        client.Scenes.SetCurrentProgramSceneAsync(
                            new(sceneName: sceneName),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "SetSceneSceneTransitionOverride",
                    () =>
                        client.Scenes.SetSceneSceneTransitionOverrideAsync(
                            new(sceneName: sceneName, transitionName: "Fade"),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "SetSceneName",
                    () =>
                        client.Scenes.SetSceneNameAsync(
                            new(newSceneName: renamedScene, sceneName: sceneName),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            sceneName = renamedScene;

            // ── Scene items ──────────────────────────────────────────────────
            await Probe(
                    "SetSceneItemEnabled",
                    () =>
                        client.SceneItems.SetSceneItemEnabledAsync(
                            new(sceneItemId: itemId, sceneItemEnabled: true, sceneName: sceneName),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "SetSceneItemLocked",
                    () =>
                        client.SceneItems.SetSceneItemLockedAsync(
                            new(sceneItemId: itemId, sceneItemLocked: false, sceneName: sceneName),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "SetSceneItemIndex",
                    () =>
                        client.SceneItems.SetSceneItemIndexAsync(
                            new(sceneItemId: itemId, sceneItemIndex: 0, sceneName: sceneName),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "SetSceneItemBlendMode",
                    () =>
                        client.SceneItems.SetSceneItemBlendModeAsync(
                            new(
                                sceneItemId: itemId,
                                sceneItemBlendMode: "OBS_BLEND_NORMAL",
                                sceneName: sceneName
                            ),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            // A whole transform read back from OBS is refused: it carries the source dimensions
            // OBS computes and will not accept back. A caller sets the fields they mean to.
            SceneItemTransformPatchStub transformPatch = new()
            {
                PositionX = 0.0,
                PositionY = 0.0,
                Rotation = 0.0,
            };
            await Probe(
                    "SetSceneItemTransform",
                    () =>
                        client.SceneItems.SetSceneItemTransformAsync(
                            new(
                                sceneItemId: itemId,
                                sceneItemTransform: transformPatch,
                                sceneName: sceneName
                            ),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            long? addedItemId = null;
            try
            {
                CreateSceneItemResponseData added = await client
                    .SceneItems.CreateSceneItemAsync(
                        new(sceneName: sceneName, sourceName: inputName),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                addedItemId = added.SceneItemId;
                sent++;
            }
            catch (ObsWebSocketRequestException ex)
            {
                declined.Add($"CreateSceneItem ({ex.StatusCode})");
            }

            long? duplicatedItemId = null;
            try
            {
                DuplicateSceneItemResponseData duplicated = await client
                    .SceneItems.DuplicateSceneItemAsync(
                        new(sceneItemId: itemId, sceneName: sceneName),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                duplicatedItemId = duplicated.SceneItemId;
                sent++;
            }
            catch (ObsWebSocketRequestException ex)
            {
                declined.Add($"DuplicateSceneItem ({ex.StatusCode})");
            }

            // Only the two this sweep added. Removing the last scene item that references an input
            // destroys the input, which took the audio and media fixtures with it.
            foreach (long extra in new[] { addedItemId, duplicatedItemId }.OfType<long>())
            {
                await Probe(
                        "RemoveSceneItem",
                        () =>
                            client.SceneItems.RemoveSceneItemAsync(
                                new(sceneItemId: extra, sceneName: sceneName),
                                cancellationToken
                            )
                    )
                    .ConfigureAwait(false);
            }

            // ── Inputs ───────────────────────────────────────────────────────
            await Probe(
                    "SetInputSettings",
                    () =>
                        client.Inputs.SetInputSettingsAsync(
                            inputName,
                            new ColorSourceSettings { Width = 320, Height = 180 },
                            cancellationToken: cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "SetInputMute",
                    () =>
                        client.Inputs.SetInputMuteAsync(
                            new(inputMuted: false, inputName: audioInput),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "ToggleInputMute",
                    () =>
                        client.Inputs.ToggleInputMuteAsync(
                            new(inputName: audioInput),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "SetInputVolume",
                    () =>
                        client.Inputs.SetInputVolumeAsync(
                            new(inputName: audioInput, inputVolumeMul: 1.0),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "SetInputAudioBalance",
                    () =>
                        client.Inputs.SetInputAudioBalanceAsync(
                            new(inputAudioBalance: 0.5, inputName: audioInput),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "SetInputAudioSyncOffset",
                    () =>
                        client.Inputs.SetInputAudioSyncOffsetAsync(
                            new(inputAudioSyncOffset: 0, inputName: audioInput),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "SetInputAudioMonitorType",
                    () =>
                        client.Inputs.SetInputAudioMonitorTypeAsync(
                            new(monitorType: "OBS_MONITORING_TYPE_NONE", inputName: audioInput),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            // The request that could not be sent over MessagePack at all.
            GetInputAudioTracksResponseData? tracks = null;
            try
            {
                tracks = await client
                    .Inputs.GetInputAudioTracksAsync(new(inputName: audioInput), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ObsWebSocketRequestException)
            {
                // Deliberately not logged: the write below is probed either way and reports what
                // OBS said about it.
            }
            await Probe(
                    "SetInputAudioTracks",
                    () =>
                        client.Inputs.SetInputAudioTracksAsync(
                            new(
                                inputAudioTracks: tracks?.InputAudioTracks
                                    ?? new Dictionary<string, bool> { ["1"] = true },
                                inputName: audioInput
                            ),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            await Probe(
                    "SetInputDeinterlaceMode",
                    () =>
                        client.Inputs.SetInputDeinterlaceModeAsync(
                            new(
                                inputDeinterlaceMode: "OBS_DEINTERLACE_MODE_DISABLE",
                                inputName: mediaInput
                            ),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "SetInputDeinterlaceFieldOrder",
                    () =>
                        client.Inputs.SetInputDeinterlaceFieldOrderAsync(
                            new(
                                inputDeinterlaceFieldOrder: "OBS_DEINTERLACE_FIELD_ORDER_TOP",
                                inputName: mediaInput
                            ),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "SetInputName",
                    () =>
                        client.Inputs.SetInputNameAsync(
                            new(newInputName: renamedInput, inputName: inputName),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            inputName = renamedInput;

            // ── Media inputs ─────────────────────────────────────────────────
            // The cursor requests only reach their own answer while the clip is playing.
            await Probe(
                    "TriggerMediaInputAction (play)",
                    () =>
                        client.MediaInputs.TriggerMediaInputActionAsync(
                            new(mediaAction: MediaInputAction.Play, inputName: mediaInput),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            // Playback starts a moment after OBS accepts the action, and the cursor requests are
            // answered only once it has.
            for (int attempt = 0; attempt < 20; attempt++)
            {
                GetMediaInputStatusResponseData status = await client
                    .MediaInputs.GetMediaInputStatusAsync(
                        new(inputName: mediaInput),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (status.MediaState is "OBS_MEDIA_STATE_PLAYING" or "OBS_MEDIA_STATE_PAUSED")
                {
                    break;
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            await Probe(
                    "SetMediaInputCursor",
                    () =>
                        client.MediaInputs.SetMediaInputCursorAsync(
                            new(mediaCursor: 0, inputName: mediaInput),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "OffsetMediaInputCursor",
                    () =>
                        client.MediaInputs.OffsetMediaInputCursorAsync(
                            new(mediaCursorOffset: 0, inputName: mediaInput),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "TriggerMediaInputAction",
                    () =>
                        client.MediaInputs.TriggerMediaInputActionAsync(
                            new(mediaAction: MediaInputAction.Stop, inputName: mediaInput),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "PressInputPropertiesButton",
                    () =>
                        client.Inputs.PressInputPropertiesButtonAsync(
                            new(propertyName: "__obsws_no_such_button", inputName: inputName),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            // ── Filters ──────────────────────────────────────────────────────
            await Probe(
                    "CreateSourceFilter",
                    () =>
                        client.Filters.CreateSourceFilterAsync(
                            new(
                                filterName: filterName,
                                filterKind: "color_filter_v2",
                                sourceName: inputName
                            ),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "SetSourceFilterEnabled",
                    () =>
                        client.Filters.SetSourceFilterEnabledAsync(
                            new(filterName: filterName, filterEnabled: true, sourceName: inputName),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "SetSourceFilterIndex",
                    () =>
                        client.Filters.SetSourceFilterIndexAsync(
                            new(filterName: filterName, filterIndex: 0, sourceName: inputName),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "SetSourceFilterSettings",
                    () =>
                        client.Filters.SetSourceFilterSettingsAsync(
                            inputName,
                            filterName,
                            new ColorCorrectionFilterSettings { Opacity = 1.0 },
                            cancellationToken: cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "SetSourceFilterName",
                    () =>
                        client.Filters.SetSourceFilterNameAsync(
                            new(
                                filterName: filterName,
                                newFilterName: renamedFilter,
                                sourceName: inputName
                            ),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "RemoveSourceFilter",
                    () =>
                        client.Filters.RemoveSourceFilterAsync(
                            new(filterName: renamedFilter, sourceName: inputName),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            // ── General ──────────────────────────────────────────────────────
            using JsonDocument custom = JsonDocument.Parse("""{"obswsSweep":true}""");
            await Probe(
                    "BroadcastCustomEvent",
                    () =>
                        client.General.BroadcastCustomEventAsync(
                            new(eventData: custom.RootElement.Clone()),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "TriggerHotkeyByKeySequence",
                    () =>
                        client.General.TriggerHotkeyByKeySequenceAsync(
                            new(
                                keyId: "OBS_KEY_F13",
                                keyModifiers: new TriggerHotkeyByKeySequenceRequestData_KeyModifiers(
                                    shift: false,
                                    control: false,
                                    alt: false,
                                    command: false
                                )
                            ),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            // ── Config, every one a read then a write of the same value ──────
            await Probe(
                    "SetPersistentData",
                    () =>
                        client.Config.SetPersistentDataAsync(
                            new(
                                realm: "OBS_WEBSOCKET_DATA_REALM_PROFILE",
                                slotName: "__obsws_sweep",
                                slotValue: JsonDocument.Parse("\"probe\"").RootElement.Clone()
                            ),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            // CreateProfile activates the profile it creates, and removing the active one leaves
            // OBS writing into a directory it has deleted (#42). Switch back first, and wait for
            // each step: OBS answers these before it applies them.
            string sweepProfile = $"__obsws_wsweep_profile_{suffix}";
            GetProfileListResponseData profilesBefore = await client
                .Config.GetProfileListAsync(cancellationToken)
                .ConfigureAwait(false);
            string originalProfile = profilesBefore.CurrentProfileName!;

            await Probe(
                    "CreateProfile",
                    () =>
                        client.Config.CreateProfileAsync(
                            new(profileName: sweepProfile),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            if (
                await WaitForProfileAsync(
                        client,
                        list => list.CurrentProfileName == sweepProfile,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            )
            {
                await Probe(
                        "SetCurrentProfile",
                        () =>
                            client.Config.SetCurrentProfileAsync(
                                new(profileName: originalProfile),
                                cancellationToken
                            )
                    )
                    .ConfigureAwait(false);

                if (
                    await WaitForProfileAsync(
                            client,
                            list => list.CurrentProfileName == originalProfile,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                )
                {
                    await Probe(
                            "RemoveProfile",
                            () =>
                                client.Config.RemoveProfileAsync(
                                    new(profileName: sweepProfile),
                                    cancellationToken
                                )
                        )
                        .ConfigureAwait(false);
                    _ = await WaitForProfileAsync(
                            client,
                            list => !list.Profiles.Contains(sweepProfile),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
                else
                {
                    declined.Add("RemoveProfile (not sent: OBS stayed on the sweep's profile)");
                }
            }
            else
            {
                declined.Add(
                    "SetCurrentProfile, RemoveProfile (not sent: the new profile never became active)"
                );
            }

            GetProfileParameterResponseData profileParameter = await client
                .Config.GetProfileParameterAsync(
                    new(parameterCategory: "Output", parameterName: "Mode"),
                    cancellationToken
                )
                .ConfigureAwait(false);
            await Probe(
                    "SetProfileParameter",
                    () =>
                        client.Config.SetProfileParameterAsync(
                            new(
                                parameterCategory: "Output",
                                parameterName: "Mode",
                                parameterValue: profileParameter.ParameterValue
                                    ?? profileParameter.DefaultParameterValue
                                    ?? "Simple"
                            ),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            GetVideoSettingsResponseData video = await client
                .Config.GetVideoSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
            await Probe(
                    "SetVideoSettings",
                    () =>
                        client.Config.SetVideoSettingsAsync(
                            new(
                                fpsNumerator: video.FpsNumerator,
                                fpsDenominator: video.FpsDenominator,
                                baseWidth: video.BaseWidth,
                                baseHeight: video.BaseHeight,
                                outputWidth: video.OutputWidth,
                                outputHeight: video.OutputHeight
                            ),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            GetRecordDirectoryResponseData recordDirectory = await client
                .Config.GetRecordDirectoryAsync(cancellationToken)
                .ConfigureAwait(false);
            await Probe(
                    "SetRecordDirectory",
                    () =>
                        client.Config.SetRecordDirectoryAsync(
                            new(recordDirectory: recordDirectory.RecordDirectory),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            GetStreamServiceSettingsResponseData streamService = await client
                .Config.GetStreamServiceSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
            // Written back unchanged where OBS has settings. A fresh install has none and OBS
            // rejects an empty object, so the request would never be exercised; a placeholder for
            // the service it already reports keeps the check real and overwrites nothing.
            JsonElement serviceSettings =
                streamService.StreamServiceSettings is JsonElement existing
                && existing.ValueKind == JsonValueKind.Object
                && existing.EnumerateObject().Any()
                    ? existing
                    : JsonDocument
                        .Parse("""{"server":"auto","service":"Twitch"}""")
                        .RootElement.Clone();
            await Probe(
                    "SetStreamServiceSettings",
                    () =>
                        client.Config.SetStreamServiceSettingsAsync(
                            new(
                                streamServiceType: streamService.StreamServiceType,
                                streamServiceSettings: serviceSettings
                            ),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            // ── Transitions, same read then write ────────────────────────────
            GetCurrentSceneTransitionResponseData transition = await client
                .Transitions.GetCurrentSceneTransitionAsync(cancellationToken)
                .ConfigureAwait(false);
            await Probe(
                    "SetCurrentSceneTransition",
                    () =>
                        client.Transitions.SetCurrentSceneTransitionAsync(
                            new(transitionName: transition.TransitionName),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "SetCurrentSceneTransitionDuration",
                    () =>
                        client.Transitions.SetCurrentSceneTransitionDurationAsync(
                            new(transitionDuration: transition.TransitionDuration ?? 300),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            // Fade and Cut have nothing to configure and OBS answers 606. Select a configurable
            // transition in OBS to exercise this one; the CI scene collection ships with one.
            if (transition.TransitionConfigurable)
            {
                await Probe(
                        "SetCurrentSceneTransitionSettings",
                        () =>
                            client.Transitions.SetCurrentSceneTransitionSettingsAsync(
                                new(
                                    transitionSettings: transition.TransitionSettings
                                        ?? JsonDocument.Parse("{}").RootElement.Clone()
                                ),
                                cancellationToken
                            )
                    )
                    .ConfigureAwait(false);
            }
            else
            {
                declined.Add(
                    $"SetCurrentSceneTransitionSettings (not sent: '{transition.TransitionName}' has nothing to configure)"
                );
            }

            // ── UI and studio mode, restored below ───────────────────────────
            GetStudioModeEnabledResponseData studio = await client
                .Ui.GetStudioModeEnabledAsync(cancellationToken)
                .ConfigureAwait(false);
            await Probe(
                    "SetStudioModeEnabled",
                    () => client.Ui.SetStudioModeEnabledAsync(new(true), cancellationToken)
                )
                .ConfigureAwait(false);
            await Probe(
                    "SetCurrentPreviewScene",
                    () =>
                        client.Scenes.SetCurrentPreviewSceneAsync(
                            new(sceneName: sceneName),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "SetTBarPosition",
                    () =>
                        client.Transitions.SetTBarPositionAsync(
                            new(position: 0.0, release: true),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "TriggerStudioModeTransition",
                    () => client.Transitions.TriggerStudioModeTransitionAsync(cancellationToken)
                )
                .ConfigureAwait(false);
            if (!studio.StudioModeEnabled)
            {
                await client
                    .Ui.SetStudioModeEnabledAsync(new(false), CancellationToken.None)
                    .ConfigureAwait(false);
            }

            // ── Requests OBS should decline in this state, sent anyway so the
            //    request side is still exercised ───────────────────────────────
            await Probe("StopRecord", () => client.Record.StopRecordAsync(cancellationToken))
                .ConfigureAwait(false);
            await Probe(
                    "ToggleRecordPause",
                    () => client.Record.ToggleRecordPauseAsync(cancellationToken)
                )
                .ConfigureAwait(false);
            await Probe("PauseRecord", () => client.Record.PauseRecordAsync(cancellationToken))
                .ConfigureAwait(false);
            await Probe("ResumeRecord", () => client.Record.ResumeRecordAsync(cancellationToken))
                .ConfigureAwait(false);
            await Probe(
                    "SplitRecordFile",
                    () => client.Record.SplitRecordFileAsync(cancellationToken)
                )
                .ConfigureAwait(false);
            await Probe(
                    "CreateRecordChapter",
                    () =>
                        client.Record.CreateRecordChapterAsync(
                            new(chapterName: "__obsws_sweep"),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe("StopStream", () => client.Stream.StopStreamAsync(cancellationToken))
                .ConfigureAwait(false);
            await Probe(
                    "SendStreamCaption",
                    () =>
                        client.Stream.SendStreamCaptionAsync(
                            new(captionText: "__obsws_sweep"),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            await Probe(
                    "StopReplayBuffer",
                    () => client.Outputs.StopReplayBufferAsync(cancellationToken)
                )
                .ConfigureAwait(false);
            await Probe(
                    "SaveReplayBuffer",
                    () => client.Outputs.SaveReplayBufferAsync(cancellationToken)
                )
                .ConfigureAwait(false);

            await Probe(
                    "StopOutput",
                    () =>
                        client.Outputs.StopOutputAsync(
                            new(outputName: outputName),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            // SetOutputSettings is deliberately not sent. Writing settings back to a real
            // output wedged the output subsystem: GetOutputList then timed out for the rest of
            // the session, in checks that had nothing to do with the sweep.
            declined.Add("SetOutputSettings (not sent: wedges the output subsystem)");

            // ── Sources ──────────────────────────────────────────────────────
            string screenshotPath = Path.Combine(Path.GetTempPath(), $"obsws_sweep_{suffix}.png");
            await Probe(
                    "SaveSourceScreenshot",
                    () =>
                        client.Sources.SaveSourceScreenshotAsync(
                            new(
                                imageFormat: "png",
                                imageFilePath: screenshotPath,
                                sourceName: sceneName
                            ),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);
            if (File.Exists(screenshotPath))
            {
                File.Delete(screenshotPath);
            }
        }
        finally
        {
            await client
                .Scenes.SetCurrentProgramSceneAsync(
                    new(sceneName: originalProgramScene),
                    CancellationToken.None
                )
                .ConfigureAwait(false);

            foreach (string fixture in new[] { inputName, audioInput, mediaInput })
            {
                await Probe(
                        "RemoveInput",
                        () =>
                            client.Inputs.RemoveInputAsync(
                                new(inputName: fixture),
                                CancellationToken.None
                            )
                    )
                    .ConfigureAwait(false);
            }
            await Probe(
                    "RemoveScene",
                    () =>
                        client.Scenes.RemoveSceneAsync(
                            new(sceneName: sceneName),
                            CancellationToken.None
                        )
                )
                .ConfigureAwait(false);
        }

        return
        [
            (
                "Every write request serializes",
                unsendable.Count == 0,
                unsendable.Count == 0
                    // A decline still proves the request serialized and reached OBS, which is
                    // what this sweep covers; only unsendable is a defect.
                    ? $"{sent + declined.Count} serialized ({sent} accepted, {declined.Count} "
                        + $"declined for machine state): {string.Join(", ", declined)}"
                    : string.Join(" | ", unsendable.Take(3))
            ),
        ];
    }

    /// <summary>
    /// The settings-mode checks themselves, against fixtures the caller creates and removes.
    /// </summary>
    internal static async Task<
        List<(string Label, bool Pass, string Detail)>
    > RunSettingsModeChecksAsync(
        ObsWebSocketClient client,
        string? browserInputName,
        string? filterSourceName,
        string? gainFilterName,
        CancellationToken cancellationToken
    )
    {
        List<(string Label, bool Pass, string Detail)> results = [];

        if (string.IsNullOrEmpty(browserInputName))
        {
            results.Add(
                ("InputSettings [all modes]", false, "fixture browser source was not created")
            );
        }
        else
        {
            // Mode 1: raw JsonElement via protocol-level call
            results.Add(
                await TrySettingsCheckAsync(
                    "InputSettings Mode1 (raw JsonElement)",
                    async () =>
                    {
                        GetInputSettingsResponseData? r = await client.Inputs.GetInputSettingsAsync(
                            new GetInputSettingsRequestData(browserInputName),
                            cancellationToken
                        );
                        if (r?.InputSettings is not JsonElement el)
                        {
                            return (false, "null InputSettings in response");
                        }

                        await client.Inputs.SetInputSettingsAsync(
                            new SetInputSettingsRequestData(
                                el,
                                inputName: browserInputName,
                                overlay: true
                            ),
                            cancellationToken
                        );
                        string url = el.TryGetProperty("url", out JsonElement p)
                            ? p.GetString() ?? "(no url)"
                            : "(no url key)";
                        return (true, $"'{browserInputName}' url={url}");
                    }
                )
            );

            // Mode 2: library-registered type via implicit GetTypeInfo lookup
            results.Add(
                await TrySettingsCheckAsync(
                    "InputSettings Mode2 (BrowserSourceSettings)",
                    async () =>
                    {
                        BrowserSourceSettings? s =
                            await client.Inputs.GetInputSettingsAsync<BrowserSourceSettings>(
                                browserInputName,
                                cancellationToken
                            );
                        if (s is null)
                        {
                            return (false, "null result");
                        }

                        await client.Inputs.SetInputSettingsAsync(
                            browserInputName,
                            s,
                            overlay: true,
                            cancellationToken: cancellationToken
                        );
                        return (true, $"'{browserInputName}' url={s.Url ?? "(null)"}");
                    }
                )
            );

            // Mode 3: consumer-defined type with explicit JsonTypeInfo<T>
            results.Add(
                await TrySettingsCheckAsync(
                    "InputSettings Mode3 (consumer JsonTypeInfo)",
                    async () =>
                    {
                        JsonTypeInfo<WorkerBrowserUrlSettings> typeInfo = WorkerSettingsJsonContext
                            .Default
                            .WorkerBrowserUrlSettings;
                        WorkerBrowserUrlSettings? s = await client.Inputs.GetInputSettingsAsync(
                            browserInputName,
                            typeInfo,
                            cancellationToken
                        );
                        if (s is null)
                        {
                            return (false, "null result");
                        }

                        await client.Inputs.SetInputSettingsAsync(
                            browserInputName,
                            s,
                            typeInfo,
                            overlay: true,
                            cancellationToken: cancellationToken
                        );
                        return (true, $"'{browserInputName}' url={s.Url ?? "(null)"}");
                    }
                )
            );
        }

        // ── FilterSettings ────────────────────────────────────────────────────
        if (string.IsNullOrEmpty(filterSourceName) || string.IsNullOrEmpty(gainFilterName))
        {
            results.Add(
                ("FilterSettings [all modes]", false, "fixture gain filter was not created")
            );
        }
        else
        {
            // Mode 1: raw JsonElement via protocol-level call
            results.Add(
                await TrySettingsCheckAsync(
                    "FilterSettings Mode1 (raw JsonElement)",
                    async () =>
                    {
                        GetSourceFilterResponseData? r = await client.Filters.GetSourceFilterAsync(
                            new GetSourceFilterRequestData
                            {
                                SourceName = filterSourceName,
                                FilterName = gainFilterName,
                            },
                            cancellationToken
                        );
                        if (r?.FilterSettings is not JsonElement el)
                        {
                            return (false, "null FilterSettings in response");
                        }

                        await client.Filters.SetSourceFilterSettingsAsync(
                            new SetSourceFilterSettingsRequestData(
                                gainFilterName,
                                el,
                                sourceName: filterSourceName,
                                overlay: true
                            ),
                            cancellationToken
                        );
                        string db = el.TryGetProperty("db", out JsonElement p)
                            ? p.GetDouble().ToString("F1")
                            : "(no db key)";
                        return (true, $"'{filterSourceName}/{gainFilterName}' db={db}");
                    }
                )
            );

            // Mode 2: library-registered type via implicit GetTypeInfo lookup
            results.Add(
                await TrySettingsCheckAsync(
                    "FilterSettings Mode2 (GainFilterSettings)",
                    async () =>
                    {
                        GainFilterSettings? s =
                            await client.Filters.GetSourceFilterSettingsAsync<GainFilterSettings>(
                                filterSourceName,
                                gainFilterName,
                                cancellationToken
                            );
                        if (s is null)
                        {
                            return (false, "null result");
                        }

                        await client.Filters.SetSourceFilterSettingsAsync(
                            filterSourceName,
                            gainFilterName,
                            s,
                            overlay: true,
                            cancellationToken: cancellationToken
                        );
                        return (
                            true,
                            $"'{filterSourceName}/{gainFilterName}' db={s.Db?.ToString("F1") ?? "(null)"}"
                        );
                    }
                )
            );

            // Mode 3: consumer-defined type with explicit JsonTypeInfo<T>
            results.Add(
                await TrySettingsCheckAsync(
                    "FilterSettings Mode3 (consumer JsonTypeInfo)",
                    async () =>
                    {
                        JsonTypeInfo<WorkerGainDbSettings> typeInfo = WorkerSettingsJsonContext
                            .Default
                            .WorkerGainDbSettings;
                        WorkerGainDbSettings? s = await client.Filters.GetSourceFilterSettingsAsync(
                            filterSourceName,
                            gainFilterName,
                            typeInfo,
                            cancellationToken
                        );
                        if (s is null)
                        {
                            return (false, "null result");
                        }

                        await client.Filters.SetSourceFilterSettingsAsync(
                            filterSourceName,
                            gainFilterName,
                            s,
                            typeInfo,
                            overlay: true,
                            cancellationToken: cancellationToken
                        );
                        return (
                            true,
                            $"'{filterSourceName}/{gainFilterName}' db={s.Db?.ToString("F1") ?? "(null)"}"
                        );
                    }
                )
            );
        }

        return results;
    }

    /// <summary>
    /// Runs an action expected to throw, and hands back the exception it threw.
    /// </summary>
    internal static async Task<TException> ExpectThrowAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException expected)
        {
            return expected;
        }

        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name}, but the call succeeded."
        );
    }
}

/// <summary>
/// A request payload this library has no metadata for, sent with the consumer's own context.
/// </summary>
/// <param name="SceneName">The scene to list items for.</param>
internal sealed record ConsumerSceneRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("sceneName")] string SceneName
);

/// <summary>
/// The consumer side serializer context, the AOT safe way to describe a payload the library does
/// not model.
/// </summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(ConsumerSceneRequest))]
internal sealed partial class ExampleRequestContext
    : System.Text.Json.Serialization.JsonSerializerContext;

// Settings types a consumer would define, deliberately absent from the library's context.────────────────────────
// These are NOT registered in the library's ObsWebSocketSettingsJsonContext.
// They represent what a consumer app would define to map only the fields it cares about,
// using its own JsonSerializerContext and passing an explicit JsonTypeInfo<T> to the helpers.

internal sealed record WorkerBrowserUrlSettings(
    [property: JsonPropertyName("url")] string? Url = null
);

internal sealed record WorkerGainDbSettings([property: JsonPropertyName("db")] double? Db = null);

[JsonSerializable(typeof(WorkerBrowserUrlSettings))]
[JsonSerializable(typeof(WorkerGainDbSettings))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
)]
internal sealed partial class WorkerSettingsJsonContext : JsonSerializerContext { }
