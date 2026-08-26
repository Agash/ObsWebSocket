using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Common.InputSettings;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Tests;

[JsonSerializable(typeof(OverlaySettings))]
internal partial class MyContext : JsonSerializerContext { }

internal sealed record OverlaySettings(
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("css")] string? Css = null
);

// Compile-only check that the call shapes printed in README.md are real.
// Never executed; if this file compiles, the README examples compile.
internal static class ReadmeCompileCheck
{
    internal static async Task TextSourceAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        await client.Inputs.SetInputTextAsync("NewsTicker", "Breaking: Live now!", ct);

        var settings = new TextGdiPlusInputSettings(Text: "Breaking: Live now!", WordWrap: true);
        await client.Inputs.SetInputSettingsAsync("NewsTicker", settings, cancellationToken: ct);
    }

    internal static async Task ReplayBufferAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        var status = await client.Outputs.GetReplayBufferStatusAsync(cancellationToken: ct);
        if (status?.OutputActive == true)
        {
            await client.Outputs.SaveReplayBufferAsync(cancellationToken: ct);
        }
    }

    internal static async Task BrowserSourceAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        var current = await client.Inputs.GetInputSettingsAsync<BrowserSourceSettings>(
            "StreamOverlay",
            ct
        );
        _ = current?.Url;

        await client.Inputs.SetInputSettingsAsync(
            "StreamOverlay",
            new BrowserSourceSettings(
                Url: "https://myoverlay.example.com",
                Width: 1920,
                Height: 1080
            ),
            cancellationToken: ct
        );

        await client.Inputs.SetInputSettingsAsync(
            "StreamOverlay",
            new OverlaySettings(Url: "https://myoverlay.example.com"),
            MyContext.Default.OverlaySettings,
            cancellationToken: ct
        );
    }

    internal static async Task UtilitiesAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        await client.Scenes.SwitchProgramSceneAndWaitAsync("Scene", cancellationToken: ct);
        _ = await client.Sources.SourceExistsAsync("Source", ct);
        await client.Filters.CreateSourceFilterAsync(
            "Source",
            "MyFilter",
            "gain_filter",
            new OverlaySettings(),
            MyContext.Default.OverlaySettings,
            ct
        );
        _ = await client.WaitForEventAsync<CurrentProgramSceneChangedEventArgs>(
            e => e.EventData.SceneName == "Scene",
            TimeSpan.FromSeconds(5),
            ct
        );
    }

    internal static async Task VersionAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        GetVersionResponseData? version = await client.General.GetVersionAsync(
            cancellationToken: ct
        );
        _ = version?.ObsVersion;
    }

    internal static async Task BatchAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        List<BatchRequestItem> items =
        [
            new("GetVersion", null),
            new(
                "SetCurrentProgramScene",
                new SetCurrentProgramSceneRequestData(sceneName: "Intro")
            ),
            new("Sleep", new SleepRequestData(sleepMillis: 100)),
            new(
                "SetInputMute",
                new SetInputMuteRequestData { InputName = "Mic", InputMuted = false }
            ),
        ];

        var results = await client.CallBatchAsync(
            items,
            executionType: RequestBatchExecutionType.SerialRealtime,
            haltOnFailure: false,
            cancellationToken: ct
        );

        foreach (var result in results)
        {
            _ = $"{result.RequestType}: {result.RequestStatus.Result}";
        }
    }

    internal static async Task UtilitiesExtendedAsync(
        ObsWebSocketClient client,
        CancellationToken ct
    )
    {
        await client.Scenes.SwitchProgramSceneAsync("Scene", cancellationToken: ct);
        _ = await client.SceneItems.SetSceneItemEnabledAsync("Scene", "Source", null, ct);
        _ = await client.SceneItems.FindSceneItemIdAsync("Scene", "Source", ct);
        await client.Inputs.SetInputMutesAsync([("Mic", false), ("Desktop Audio", true)], ct);
        _ = await client.Sources.GetSourceScreenshotBytesAsync("Source", cancellationToken: ct);
        _ = await client.Sources.GetSourceScreenshotOnCanvasBytesAsync(
            "Source",
            cancellationToken: ct
        );
        await client.Sources.SaveSourceScreenshotToFileAsync(
            "Source",
            "shot.png",
            cancellationToken: ct
        );
        _ = await client.Config.EnsureProfileActiveAsync("Profile", ct);
        _ = await client.Config.EnsureSceneCollectionActiveAsync("Collection", ct);
        _ = await client.Outputs.IsVirtualCamActiveAsync(ct);
        _ = await client.Outputs.SetVirtualCamActiveAndWaitAsync(true, cancellationToken: ct);
        await client.General.TriggerHotkeyAsync("OBSBasic.StartRecording", ct);
        _ = await client.Inputs.CreateInputAsync(
            "browser_source",
            "NewOverlay",
            new OverlaySettings(),
            MyContext.Default.OverlaySettings,
            cancellationToken: ct
        );
    }

    internal static async Task EventStreamsAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        await foreach (
            var e in client.Scenes.CurrentProgramSceneChangedStream(cancellationToken: ct)
        )
        {
            _ = e.EventData.SceneName;
            break;
        }

        client.Scenes.CurrentProgramSceneChanged += (_, e) => _ = e.EventData.SceneName;

        _ = await client.WaitForEventAsync<CurrentProgramSceneChangedEventArgs>(ct);
        _ = await client.WaitForEventAsync<CurrentProgramSceneChangedEventArgs>(
            e => e.EventData.SceneName == "Intro",
            TimeSpan.FromSeconds(5),
            ct
        );
    }

    internal static async Task TypedBatchAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        ObsBatchBuilder batch = new();
        BatchRef<GetVersionResponseData> version = batch.General.GetVersion();
        _ = batch.Scenes.SetCurrentProgramScene(new(sceneName: "Intro"));
        _ = batch.General.Sleep(new(sleepMillis: 100));
        _ = batch.Inputs.SetInputMute(new() { InputName = "Mic", InputMuted = false });

        BatchResults results = await client.CallBatchAsync(
            batch,
            executionType: RequestBatchExecutionType.SerialRealtime,
            haltOnFailure: false,
            cancellationToken: ct
        );

        _ = results.Get(version).ObsVersion;
        _ = results.TryGet(version, out GetVersionResponseData? maybe);
        _ = maybe;

        if (!results.AllSucceeded())
        {
            foreach (var failed in results.GetFailures())
            {
                _ = $"{failed.RequestType}: {failed.RequestStatus.Comment}";
            }
        }

        ObsBatchBuilder raw = new();
        _ = raw.Add("GetStats");
        _ = raw.Add("SetInputSettings", System.Text.Json.JsonDocument.Parse("{}").RootElement);
    }

    internal static void TypedEnums(ObsWebSocketClient client)
    {
        client.Outputs.StreamStateChanged += (_, e) =>
        {
            string what = e.EventData.OutputState switch
            {
                OutputState.Started => "live",
                OutputState.Starting or OutputState.Reconnecting => "coming up",
                OutputState.Stopped or OutputState.Stopping => "going down",
                OutputState.Unknown => "in an unrecognised state",
                _ => "in between",
            };

            _ = what;
        };
    }

    internal static async Task NewHelpersAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        await client.MediaInputs.PlayMediaAsync("Stinger", ct);
        await client.MediaInputs.TriggerMediaActionAsync("Stinger", MediaInputAction.Restart, ct);
        _ = MediaInputAction.Play.ToWireValue();

        _ = await client.Record.SetRecordActiveAndWaitAsync(true, cancellationToken: ct);
        _ = await client.Stream.SetStreamActiveAndWaitAsync(false, cancellationToken: ct);
        _ = await client.Record.IsRecordActiveAsync(ct);
        _ = await client.Stream.IsStreamActiveAsync(ct);
        _ = await client.Scenes.SceneExistsAsync("Scene", ct);
        _ = await client.SceneItems.FindSceneItemIdAsync("Scene", "Source", ct);
        await client.Inputs.SetInputVolumeDbAsync("Mic", -6, ct);
        await client.Inputs.SetInputVolumeMulAsync("Mic", 0.5, ct);
    }

    internal static async Task TypedBatchResultsAsync(
        ObsWebSocketClient client,
        CancellationToken ct
    )
    {
        ObsBatchBuilder batch = new();
        BatchRef<GetSceneItemListResponseData> intro = batch.SceneItems.GetSceneItemList(
            new(sceneName: "Intro")
        );
        BatchRef<GetVersionResponseData> version = batch.General.GetVersion();
        BatchRef<GetSceneItemListResponseData> outro = batch.SceneItems.GetSceneItemList(
            new(sceneName: "Outro")
        );

        BatchResults results = await client.CallBatchAsync(batch, cancellationToken: ct);

        _ = results.Get(intro);
        _ = results.Get(version);
        _ = results.Get(outro);
    }

    internal static async Task TypedErrorsAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        try
        {
            await client.Ui.SetStudioModeEnabledAsync(new(true), ct);
        }
        catch (ObsWebSocketRequestException ex)
        {
            _ = $"{ex.RequestType} failed with {(int?)ex.StatusCode}: {ex.Comment}";
        }
        catch (ObsWebSocketTimeoutException)
        {
            // No response within the request timeout.
        }
    }

    internal static async Task TypedStatusFilterAsync(
        ObsWebSocketClient client,
        CancellationToken ct
    )
    {
        try
        {
            await client.SceneItems.GetSceneItemListAsync(new("Missing"), ct);
        }
        catch (ObsWebSocketRequestException ex)
            when (ex.StatusCode is RequestStatusCode.ResourceNotFound)
        {
            // The scene, input or filter does not exist.
        }
    }

    internal static async Task NumbersAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        int id =
            await client.SceneItems.FindSceneItemIdAsync("Intro", "Logo", ct)
            ?? throw new InvalidOperationException();
        await client.SceneItems.SetSceneItemIndexAsync(
            new(sceneItemId: id, sceneItemIndex: 0, sceneName: "Intro"),
            ct
        );

        long bytes = (await client.Stream.GetStreamStatusAsync(ct)).OutputBytes;
        double volume = (await client.Inputs.GetInputVolumeAsync(new("Mic"), ct)).InputVolumeMul;
        _ = $"{bytes} {volume}";
    }

    internal static async Task DroppingToTheWireAsync(
        ObsWebSocketClient client,
        CancellationToken ct
    )
    {
        string wire = MediaInputAction.Restart.ToWireValue();

        // Request data has to be a JsonElement or a type the serializer context knows. An
        // anonymous object compiles and then throws at runtime, so the README does not use one.
        using System.Text.Json.JsonDocument body = System.Text.Json.JsonDocument.Parse(
            """{"someField":1}"""
        );
        System.Text.Json.JsonElement? raw =
            await client.CallAsyncValue<System.Text.Json.JsonElement>(
                "SomeNewRequest",
                body.RootElement,
                cancellationToken: ct
            );
        _ = $"{wire} {raw}";
    }

    internal static async Task ParallelRecoveryAsync(
        ObsWebSocketClient client,
        ObsBatchBuilder batch,
        CancellationToken ct
    )
    {
        BatchResults results = await client.CallBatchAsync(
            batch,
            executionType: RequestBatchExecutionType.Parallel,
            haltOnFailure: false,
            cancellationToken: ct
        );

        foreach (RequestResponsePayload<object> row in results.Raw)
        {
            if (!row.RequestStatus.Result)
            {
                _ = $"one request failed with {row.RequestStatus.Code}";
                continue;
            }

            GetSceneItemListResponseData? data = row.GetData<GetSceneItemListResponseData>();
            _ = data?.SceneItems?.Count;
        }

        _ = results.AllSucceeded();
        _ = results.GetFailures().Count();
    }

    internal static async Task ConcurrentRequestsAsync(
        ObsWebSocketClient client,
        string[] sceneNames,
        CancellationToken ct
    )
    {
        Task<GetVersionResponseData> version = client.General.GetVersionAsync(ct);
        Task<GetStatsResponseData> stats = client.General.GetStatsAsync(ct);
        Task<GetSceneItemListResponseData>[] perScene =
        [
            .. sceneNames.Select(n =>
                client.SceneItems.GetSceneItemListAsync(new(sceneName: n), ct)
            ),
        ];

        await Task.WhenAll([version, stats, .. perScene.Cast<Task>()]);
        _ = version.Result.ObsVersion;
    }

    internal static async Task LowLevelAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        GetVersionResponseData? v = await client.CallAsync<GetVersionResponseData>(
            "GetVersion",
            null,
            cancellationToken: ct
        );

        // Request data has to be a JsonElement or a type the serializer context knows. An
        // anonymous object compiles and then throws at runtime, so the README does not use one.
        using System.Text.Json.JsonDocument body = System.Text.Json.JsonDocument.Parse(
            """{"someField":1}"""
        );
        System.Text.Json.JsonElement? raw =
            await client.CallAsyncValue<System.Text.Json.JsonElement>(
                "SomeNewRequest",
                body.RootElement,
                cancellationToken: ct
            );

        List<RequestResponsePayload<object>> results = await client.CallBatchAsync(
            [new BatchRequestItem("GetVersion", null), new BatchRequestItem("GetStats", null)],
            executionType: RequestBatchExecutionType.SerialRealtime,
            cancellationToken: ct
        );

        foreach (RequestResponsePayload<object> result in results)
        {
            _ = result.GetData<GetVersionResponseData>();
        }

        _ = $"{v?.ObsVersion} {raw}";
    }

    internal static async Task GroupedSurfaceAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        await client.Scenes.GetSceneListAsync(new(), ct);
        await client.Scenes.SwitchProgramSceneAndWaitAsync("Intro", cancellationToken: ct);
        await client.Inputs.SetInputVolumeDbAsync("Mic", -6, ct);
        await client.SceneItems.SetSceneItemEnabledAsync("Intro", "Logo", false, ct);

        client.Scenes.CurrentProgramSceneChanged += (_, e) => { };
        await foreach (
            var e in client.Scenes.CurrentProgramSceneChangedStream(cancellationToken: ct)
        )
        {
            break;
        }
    }

    internal static async Task TimeoutTypeAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        try
        {
            _ = await client.WaitForEventAsync<CurrentProgramSceneChangedEventArgs>(
                TimeSpan.FromSeconds(5),
                ct
            );
        }
        catch (ObsWebSocketTimeoutException)
        {
            // One catch covers a request timeout and a wait timeout alike.
        }
    }

    internal static void HostIntegration(
        Microsoft.Extensions.Hosting.IHostApplicationBuilder builder
    )
    {
        _ = builder
            .AddObsWebSocketClient("obs")
            .WithAutoConnect()
            .WithHealthCheck()
            .WithReconnectPipeline();
    }

    internal static void TelemetryAndKeyedRegistration(IServiceCollection services)
    {
        _ = services.AddObsWebSocketClient(
            "main",
            o => o.ServerUri = new Uri("ws://localhost:4455")
        );
        _ = services.AddObsWebSocketClient("booth", o => o.ServerUri = new Uri("ws://booth:4455"));
        _ = ObsWebSocketDiagnostics.ActivitySourceName;
        _ = ObsWebSocketDiagnostics.MeterName;
        _ = ObsWebSocketResilience.ReconnectPipelineKey;
    }

    internal static async Task ScreenshotsAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        byte[]? png = await client.Sources.GetSourceScreenshotBytesAsync(
            "Intro",
            "png",
            cancellationToken: ct
        );
        _ = png;
        await client.Sources.SaveSourceScreenshotToFileAsync(
            "Intro",
            "shot.png",
            cancellationToken: ct
        );
    }

    internal static async Task ReplayBufferAsync2(ObsWebSocketClient client, CancellationToken ct)
    {
        var status = await client.Outputs.GetReplayBufferStatusAsync(ct);
        if (status.OutputActive)
        {
            await client.Outputs.SaveReplayBufferAsync(ct);
        }
    }

    internal static async Task StudioModeAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        try
        {
            await client.Ui.SetStudioModeEnabledAsync(new(true), ct);
        }
        catch (ObsWebSocketRequestException ex)
        {
            _ = $"{ex.RequestType} failed with {(int?)ex.StatusCode}: {ex.Comment}";
        }
        catch (ObsWebSocketTimeoutException) { }
    }
}
