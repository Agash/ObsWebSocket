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
- Strongly typed event args, observable as `IAsyncEnumerable<T>` or as classic events
- Typed batch builder that pairs each request type with its own payload
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

        var version = await client.General.GetVersionAsync(cancellationToken: ct);
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

## Observing Events

Every OBS event is exposed as an async sequence. The stream subscribes for the lifetime of the
loop and unsubscribes when it ends, so there is no handler bookkeeping and cancellation is the
only thing that stops it:

```csharp
await foreach (var e in client.CurrentProgramSceneChangedStream(cancellationToken: ct))
{
    Console.WriteLine($"Program scene is now {e.EventData.SceneName}");
}
```

Streams buffer a bounded number of events and drop the oldest when a consumer falls behind, so a
slow loop cannot stall the receive loop. Pass `capacity` to change that.

The classic events are unchanged and still work, including alongside a stream over the same event:

```csharp
client.CurrentProgramSceneChanged += (_, e) =>
    Console.WriteLine($"Program scene is now {e.EventData.SceneName}");
```

To wait for a single occurrence rather than a sequence, use `WaitForEventAsync`. It subscribes
before returning, so you can start the wait and then trigger the action without racing it:

```csharp
// Next occurrence, no timeout
var changed = await client.WaitForEventAsync<CurrentProgramSceneChangedEventArgs>(ct);

// Next matching occurrence, giving up after 5 seconds
var intro = await client.WaitForEventAsync<CurrentProgramSceneChangedEventArgs>(
    e => e.EventData.SceneName == "Intro",
    TimeSpan.FromSeconds(5),
    ct
);
```

## Common Use Cases

### Update a text source

```csharp
// One-liner helper
await client.Inputs.SetInputTextAsync("NewsTicker", "Breaking: Live now!", ct);

// Or use a typed settings object to update multiple properties at once
var settings = new TextGdiPlusInputSettings(Text: "Breaking: Live now!", WordWrap: true);
await client.Inputs.SetInputSettingsAsync("NewsTicker", settings, cancellationToken: ct);
```

`TextGdiPlusInputSettings` is a built-in library type. The same pattern applies to `TextFreetype2InputSettings`, `BrowserSourceSettings`, and the filter settings types, which live in `ObsWebSocket.Core.Protocol.Common.InputSettings` and `ObsWebSocket.Core.Protocol.Common.FilterSettings`.

### Check and save the replay buffer

```csharp
var status = await client.Outputs.GetReplayBufferStatusAsync(cancellationToken: ct);
if (status?.OutputActive == true)
{
    await client.Outputs.SaveReplayBufferAsync(cancellationToken: ct);
    Console.WriteLine("Replay saved.");
}
```

### Create or update a browser source

Use a library type for common properties, or define your own type to target exactly what you need:

```csharp
// Library type, covers the standard browser source properties
var current = await client.Inputs.GetInputSettingsAsync<BrowserSourceSettings>("StreamOverlay", ct);
Console.WriteLine($"Current URL: {current?.Url}");

await client.Inputs.SetInputSettingsAsync(
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

await client.Inputs.SetInputSettingsAsync(
    "StreamOverlay",
    new OverlaySettings(Url: "https://myoverlay.example.com"),
    MyContext.Default.OverlaySettings,
    cancellationToken: ct
);
```

Both `Set` overloads take `overlay` before the cancellation token. It defaults to `true`, which merges your values onto the existing settings. Pass `overlay: false` to replace them outright.

> Raw `JsonElement` access is also available. All settings helpers have counterparts in the generated types under `ObsWebSocket.Core.Protocol.Requests` if you need full control.

## Requests and helpers

Everything the client can do is reached through the category the OBS protocol puts it in, so a
generated request and a convenience that wraps several of them sit together and read the same way:

```csharp
await client.Scenes.GetSceneListAsync(new(), ct);            // generated request
await client.Scenes.SwitchProgramSceneAndWaitAsync("Intro", cancellationToken: ct);  // convenience
```

The categories are OBS's own: `Canvases`, `Config`, `Filters`, `General`, `Inputs`, `MediaInputs`,
`Outputs`, `Record`, `SceneItems`, `Scenes`, `Sources`, `Stream`, `Transitions`, `Ui`. They follow
the protocol, so a refresh that recategorises a request moves it here too. The batch builder uses
the same grouping.

`WaitForEventAsync` and `CallBatchAsync` stay directly on the client, since neither belongs to one
category.

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
- `FindSceneItemIdAsync(scene, sourceName, ct)` returns null instead of throwing when the item is not in the scene.
- `SourceExistsAsync(name, ct)` and `SceneExistsAsync(name, ct)` check for existence.

**Inputs and filters:**

- `SetInputTextAsync(name, text, ct)` is shorthand for updating text source content.
- `SetInputVolumeDbAsync(name, db, ct)` and `SetInputVolumeMulAsync(name, mul, ct)` each pick one unit. The underlying request accepts either and fails when given neither.
- `SetInputMutesAsync(inputMutes, ct)` sets many mute states in one batch, taking `IEnumerable<(string InputName, bool IsMuted)>`.
- `CreateInputAsync<T>(kind, name, settings, ...)` creates an input with typed settings, optionally placing it in a scene.
- `CreateSourceFilterAsync<T>(source, filterName, kind, settings, ct)` adds a typed filter.

**Screenshots:**

- `GetSourceScreenshotBytesAsync(source, ...)` returns the decoded image bytes rather than a base64 data URI.
- `GetSourceScreenshotOnCanvasBytesAsync(source, ...)` does the same at full canvas dimensions.
- `SaveSourceScreenshotToFileAsync(source, filePath, ...)` writes straight to disk.

**Outputs:**

- `SetRecordActiveAndWaitAsync(activate, timeout, ct)` and `SetStreamActiveAndWaitAsync(...)` start or stop the output and wait for OBS to confirm, returning the resulting `OutputState`.
- `IsRecordActiveAsync(ct)`, `IsStreamActiveAsync(ct)`, and `IsVirtualCamActiveAsync(ct)` read current state.
- `SetVirtualCamActiveAndWaitAsync(activate, timeout, ct)` does the same for the virtual camera.

**Application state:**

- `EnsureProfileActiveAsync(name, ct)` and `EnsureSceneCollectionActiveAsync(name, ct)` switch only if needed, returning whether the target is active afterwards rather than throwing when it does not exist.
- `TriggerHotkeyAsync(hotkeyName, ct)` fires a hotkey by name.
- `WaitForEventAsync<TEventArgs>(...)` awaits a single event. See [Observing Events](#observing-events).

### Typed protocol enums

Protocol enums that travel as strings have a real C# enum, so states can be matched rather than
compared against constants:

```csharp
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

    Console.WriteLine($"Stream is {what}");
};
```

Media transport works the same way, with shorthands for the common actions:

```csharp
await client.MediaInputs.PlayMediaAsync("Stinger", ct);
await client.MediaInputs.TriggerMediaActionAsync("Stinger", MediaInputAction.Restart, ct);
```

The wire constants remain available as `const` strings on `ObsOutputState` and
`ObsMediaInputAction`, and `ToWireValue()` converts an enum back when you need the raw form.

For direct low-level access, all generated request/response types are in:
- `ObsWebSocket.Core.Protocol.Requests`
- `ObsWebSocket.Core.Protocol.Responses`
- `ObsWebSocket.Core.Events.Generated`

### Batch API

Send several requests in one round trip, with OBS executing them back to back. Requests are
grouped by the protocol's own categories, and each one hands back a reference carrying its
response type:

```csharp
ObsBatchBuilder batch = new();
BatchRef<GetVersionResponseData> version = batch.General.GetVersion();
BatchRef<GetSceneListResponseData> scenes = batch.Scenes.GetSceneList(new());
_ = batch.General.Sleep(new(sleepMillis: 100));
_ = batch.Inputs.SetInputMute(new() { InputName = "Mic", InputMuted = false });

BatchResults results = await client.CallBatchAsync(
    batch,
    executionType: RequestBatchExecutionType.SerialRealtime,
    haltOnFailure: false,
    cancellationToken: ct
);

Console.WriteLine(results.Get(version).ObsVersion);
Console.WriteLine(results.Get(scenes).Scenes?.Count);
```

`results.Get(reference)` returns the response record for that request, so neither its position nor
its type is restated. A request type may appear many times in one batch and each reference still
resolves to its own result.

`Sleep` is only valid inside a batch, and pairs with `SerialRealtime` to pace a sequence.

`TryGet` reports a failed or missing result instead of throwing, and `Get` throws
`ObsWebSocketRequestException` carrying the OBS status code when that request was rejected:

```csharp
if (!results.AllSucceeded())
{
    foreach (var failed in results.GetFailures())
    {
        Console.WriteLine($"{failed.RequestType}: {failed.RequestStatus.Comment}");
    }
}
```

With `haltOnFailure: true` OBS stops at the first failure, so fewer results come back than
requests were sent. Reading a reference past that point throws, and `Count` reports how many ran.

`Add` takes anything the generated methods do not cover, including a raw `JsonElement` payload,
and an overload accepting a `JsonTypeInfo<T>` keeps a custom payload AOT-safe:

```csharp
batch.Add("GetStats").Add("SetInputSettings", myJsonElement);
```

The lower-level form still works, and remains the way to build a batch ahead of time:

```csharp
List<BatchRequestItem> items =
[
    new("GetVersion", null),
    new("SetCurrentProgramScene", new SetCurrentProgramSceneRequestData(sceneName: "Intro")),
];

var raw = await client.CallBatchAsync(items, cancellationToken: ct);
```

Either way, an item's `RequestData` should be `null`, a generated `*RequestData` DTO, or a `JsonElement` built with `Utf8JsonWriter`. Anonymous types and reflection-based serialization are not AOT-safe here.

## Multiple OBS instances

Register clients by name and resolve them with `[FromKeyedServices]`:

```csharp
builder.Services.AddObsWebSocketClient("main", o => o.ServerUri = new Uri("ws://localhost:4455"));
builder.Services.AddObsWebSocketClient("booth", o => o.ServerUri = new Uri("ws://booth:4455"));

public sealed class Worker(
    [FromKeyedServices("main")] ObsWebSocketClient main,
    [FromKeyedServices("booth")] ObsWebSocketClient booth);
```

## Errors

Failures are typed, so they can be caught by category rather than matched by message:

```csharp
try
{
    await client.Ui.SetStudioModeEnabledAsync(new(true), ct);
}
catch (ObsWebSocketRequestException ex)
{
    // OBS rejected the request. ex.Status carries the protocol code and comment.
    Console.WriteLine($"{ex.RequestType} failed with {ex.Status?.Code}: {ex.Comment}");
}
catch (ObsWebSocketTimeoutException)
{
    // No response within the request timeout.
}
```

`ObsWebSocketSerializationException` covers payloads that cannot be written or read, and all three
derive from `ObsWebSocketException` if you would rather catch the lot.

Options are validated when the client is resolved, so a missing or malformed `ServerUri` fails at
startup with the offending option named, rather than on the first connection attempt.

## Reconnect

Reconnect delays grow by `ReconnectBackoffMultiplier`, are capped at `MaxReconnectDelayMs`, and
carry jitter so that several clients recovering from one outage do not retry in lockstep.
Authentication failures are never retried, since they cannot succeed on a second attempt.

To replace the policy outright rather than tune those options, register your own pipeline under
`ObsWebSocketResilience.ReconnectPipelineKey` after adding the client.

## Telemetry

The client emits traces and metrics under the name `ObsWebSocket.Core`, inert until something
subscribes:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(ObsWebSocketDiagnostics.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(ObsWebSocketDiagnostics.MeterName));
```

One activity per request, and one per batch rather than per item. Counters cover requests sent,
requests failed, events received and reconnect attempts, plus a request-duration histogram.

Timeouts and reconnect delays run on an injectable `TimeProvider`, so tests can drive them with
`FakeTimeProvider` instead of waiting.

## Example App

`ObsWebSocket.Example` is a host-based sample with configuration and DI.

- **Interactive mode**: command loop (`help`, `version`, `scene`, `watch`, `batch-example`, `get-all-settings-types`, etc.)
- **Transport validation mode**: exercises JSON and MsgPack across the whole surface, then enters the interactive loop
- **One-shot mode**: pass a command as a process argument for CI/automation, `ObsWebSocket.Example run-transport-tests`

`run-transport-tests` creates its own scene and input, so it does not depend on a particular OBS
layout, and removes them afterwards. On each transport it covers the settings modes, event streams,
`WaitForEventAsync`, the typed batch builder including duplicate request types and partial failure,
typed protocol enums, and the scene, input, volume and output helpers.

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
