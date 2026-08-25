# ObsWebSocket.Core

Modern .NET client for OBS Studio WebSocket v5, with generated protocol types and DI-first integration.

[![Build Status](https://img.shields.io/github/actions/workflow/status/Agash/ObsWebSocket/build.yml?branch=master&style=flat-square&logo=github&logoColor=white)](https://github.com/Agash/ObsWebSocket/actions)
[![NuGet Version](https://img.shields.io/nuget/v/ObsWebSocket.Core.svg?style=flat-square&logo=nuget&logoColor=white)](https://www.nuget.org/packages/ObsWebSocket.Core/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg?style=flat-square)](https://opensource.org/licenses/MIT)

## Targets

- `net11.0`
- `net10.0`
- `net9.0`

## Install

```bash
dotnet add package ObsWebSocket.Core
```

## Features

- Strongly typed request/response DTOs generated from the obs-websocket protocol
- Strongly typed event args
- Async-first API with cancellation support
- DI helpers via `AddObsWebSocketClient()`
- JSON and MessagePack transports (configurable per environment)
- Reconnect, timeout, and event subscription options
- Typed settings helpers for inputs, filters, transitions, outputs, and stream service, working with built-in library types or your own AOT-safe source-generated types

> **OBS WebSocket v5 only** (OBS Studio 28+). Enable the server via *Tools → WebSocket Server Settings* in OBS.

## Quick Start

`appsettings.json`:

```json
{
  "Obs": {
    "ServerUri": "ws://localhost:4455",
    "Password": "",
    "Format": "Json"
  }
}
```

`Program.cs`:

```csharp
using ObsWebSocket.Core;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<ObsWebSocketClientOptions>(
    builder.Configuration.GetSection("Obs"));
builder.Services.AddObsWebSocketClient();
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
```

`Worker.cs`:

```csharp
using ObsWebSocket.Core;
using ObsWebSocket.Core.Events.Generated;

public sealed class Worker(ObsWebSocketClient client) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        client.CurrentProgramSceneChanged += OnSceneChanged;
        await client.ConnectAsync(ct);

        var version = await client.GetVersionAsync(cancellationToken: ct);
        Console.WriteLine($"Connected to OBS {version?.ObsVersion}");
    }

    public async Task StopAsync(CancellationToken ct)
    {
        client.CurrentProgramSceneChanged -= OnSceneChanged;
        if (client.IsConnected) await client.DisconnectAsync();
    }

    private static void OnSceneChanged(object? _, CurrentProgramSceneChangedEventArgs e) =>
        Console.WriteLine($"Scene changed: {e.EventData.SceneName}");
}
```

## Common Use Cases

### Update a text source

```csharp
// One-liner helper
await client.SetInputTextAsync("NewsTicker", "Breaking: Live now!", ct);

// Or use a typed settings object to update multiple properties at once
var settings = new TextGdiPlusInputSettings(Text: "Breaking: Live now!", WordWrap: true);
await client.SetInputSettingsAsync("NewsTicker", settings, cancellationToken: ct);
```

`TextGdiPlusInputSettings` is a built-in library type. The same pattern applies to `TextFreetype2InputSettings`, `BrowserSourceSettings`, and the filter settings types, which live in `ObsWebSocket.Core.Protocol.Common.InputSettings` and `ObsWebSocket.Core.Protocol.Common.FilterSettings`.

### Check and save the replay buffer

```csharp
var status = await client.GetReplayBufferStatusAsync(cancellationToken: ct);
if (status?.OutputActive == true)
{
    await client.SaveReplayBufferAsync(cancellationToken: ct);
    Console.WriteLine("Replay saved.");
}
```

### Create or update a browser source

Use a library type for common properties, or define your own type to target exactly what you need:

```csharp
// Library type, covers the standard browser source properties
var current = await client.GetInputSettingsAsync<BrowserSourceSettings>("StreamOverlay", ct);
Console.WriteLine($"Current URL: {current?.Url}");

await client.SetInputSettingsAsync(
    "StreamOverlay",
    new BrowserSourceSettings(Url: "https://myoverlay.example.com", Width: 1920, Height: 1080),
    cancellationToken: ct
);
```

```csharp
// Consumer type, define only the properties you care about, fully AOT-safe
[JsonSerializable(typeof(OverlaySettings))]
internal partial class MyContext : JsonSerializerContext { }

internal sealed record OverlaySettings(
    [property: JsonPropertyName("url")]  string? Url = null,
    [property: JsonPropertyName("css")]  string? Css = null
);

await client.SetInputSettingsAsync(
    "StreamOverlay",
    new OverlaySettings(Url: "https://myoverlay.example.com"),
    MyContext.Default.OverlaySettings,
    cancellationToken: ct
);
```

Both `Set` overloads take `overlay` before the cancellation token. It defaults to `true`, which merges your values onto the existing settings. Pass `overlay: false` to replace them outright.

> Raw `JsonElement` access is also available. All settings helpers have counterparts in the generated types under `ObsWebSocket.Core.Protocol.Requests` if you need full control.

## Helper API

Every typed settings helper has two overloads: an implicit one for library-registered types, and an explicit one taking a `JsonTypeInfo<T>` for consumer-provided types. Use the explicit overload to stay AOT-safe.

**Settings read/write:**

| Helper | Notes |
|---|---|
| `GetInputSettingsAsync<T>` / `SetInputSettingsAsync<T>` | Input settings; Set supports `overlay` |
| `GetInputDefaultSettingsAsync<T>` | Default settings for a given input kind |
| `GetSourceFilterSettingsAsync<T>` / `SetSourceFilterSettingsAsync<T>` | Filter settings; Set supports `overlay` |
| `GetSourceFilterDefaultSettingsAsync<T>` | Default settings for a given filter kind |
| `GetCurrentSceneTransitionSettingsAsync<T>` / `SetCurrentSceneTransitionSettingsAsync<T>` | Transition settings |
| `GetOutputSettingsAsync<T>` / `SetOutputSettingsAsync<T>` | Output settings |
| `GetStreamServiceSettingsAsync<T>` / `SetStreamServiceSettingsAsync<T>` | Stream service settings |

Most of these take optional parameters ahead of the cancellation token, so pass it as `cancellationToken: ct`.

**Scenes and scene items:**

- `SwitchSceneAsync(scene, cancellationToken: ct)` switches the Program scene, or Preview with `switchToProgram: false`. Optional `transitionName` and `transitionDurationMs` apply to that switch only.
- `SwitchSceneAndWaitAsync(scene, cancellationToken: ct)` does the same and waits for the event confirming it.
- `SetSceneItemEnabledAsync(scene, sourceName, isEnabled, ct)` returns the resulting state. Leave `isEnabled` null to toggle. There is an overload taking a numeric `sceneItemId`.
- `TryGetSceneItemIdAsync(scene, sourceName, ct)` returns null instead of throwing when the item is not in the scene.
- `SourceExistsAsync(name, ct)` checks whether a source exists.

**Inputs and filters:**

- `SetInputTextAsync(name, text, ct)` is shorthand for updating text source content.
- `SetInputMutesAsync(inputMutes, ct)` sets many mute states in one batch, taking `IEnumerable<(string InputName, bool IsMuted)>`.
- `CreateInputAsync<T>(kind, name, settings, ...)` creates an input with typed settings, optionally placing it in a scene.
- `CreateSourceFilterAsync<T>(source, filterName, kind, settings, ct)` adds a typed filter.

**Screenshots:**

- `GetSourceScreenshotBytesAsync(source, ...)` returns the decoded image bytes rather than a base64 data URI.
- `GetSourceScreenshotOnCanvasBytesAsync(source, ...)` does the same at full canvas dimensions.
- `SaveSourceScreenshotToFileAsync(source, filePath, ...)` writes straight to disk.

**Application state:**

- `EnsureProfileActiveAsync(name, ct)` and `EnsureSceneCollectionActiveAsync(name, ct)` switch only if needed, returning whether the target is active afterwards rather than throwing when it does not exist.
- `IsVirtualCamActiveAsync(ct)` and `SetVirtualCamActiveAndWaitAsync(activate, timeout, ct)` read and drive the virtual camera.
- `TriggerHotkeyAsync(hotkeyName, ct)` fires a hotkey by name.
- `WaitForEventAsync<TEventArgs>(predicate, timeout, ct)` awaits the next matching occurrence of a typed OBS event.

For direct low-level access, all generated request/response types are in:
- `ObsWebSocket.Core.Protocol.Requests`
- `ObsWebSocket.Core.Protocol.Responses`
- `ObsWebSocket.Core.Events.Generated`

### Batch API

Send several requests in one round trip, with OBS executing them back to back:

```csharp
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
    Console.WriteLine($"{result.RequestType}: {result.RequestStatus.Result}");
}
```

`Sleep` is only valid inside a batch, and pairs with `SerialRealtime` to pace a sequence.

Each item's `RequestData` should be `null`, a generated `*RequestData` DTO, or a `JsonElement` built with `Utf8JsonWriter`. Anonymous types and reflection-based serialization are not AOT-safe here.

## Example App

`ObsWebSocket.Example` is a host-based sample with configuration and DI.

- **Interactive mode**: command loop (`help`, `version`, `scene`, `batch-example`, `get-all-settings-types`, etc.)
- **Transport validation mode**: exercises JSON and MsgPack across scene/input/filter/settings APIs, then enters the interactive loop
- **One-shot mode**: pass a command as a process argument for CI/automation, `ObsWebSocket.Example run-transport-tests`

It reads the same `Obs` section as above, plus:

```json
{
  "ExampleValidation": {
    "RunValidationOnStartup": false,
    "ValidationIterations": 1
  }
}
```

## Native AOT

```bash
dotnet publish ObsWebSocket.Example/ObsWebSocket.Example.csproj -c Release -r win-x64 --self-contained true
```

## Contributing

Contributions are welcome. See [`CONTRIBUTING.md`](CONTRIBUTING.md).

## License

MIT. See [`LICENSE.txt`](LICENSE.txt).
