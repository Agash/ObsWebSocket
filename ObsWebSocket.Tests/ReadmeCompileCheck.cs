using System.Text.Json.Serialization;
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
        await client.SetInputTextAsync("NewsTicker", "Breaking: Live now!", ct);

        var settings = new TextGdiPlusInputSettings(Text: "Breaking: Live now!", WordWrap: true);
        await client.SetInputSettingsAsync("NewsTicker", settings, cancellationToken: ct);
    }

    internal static async Task ReplayBufferAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        var status = await client.GetReplayBufferStatusAsync(cancellationToken: ct);
        if (status?.OutputActive == true)
        {
            await client.SaveReplayBufferAsync(cancellationToken: ct);
        }
    }

    internal static async Task BrowserSourceAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        var current = await client.GetInputSettingsAsync<BrowserSourceSettings>("StreamOverlay", ct);
        _ = current?.Url;

        await client.SetInputSettingsAsync(
            "StreamOverlay",
            new BrowserSourceSettings(Url: "https://myoverlay.example.com", Width: 1920, Height: 1080),
            cancellationToken: ct
        );

        await client.SetInputSettingsAsync(
            "StreamOverlay",
            new OverlaySettings(Url: "https://myoverlay.example.com"),
            MyContext.Default.OverlaySettings,
            cancellationToken: ct
        );
    }

    internal static async Task UtilitiesAsync(ObsWebSocketClient client, CancellationToken ct)
    {
        await client.SwitchSceneAndWaitAsync("Scene", cancellationToken: ct);
        _ = await client.SourceExistsAsync("Source", ct);
        await client.CreateSourceFilterAsync(
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
        GetVersionResponseData? version = await client.GetVersionAsync(cancellationToken: ct);
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
        await client.SwitchSceneAsync("Scene", cancellationToken: ct);
        _ = await client.SetSceneItemEnabledAsync("Scene", "Source", null, ct);
        _ = await client.FindSceneItemIdAsync("Scene", "Source", ct);
        await client.SetInputMutesAsync([("Mic", false), ("Desktop Audio", true)], ct);
        _ = await client.GetSourceScreenshotBytesAsync("Source", cancellationToken: ct);
        _ = await client.GetSourceScreenshotOnCanvasBytesAsync("Source", cancellationToken: ct);
        await client.SaveSourceScreenshotToFileAsync("Source", "shot.png", cancellationToken: ct);
        _ = await client.EnsureProfileActiveAsync("Profile", ct);
        _ = await client.EnsureSceneCollectionActiveAsync("Collection", ct);
        _ = await client.IsVirtualCamActiveAsync(ct);
        _ = await client.SetVirtualCamActiveAndWaitAsync(true, cancellationToken: ct);
        await client.TriggerHotkeyAsync("OBSBasic.StartRecording", ct);
        _ = await client.CreateInputAsync(
            "browser_source",
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
        var results = await client.CallBatchAsync(
            batch => batch
                .GetVersion()
                .SetCurrentProgramScene(new(sceneName: "Intro"))
                .Sleep(new(sleepMillis: 100))
                .SetInputMute(new() { InputName = "Mic", InputMuted = false }),
            executionType: RequestBatchExecutionType.SerialRealtime,
            haltOnFailure: false,
            cancellationToken: ct
        );

        foreach (var result in results)
        {
            _ = $"{result.RequestType}: {result.RequestStatus.Result}";
        }

        ObsBatchBuilder batch2 = new();
        _ = batch2.Add("GetStats").Add("SetInputSettings", System.Text.Json.JsonDocument.Parse("{}").RootElement);
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
        await client.PlayMediaAsync("Stinger", ct);
        await client.TriggerMediaActionAsync("Stinger", MediaInputAction.Restart, ct);
        _ = MediaInputAction.Play.ToWireValue();

        _ = await client.SetRecordActiveAndWaitAsync(true, cancellationToken: ct);
        _ = await client.SetStreamActiveAndWaitAsync(false, cancellationToken: ct);
        _ = await client.IsRecordActiveAsync(ct);
        _ = await client.IsStreamActiveAsync(ct);
        _ = await client.SceneExistsAsync("Scene", ct);
        _ = await client.FindSceneItemIdAsync("Scene", "Source", ct);
        await client.SetInputVolumeDbAsync("Mic", -6, ct);
        await client.SetInputVolumeMulAsync("Mic", 0.5, ct);
    }
}
