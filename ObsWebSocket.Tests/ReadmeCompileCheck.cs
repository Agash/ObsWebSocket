using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Protocol.Common.InputSettings;
using ObsWebSocket.Core.Protocol;
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
        var current = await client.Inputs.GetInputSettingsAsync<BrowserSourceSettings>("StreamOverlay", ct);
        _ = current?.Url;

        await client.Inputs.SetInputSettingsAsync("StreamOverlay",
            new BrowserSourceSettings(Url: "https://myoverlay.example.com", Width: 1920, Height: 1080),
            cancellationToken: ct
        );

        await client.Inputs.SetInputSettingsAsync("StreamOverlay",
            new OverlaySettings(Url: "https://myoverlay.example.com"),
            MyContext.Default.OverlaySettings,
            cancellationToken: ct
        );
    }

    internal static async Task UtilitiesAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        await client.Scenes.SwitchSceneAndWaitAsync("Scene", cancellationToken: ct);
        _ = await client.Sources.SourceExistsAsync("Source", ct);
        await client.Filters.CreateSourceFilterAsync("Source",
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
        GetVersionResponseData? version = await client.General.GetVersionAsync(cancellationToken: ct);
        _ = version?.ObsVersion;
    }

    internal static async Task BatchAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        List<BatchRequestItem> items =
        [
            new("GetVersion", null),
            new("SetCurrentProgramScene", new SetCurrentProgramSceneRequestData(sceneName: "Intro")),
            new("Sleep", new SleepRequestData(sleepMillis: 100)),
            new("SetInputMute", new SetInputMuteRequestData { InputName = "Mic", InputMuted = false }),
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

    internal static async Task UtilitiesExtendedAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        await client.Scenes.SwitchSceneAsync("Scene", cancellationToken: ct);
        _ = await client.SceneItems.SetSceneItemEnabledAsync("Scene", "Source", null, ct);
        _ = await client.SceneItems.FindSceneItemIdAsync("Scene", "Source", ct);
        await client.Inputs.SetInputMutesAsync([("Mic", false), ("Desktop Audio", true)], ct);
        _ = await client.Sources.GetSourceScreenshotBytesAsync("Source", cancellationToken: ct);
        _ = await client.Sources.GetSourceScreenshotOnCanvasBytesAsync("Source", cancellationToken: ct);
        await client.Sources.SaveSourceScreenshotToFileAsync("Source", "shot.png", cancellationToken: ct);
        _ = await client.Config.EnsureProfileActiveAsync("Profile", ct);
        _ = await client.Config.EnsureSceneCollectionActiveAsync("Collection", ct);
        _ = await client.Outputs.IsVirtualCamActiveAsync(ct);
        _ = await client.Outputs.SetVirtualCamActiveAndWaitAsync(true, cancellationToken: ct);
        await client.General.TriggerHotkeyAsync("OBSBasic.StartRecording", ct);
        _ = await client.Inputs.CreateInputAsync("browser_source",
            "NewOverlay",
            new OverlaySettings(),
            MyContext.Default.OverlaySettings,
            cancellationToken: ct
        );
    }

    internal static async Task EventStreamsAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        await foreach (var e in client.CurrentProgramSceneChangedStream(cancellationToken: ct))
        {
            _ = e.EventData.SceneName;
            break;
        }

        client.CurrentProgramSceneChanged += (_, e) => _ = e.EventData.SceneName;

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
        client.StreamStateChanged += (_, e) =>
        {
            string what = OutputStateExtensions.FromWireValue(e.EventData.OutputState) switch
            {
                OutputState.Started => "live",
                OutputState.Starting or OutputState.Reconnecting => "coming up",
                OutputState.Stopped or OutputState.Stopping => "going down",
                null => $"unrecognised ({e.EventData.OutputState})",
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

    internal static async Task TypedBatchResultsAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        ObsBatchBuilder batch = new();
        BatchRef<GetSceneItemListResponseData> intro = batch.SceneItems.GetSceneItemList(new(sceneName: "Intro"));
        BatchRef<GetVersionResponseData> version = batch.General.GetVersion();
        BatchRef<GetSceneItemListResponseData> outro = batch.SceneItems.GetSceneItemList(new(sceneName: "Outro"));

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
            _ = $"{ex.RequestType} failed with {ex.Status?.Code}: {ex.Comment}";
        }
        catch (ObsWebSocketTimeoutException)
        {
            // No response within the request timeout.
        }
    }

    internal static void TelemetryAndKeyedRegistration(IServiceCollection services)
    {
        _ = services.AddObsWebSocketClient("main", o => o.ServerUri = new Uri("ws://localhost:4455"));
        _ = services.AddObsWebSocketClient("booth", o => o.ServerUri = new Uri("ws://booth:4455"));
        _ = ObsWebSocketDiagnostics.ActivitySourceName;
        _ = ObsWebSocketDiagnostics.MeterName;
        _ = ObsWebSocketResilience.ReconnectPipelineKey;
    }
}
