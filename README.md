# ObsWebSocket.Core

Modern .NET client for OBS Studio WebSocket v5, with generated protocol types and DI-first integration.

[![Build Status](https://img.shields.io/github/actions/workflow/status/Agash/ObsWebSocket/build.yml?branch=master&style=flat-square&logo=github&logoColor=white)](https://github.com/Agash/ObsWebSocket/actions)
[![NuGet Version](https://img.shields.io/nuget/v/ObsWebSocket.Core.svg?style=flat-square&logo=nuget&logoColor=white)](https://www.nuget.org/packages/ObsWebSocket.Core/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg?style=flat-square)](https://opensource.org/licenses/MIT)

## Targets

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
- Typed settings helpers for inputs, filters, transitions, outputs, and stream service — works with built-in library types or your own AOT-safe source-generated types

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
await client.SetInputSettingsAsync("NewsTicker", settings, ct);
```

`TextGdiPlusInputSettings` is a built-in library type. The same pattern applies to `TextFreetype2InputSettings`, `BrowserSourceSettings`, and filter settings types — all live in `ObsWebSocket.Core.Protocol.Common.InputSettings` and `ObsWebSocket.Core.Protocol.Common.FilterSettings`.

### Check and save the replay buffer

```csharp
var status = await client.GetReplayBufferStatusAsync(cancellationToken: ct);
if (status.OutputActive == true)
{
    await client.SaveReplayBufferAsync(cancellationToken: ct);
    Console.WriteLine("Replay saved.");
}
```

### Create or update a browser source

Use a library type for common properties, or define your own type to target exactly what you need:

```csharp
// Library type — covers the standard browser source properties
var current = await client.GetInputSettingsAsync<BrowserSourceSettings>("StreamOverlay", ct);
Console.WriteLine($"Current URL: {current?.Url}");

await client.SetInputSettingsAsync(
    "StreamOverlay",
    new BrowserSourceSettings(Url: "https://myoverlay.example.com", Width: 1920, Height: 1080),
    ct
);
```

```csharp
// Consumer type — define only the properties you care about, fully AOT-safe
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
    ct
);
```

> Raw `JsonElement` access is also available — all settings helpers have counterparts in the generated types under `ObsWebSocket.Core.Protocol.Requests` if you need full control.

## Helper API

All typed settings helpers have two overloads: an implicit one for library-registered types, and an explicit one accepting a `JsonTypeInfo<T>` for consumer-provided types. All are AOT-safe when using the explicit overload.

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

**Scene / source utilities:**

- `SwitchSceneAndWaitAsync(scene, ct)` — switch scene and wait for the event to confirm
- `SetInputTextAsync(name, text, ct)` — shorthand for updating text source content
- `CreateSourceFilterAsync<T>(source, filterName, kind, settings, ct)` — add a typed filter
- `SourceExistsAsync(name, ct)` — check whether a source exists
- `WaitForEventAsync<TEventArgs>(ct)` — await the next occurrence of any typed OBS event

For direct low-level access, all generated request/response types are in:
- `ObsWebSocket.Core.Protocol.Requests`
- `ObsWebSocket.Core.Protocol.Responses`
- `ObsWebSocket.Core.Events.Generated`

### Batch API

`CallBatchAsync` accepts a `List<BatchRequestItem>`. Each item's `RequestData` should be `null`, a `JsonElement` built with `Utf8JsonWriter`, or a generated `*RequestData` DTO — anonymous types and reflection-based serialization are not AOT-safe here.

## Example App

`ObsWebSocket.Example` is a host-based sample with configuration and DI.

- **Interactive mode** — command loop (`help`, `version`, `scene`, `batch-example`, `get-all-settings-types`, etc.)
- **Transport validation mode** — exercises JSON and MsgPack across scene/input/filter/settings APIs, then enters the interactive loop
- **One-shot mode** — pass a command as a process argument for CI/automation:
  `ObsWebSocket.Example run-transport-tests`

`appsettings.json`:

```json
{
  "Obs": {
    "ServerUri": "ws://localhost:4455",
    "Password": "",
    "Format": "Json"
  },
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
