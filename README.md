# ObsWebSocket.Core

A .NET client for the OBS Studio WebSocket v5 protocol, with generated request types and DI-first
integration.

[![Build Status](https://img.shields.io/github/actions/workflow/status/Agash/ObsWebSocket/build.yml?branch=master&style=flat-square&logo=github&logoColor=white)](https://github.com/Agash/ObsWebSocket/actions)
[![NuGet Version](https://img.shields.io/nuget/v/ObsWebSocket.Core.svg?style=flat-square&logo=nuget&logoColor=white)](https://www.nuget.org/packages/ObsWebSocket.Core/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg?style=flat-square)](https://opensource.org/licenses/MIT)

Targets `net11.0`, `net10.0` and `net9.0`.

## Install

```bash
dotnet add package ObsWebSocket.Core
```

Requires OBS Studio 28 or newer with obs-websocket v5. Enable the server under
*Tools > WebSocket Server Settings*.

## Quick start

`appsettings.json`:

```json
{
  "ConnectionStrings": {
    "obs": "ws://localhost:4455?password=secret"
  }
}
```

`Program.cs`:

```csharp
using ObsWebSocket.Core;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.AddObsWebSocketClient("obs")    // endpoint from ConnectionStrings:obs
       .WithAutoConnect();              // connect on start, disconnect on stop
builder.Services.AddHostedService<Worker>();

await builder.Build().RunAsync();
```

`Worker.cs`:

```csharp
using ObsWebSocket.Core;
using ObsWebSocket.Core.Events.Generated;

public sealed class Worker(ObsWebSocketClient client) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var version = await client.General.GetVersionAsync(ct);
        Console.WriteLine($"Connected to OBS {version.ObsVersion}");

        await client.Input("Mic").SetMuteAsync(true, ct);

        await foreach (var e in client.Scenes.CurrentProgramSceneChangedStream(cancellationToken: ct))
        {
            Console.WriteLine($"Scene changed: {e.EventData.SceneName}");
        }
    }
}
```

## Three ways to call OBS

| | Example | Use it for |
|---|---|---|
| [Handles](#handles) | `client.Input("Mic").SetMuteAsync(true, ct)` | Requests about one scene, input, source, scene item or filter |
| [Category groups](#category-groups) | `client.Inputs.SetInputMuteAsync(new("Mic", true), ct)` | Everything. One method per protocol request, plus helpers |
| [Raw requests](#raw-requests) | `client.CallAsync<T>("SetInputMute", data, ct)` | Requests this build does not model |

Each forwards to the one below it, so they mix freely.

## Category groups

The client mirrors the categories the protocol defines. Requests, event streams and the helpers this
library adds sit in the group their category owns:

```csharp
await client.Scenes.GetSceneListAsync(new(), ct);
await client.Scenes.SwitchProgramSceneAndWaitAsync("Intro", cancellationToken: ct);
await client.Inputs.SetInputVolumeDbAsync("Mic", -6, ct);
await client.SceneItems.SetSceneItemEnabledAsync("Intro", "Logo", false, ct);

client.Scenes.CurrentProgramSceneChanged += (_, e) => { };
await foreach (var e in client.Scenes.CurrentProgramSceneChangedStream(cancellationToken: ct)) { break; }
```

The groups are `Canvases`, `Config`, `Filters`, `General`, `Inputs`, `MediaInputs`, `Outputs`,
`Record`, `SceneItems`, `Scenes`, `Sources`, `Stream`, `Transitions` and `Ui`.

`WaitForEventAsync` and `CallBatchAsync` sit on the client itself, since neither belongs to a
category.

## Handles

Most OBS requests identify their target by name or by uuid. A handle carries that identity so it is
not repeated on every call. A string is a name, a `Guid` is a uuid:

```csharp
await client.Scene("Intro").SetCurrentProgramAsync(ct);
await client.Scene(sceneGuid).SetNameAsync("Outro", ct);
await client.Input("Mic").SetMuteAsync(true, ct);
await client.Input("Mic").Filter("EQ").SetEnabledAsync(false, ct);
```

The entry points are `Scene`, `Input`, `Source`, `SceneItem` and `Filter`. Each carries the requests
the protocol defines for that kind of thing, with the entity dropped from the method name:
`SetSceneItemEnabled` is `SetEnabledAsync` on a scene item, `GetInputMute` is `GetMuteAsync` on an
input. The protocol name is in the XML docs and on the category group.

Requests that are not about a particular thing, such as `GetVersion`, `GetStats` and the record and
stream controls, are on their group only.

### Names and uuids

A name works, but breaks if the thing is renamed. Resolving a name to a uuid costs one round trip:

```csharp
SceneOperations intro = await client.Scene("Intro").ResolveAsync(ct);
// intro.Handle.IsResolved is true, and a rename no longer affects it
```

The protocol has no lookup for a single uuid, so this reads the scene list. When the name is not
found, the exception lists the names that were:

```
ObsWebSocketResourceNotFoundException: No scene named 'Intor'. Available: 'Intro', 'Gameplay', 'BRB'.
```

### Handles from events and responses

Events and responses that carry a uuid expose a handle for it, so acting on one needs no lookup:

```csharp
client.Scenes.CurrentProgramSceneChanged += async (_, e) =>
    await client.Scene(e.EventData.Scene).GetItemListAsync();

CreateSceneResponseData created = await client.Scenes.CreateSceneAsync(new("Intro"), ct);
await client.Scene(created.Scene).SetCurrentProgramAsync(ct);
```

### Scene items

OBS addresses scene items by a numeric id that only `GetSceneItemId` reports, so an item known by
source name has to be resolved before it can be used:

```csharp
SceneItemOperations logo = await client.Scene("Intro").ItemAsync("Logo", cancellationToken: ct);
await logo.SetEnabledAsync(false, ct);
await logo.Scene.GetItemListAsync(ct);

await client.Scene("Intro").Item(3).SetIndexAsync(0, ct);   // an id needs no lookup
```

`Item(long)` and `Filter(string)` send nothing, since an id and a filter name are the whole
identity.

### Canvases

Canvas-scoped requests take a uuid; `canvasName` appears only in `GetCanvasList`. Resolve a canvas
by name to use it:

```csharp
CanvasHandle vertical = await client.Canvases.ResolveAsync("Vertical", ct);
await client.Scene(vertical.Scene("Intro")).GetItemListAsync(ct);
```

Omitting the canvas means the main one, which is `CanvasHandle.Main`. A canvas scopes a name only,
so a resolved handle drops it.

## Helpers

Each group carries helpers for things that otherwise take several calls or a lookup. They are
hand-written, so they are on the group rather than on a handle.

Typed settings helpers have two overloads: an implicit one for library-registered types, and an
explicit one taking a `JsonTypeInfo<T>` for your own types. Use the explicit overload under
Native AOT.

**Settings**

| Helper | Notes |
|---|---|
| `Inputs.GetInputSettingsAsync<T>` / `SetInputSettingsAsync<T>` | Input settings; Set supports `overlay` |
| `Inputs.GetInputDefaultSettingsAsync<T>` | Defaults for an input kind |
| `Filters.GetSourceFilterSettingsAsync<T>` / `SetSourceFilterSettingsAsync<T>` | Filter settings; Set supports `overlay` |
| `Filters.GetSourceFilterDefaultSettingsAsync<T>` | Defaults for a filter kind |
| `Transitions.GetCurrentSceneTransitionSettingsAsync<T>` / `SetCurrentSceneTransitionSettingsAsync<T>` | Transition settings |
| `Outputs.GetOutputSettingsAsync<T>` / `SetOutputSettingsAsync<T>` | Output settings |
| `Config.GetStreamServiceSettingsAsync<T>` / `SetStreamServiceSettingsAsync<T>` | Stream service settings |

Most take optional parameters before the cancellation token, so pass it as `cancellationToken: ct`.

**Scenes and scene items**

- `Scenes.SwitchProgramSceneAsync(scene, ct)` and `Scenes.SwitchPreviewSceneAsync(scene, ct)`.
  Optional `transitionName` and `transitionDurationMs` apply to that switch only.
- `Scenes.SwitchProgramSceneAndWaitAsync` and `Scenes.SwitchPreviewSceneAndWaitAsync` also wait for
  the confirming event.
- `SceneItems.SetSceneItemEnabledAsync(scene, sourceName, isEnabled, ct)` returns the resulting
  state. Pass null for `isEnabled` to toggle. An overload takes the numeric item id.
- `SceneItems.FindSceneItemIdAsync(scene, sourceName, ct)` returns `long?`, null when the item is
  not in the scene.
- `Sources.SourceExistsAsync(name, ct)` and `Scenes.SceneExistsAsync(name, ct)`.

**Inputs and filters**

- `Inputs.SetInputTextAsync(name, text, ct)` updates text source content.
- `Inputs.SetInputVolumeDbAsync(name, db, ct)` and `Inputs.SetInputVolumeMulAsync(name, mul, ct)`
  each pick one unit. The underlying request accepts either and fails when given neither.
- `Inputs.SetInputMutesAsync(inputMutes, ct)` sets many mute states in one batch and returns the
  per-input results.
- `Inputs.CreateInputAsync<T>(kind, name, settings, ...)` creates an input with typed settings.
- `Filters.CreateSourceFilterAsync<T>(source, filterName, kind, settings, ct)` adds a typed filter.

**Media**

- `MediaInputs.PlayMediaAsync`, `PauseMediaAsync`, `StopMediaAsync` and `RestartMediaAsync` wrap
  `TriggerMediaActionAsync(name, MediaInputAction, ct)`.

**Screenshots**

- `Sources.GetSourceScreenshotBytesAsync(source, ...)` returns decoded image bytes.
- `Sources.GetSourceScreenshotOnCanvasBytesAsync(source, ...)` does the same at canvas dimensions.
- `Sources.SaveSourceScreenshotToFileAsync(source, filePath, ...)` writes to disk.

**Outputs**

- `Record.SetRecordActiveAndWaitAsync(activate, timeout, ct)`,
  `Stream.SetStreamActiveAndWaitAsync(...)` and `Outputs.SetVirtualCamActiveAndWaitAsync(...)` start
  or stop the output and wait for confirmation, returning the resulting `OutputState`.
- `Record.IsRecordActiveAsync(ct)`, `Stream.IsStreamActiveAsync(ct)` and
  `Outputs.IsVirtualCamActiveAsync(ct)` read current state.

**Application state**

- `Config.EnsureProfileActiveAsync(name, ct)` and `Config.EnsureSceneCollectionActiveAsync(name, ct)`
  switch only if needed, returning whether the target is active.
- `General.TriggerHotkeyAsync(hotkeyName, ct)` fires a hotkey by name.

## Events

Every event is available as an async sequence on its group. The stream subscribes for the lifetime
of the loop and unsubscribes when it ends:

```csharp
await foreach (var e in client.Scenes.CurrentProgramSceneChangedStream(cancellationToken: ct))
{
    Console.WriteLine($"Program scene is now {e.EventData.SceneName}");
}
```

Streams buffer a bounded number of events and drop the oldest when a consumer falls behind. Pass
`capacity` to change that.

The classic handler is on the same group:

```csharp
client.Scenes.CurrentProgramSceneChanged += (_, e) =>
    Console.WriteLine($"Program scene is now {e.EventData.SceneName}");
```

The group's event is the client's event, so both work at once.

`Connected`, `Disconnected`, `ConnectionFailed` and `AuthenticationFailure` are on the client, since
they belong to no protocol category.

To wait for a single occurrence, use `WaitForEventAsync`. It subscribes before returning, so you can
start the wait and then trigger the action:

```csharp
var changed = await client.WaitForEventAsync<CurrentProgramSceneChangedEventArgs>(ct);

var intro = await client.WaitForEventAsync<CurrentProgramSceneChangedEventArgs>(
    e => e.EventData.SceneName == "Intro",
    TimeSpan.FromSeconds(5),
    ct
);
```

It throws `ObsWebSocketTimeoutException` when the wait elapses.

## Common tasks

### Update a text source

```csharp
await client.Inputs.SetInputTextAsync("NewsTicker", "Breaking: Live now!", ct);

var settings = new TextGdiPlusInputSettings(Text: "Breaking: Live now!", WordWrap: true);
await client.Inputs.SetInputSettingsAsync("NewsTicker", settings, cancellationToken: ct);
```

`TextGdiPlusInputSettings`, `TextFreetype2InputSettings`, `BrowserSourceSettings` and the filter
settings types are built in, under `ObsWebSocket.Core.Protocol.Common.InputSettings` and
`.FilterSettings`.

### Save the replay buffer

```csharp
var status = await client.Outputs.GetReplayBufferStatusAsync(ct);
if (status.OutputActive)
{
    await client.Outputs.SaveReplayBufferAsync(ct);
}
```

### Create or update a browser source

```csharp
var current = await client.Inputs.GetInputSettingsAsync<BrowserSourceSettings>("StreamOverlay", ct);
Console.WriteLine($"Current URL: {current?.Url}");

await client.Inputs.SetInputSettingsAsync(
    "StreamOverlay",
    new BrowserSourceSettings(Url: "https://myoverlay.example.com", Width: 1920, Height: 1080),
    cancellationToken: ct
);
```

For settings this library does not model, define your own type and pass its `JsonTypeInfo`:

```csharp
[JsonSerializable(typeof(OverlaySettings))]
internal partial class MyContext : JsonSerializerContext { }

internal sealed record OverlaySettings(
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("css")] string? Css = null
);

await client.Inputs.SetInputSettingsAsync(
    "StreamOverlay",
    new OverlaySettings(Url: "https://myoverlay.example.com"),
    MyContext.Default.OverlaySettings,
    cancellationToken: ct
);
```

`overlay` comes before the cancellation token and defaults to true, merging your values onto the
existing settings. Pass `overlay: false` to replace them.

### Screenshots

```csharp
byte[]? png = await client.Sources.GetSourceScreenshotBytesAsync("Intro", "png", cancellationToken: ct);
await client.Sources.SaveSourceScreenshotToFileAsync("Intro", "shot.png", cancellationToken: ct);
```

## Batches

Send several requests in one round trip. Each returns a reference carrying its response type:

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

A request type may appear several times in one batch; each reference resolves to its own result.
`Sleep` is valid only inside a batch.

`TryGet` reports a failed or missing result instead of throwing. `Get` throws
`ObsWebSocketRequestException` carrying the OBS status code:

```csharp
if (!results.AllSucceeded())
{
    foreach (var failed in results.GetFailures())
    {
        Console.WriteLine($"{failed.RequestType}: {failed.RequestStatus.Comment}");
    }
}
```

With `haltOnFailure: true`, OBS stops at the first failure, so fewer results come back than requests
were sent. Reading a reference past that point throws, and `Count` reports how many ran.

`Add` covers anything the generated methods do not, including a raw `JsonElement`, with an overload
taking a `JsonTypeInfo<T>`:

```csharp
batch.Add("GetStats");
batch.Add("SetInputSettings", myJsonElement);
```

### Parallel batches

`RequestBatchExecutionType.Parallel` works, but OBS labels the results incorrectly. It collects them
in completion order and labels them from the submission order, so `requestType` and `requestId` on a
row may not match the `requestStatus` and `responseData` beside them. This happens inside OBS and
cannot be corrected here. See [#16](https://github.com/Agash/ObsWebSocket/issues/16).

Status and payload do come from the same object, so `Get` and the indexer throw rather than return
data under the wrong reference, and `TryGet` returns false. Results that do not depend on ordering
are still exact:

```csharp
BatchResults results = await client.CallBatchAsync(
    batch, executionType: RequestBatchExecutionType.Parallel, cancellationToken: ct);

bool everythingWorked = results.AllSucceeded();
int failureCount = results.GetFailures().Count();
```

`results.Raw` reaches every payload, and `GetData<T>` reads one without consulting the label, so a
batch where every request returns the same type is fully recoverable.

Use `Parallel` for a set of writes you only need a pass or fail on. When you need results attributed
to requests, send them concurrently instead; the client multiplexes on the request id:

```csharp
Task<GetVersionResponseData> version = client.General.GetVersionAsync(ct);
Task<GetStatsResponseData> stats = client.General.GetStatsAsync(ct);

await Task.WhenAll(version, stats);
```

That costs a round trip per request. Use a serial batch when the round trip is what you are saving.

## Raw requests

Every generated request wraps the same primitives, which stay available for requests this build does
not model, a newer OBS, or a vendor plugin:

```csharp
// Reference type response.
GetVersionResponseData? v = await client.CallAsync<GetVersionResponseData>("GetVersion", null, cancellationToken: ct);

// Value type response, including JsonElement. CallAsync is constrained to classes.
JsonElement? raw = await client.CallAsyncValue<JsonElement>("GetStats", null, cancellationToken: ct);

// Your own request type, with your own context.
[JsonSerializable(typeof(MyRequest))]
internal sealed partial class MyContext : JsonSerializerContext;

JsonElement? answer = await client.CallAsyncValue<JsonElement>(
    "SomeNewRequest", new MyRequest(1), MyContext.Default.MyRequest, cancellationToken: ct);

// Or a JsonElement built by hand.
using JsonDocument body = JsonDocument.Parse("""{"someField":1}""");
JsonElement? viaElement = await client.CallAsyncValue<JsonElement>(
    "SomeNewRequest", body.RootElement, cancellationToken: ct);

// A batch without the typed builder.
List<RequestResponsePayload<object>> results = await client.CallBatchAsync(
    [new BatchRequestItem("GetVersion", null), new BatchRequestItem("GetStats", null)],
    executionType: RequestBatchExecutionType.SerialRealtime,
    cancellationToken: ct);
```

Request data is written through a source-generated context, so it must be a `JsonElement`, a type
the library knows, or a type you supply metadata for. An anonymous object throws
`ObsWebSocketSerializationException`. `JsonSerializer.SerializeToElement` without a `JsonTypeInfo`,
and the `JsonNode` and `JsonObject` routes, work at runtime but carry `IL2026` and `IL3050`, so they
are not options under Native AOT.

Events and enums have the same escape hatch: `client.SceneCreated` remains alongside
`client.Scenes.SceneCreated`, and `ToWireValue()` and `FromWireValue()` convert an enum to and from
the protocol string.

## Protocol types

The protocol definition has one numeric type and describes enum-valued fields as plain strings. The
generated types narrow both.

**Numbers.** Fields holding whole numbers are generated as `int` or `long`, from an explicit list in
the generator rather than a rule over field names:

```csharp
long id = await client.SceneItems.FindSceneItemIdAsync("Intro", "Logo", ct) ?? throw new(...);
await client.SceneItems.SetSceneItemIndexAsync(new(sceneItemId: id, sceneItemIndex: 0, sceneName: "Intro"), ct);

long bytes = (await client.Stream.GetStreamStatusAsync(ct)).OutputBytes;
double volume = (await client.Inputs.GetInputVolumeAsync(new("Mic"), ct)).InputVolumeMul;
```

**Enums.** Fields carrying a protocol enum are typed as that enum on both the read and the write
side:

```csharp
client.Outputs.StreamStateChanged += (_, e) =>
{
    string what = e.EventData.OutputState switch
    {
        OutputState.Started => "live",
        OutputState.Starting or OutputState.Reconnecting => "coming up",
        OutputState.Stopped or OutputState.Stopping => "going down",
        OutputState.Unknown => "unrecognised",
        _ => "in between",
    };
};

await client.MediaInputs.TriggerMediaActionAsync("Stinger", MediaInputAction.Restart, ct);
```

A value this build does not know maps to the enum's zero member rather than throwing.

`mediaState`, `monitorType`, `sceneItemBlendMode` and `inputKind` have fixed vocabularies but are
typed as strings in the protocol and their values are never listed, so they stay strings. The wire
values are available as `const` strings on `ObsOutputState` and `ObsMediaInputAction`.

## Host integration

```csharp
builder.AddObsWebSocketClient("obs")   // reads ConnectionStrings:obs
       .WithAutoConnect()              // connects on start, disconnects on stop
       .WithHealthCheck();
```

The password can travel in the connection string or be set on the options; either way it is kept off
`ServerUri`. A connection that cannot be established at startup is logged rather than thrown, since
OBS is often started after the application, and reconnect takes over.

Options are read through `IOptionsMonitor`, so configuration changes take effect without a restart.
Timeouts and reconnect settings apply to the next call that uses them. Changing the endpoint,
password or transport reconnects.

To configure in code instead:

```csharp
builder.Services.AddObsWebSocketClient(o =>
{
    o.ServerUri = new Uri("ws://localhost:4455");
    o.Password = "secret";
    o.Format = SerializationFormat.MsgPack;
});
```

### Multiple OBS instances

Register clients by name and resolve them with `[FromKeyedServices]`:

```csharp
builder.Services.AddObsWebSocketClient("main", o => o.ServerUri = new Uri("ws://localhost:4455"))
       .WithAutoConnect()
       .WithHealthCheck();

builder.Services.AddObsWebSocketClient("booth", o => o.ServerUri = new Uri("ws://booth:4455"))
       .WithAutoConnect();

public sealed class Worker(
    [FromKeyedServices("main")] ObsWebSocketClient main,
    [FromKeyedServices("booth")] ObsWebSocketClient booth);
```

Each client gets its own options, connection service and health check named after its key.

## Errors

```csharp
try
{
    await client.Ui.SetStudioModeEnabledAsync(new(true), ct);
}
catch (ObsWebSocketRequestException ex)
{
    Console.WriteLine($"{ex.RequestType} failed with {ex.StatusCode}: {ex.Comment}");
}
catch (ObsWebSocketTimeoutException)
{
    // No response within the request timeout.
}
```

`StatusCode` is the `RequestStatusCode` enum, so a filter can name the reason:

```csharp
using ObsWebSocket.Core.Protocol.Generated;

catch (ObsWebSocketRequestException ex) when (ex.StatusCode is RequestStatusCode.ResourceNotFound)
{
    // The scene, input or filter does not exist.
}
```

`ObsWebSocketSerializationException` covers payloads that cannot be written or read. All three
derive from `ObsWebSocketException`.

## Reconnect

Reconnect delays grow by `ReconnectBackoffMultiplier`, are capped at `MaxReconnectDelayMs`, and
carry jitter so several clients recovering from one outage do not retry in lockstep. Authentication
failures are not retried.

`WithReconnectPipeline()` registers the default pipeline explicitly, which is useful when a host has
its own resilience configuration:

```csharp
builder.AddObsWebSocketClient("obs")
       .WithAutoConnect()
       .WithReconnectPipeline();
```

To replace the policy, register your own pipeline under
`ObsWebSocketResilience.ReconnectPipelineKey` after adding the client.

## Telemetry

Traces and metrics are published under the name `ObsWebSocket.Core`, inert until something
subscribes:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(ObsWebSocketDiagnostics.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(ObsWebSocketDiagnostics.MeterName));
```

One activity per request, and one per batch rather than per item. Counters cover requests sent,
requests failed, events received and reconnect attempts, plus a request duration histogram.
Instruments are created from `IMeterFactory`.

Timeouts and reconnect delays run on an injectable `TimeProvider`, so tests can drive them with
`FakeTimeProvider`.

## Serialization

JSON and MessagePack are both supported, selected with `Format`. Everything in this document behaves
the same on either.

## Example app

`ObsWebSocket.Example` is a host-based sample with configuration and DI.

- Interactive mode: a command loop, listed by `help`.
- Validation mode: `ObsWebSocket.Example run-transport-tests` runs the same checks over JSON and
  MessagePack against a scene, input and filter it creates and removes itself.

The validation run covers the settings helpers, event streams, `WaitForEventAsync`, the batch
builder, the raw path, typed enums, screenshots and handles. It also calls every read request and
every safely sendable write request, and fails on any response it cannot deserialize.

## Native AOT

```bash
dotnet publish ObsWebSocket.Example/ObsWebSocket.Example.csproj -c Release -r win-x64 --self-contained true
```

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md).

## License

MIT. See [`LICENSE.txt`](LICENSE.txt).
