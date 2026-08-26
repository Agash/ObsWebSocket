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

> **OBS WebSocket v5 only** (OBS Studio 28+). Enable the server via *Tools → WebSocket Server Settings* in OBS.

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

        await foreach (var e in client.Scenes.CurrentProgramSceneChangedStream(cancellationToken: ct))
        {
            Console.WriteLine($"Scene changed: {e.EventData.SceneName}");
        }
    }
}
```

## Everything is grouped by category

The client mirrors the categories the OBS protocol defines. Requests, event streams and the
conveniences this library adds all sit in the group their category owns, so there is one way to
reach anything:

```csharp
await client.Scenes.GetSceneListAsync(new(), ct);                                   // generated request
await client.Scenes.SwitchProgramSceneAndWaitAsync("Intro", cancellationToken: ct);  // convenience
await client.Scenes.CurrentProgramSceneChangedStream(cancellationToken: ct);         // event stream
await client.Inputs.SetInputVolumeDbAsync("Mic", -6, ct);
await client.SceneItems.SetSceneItemEnabledAsync("Intro", "Logo", false, ct);
```

The groups are `Canvases`, `Config`, `Filters`, `General`, `Inputs`, `MediaInputs`, `Outputs`,
`Record`, `SceneItems`, `Scenes`, `Sources`, `Stream`, `Transitions` and `Ui`. They come from the
protocol definition, so a refresh that recategorises a request moves it here too.

`WaitForEventAsync` and `CallBatchAsync` stay directly on the client, since neither belongs to one
category.

## The helper set

Alongside the generated request per protocol request, each group carries conveniences for things
that otherwise take several calls or a lookup. Every typed settings helper has two overloads: an
implicit one for library-registered types, and an explicit one taking a `JsonTypeInfo<T>` for
consumer-provided types. Use the explicit overload to stay AOT-safe.

**Settings read and write**

| Helper | Notes |
|---|---|
| `Inputs.GetInputSettingsAsync<T>` / `SetInputSettingsAsync<T>` | Input settings; Set supports `overlay` |
| `Inputs.GetInputDefaultSettingsAsync<T>` | Defaults for a given input kind |
| `Filters.GetSourceFilterSettingsAsync<T>` / `SetSourceFilterSettingsAsync<T>` | Filter settings; Set supports `overlay` |
| `Filters.GetSourceFilterDefaultSettingsAsync<T>` | Defaults for a given filter kind |
| `Transitions.GetCurrentSceneTransitionSettingsAsync<T>` / `SetCurrentSceneTransitionSettingsAsync<T>` | Transition settings |
| `Outputs.GetOutputSettingsAsync<T>` / `SetOutputSettingsAsync<T>` | Output settings |
| `Config.GetStreamServiceSettingsAsync<T>` / `SetStreamServiceSettingsAsync<T>` | Stream service settings |

Most take optional parameters ahead of the cancellation token, so pass it as `cancellationToken: ct`.

**Scenes and scene items**

- `Scenes.SwitchProgramSceneAsync(scene, ct)` and `Scenes.SwitchPreviewSceneAsync(scene, ct)` switch
  a scene. Optional `transitionName` and `transitionDurationMs` apply to that switch only.
- `Scenes.SwitchProgramSceneAndWaitAsync` and `Scenes.SwitchPreviewSceneAndWaitAsync` do the same
  and wait for the event confirming it.
- `SceneItems.SetSceneItemEnabledAsync(scene, sourceName, isEnabled, ct)` returns the resulting
  state. Leave `isEnabled` null to toggle. An overload takes the numeric item id instead.
- `SceneItems.FindSceneItemIdAsync(scene, sourceName, ct)` returns `int?`, null rather than throwing
  when the item is not in the scene.
- `Sources.SourceExistsAsync(name, ct)` and `Scenes.SceneExistsAsync(name, ct)` check existence.

**Inputs and filters**

- `Inputs.SetInputTextAsync(name, text, ct)` is shorthand for updating text source content.
- `Inputs.SetInputVolumeDbAsync(name, db, ct)` and `Inputs.SetInputVolumeMulAsync(name, mul, ct)`
  each pick one unit. The underlying request accepts either and fails when given neither.
- `Inputs.SetInputMutesAsync(inputMutes, ct)` sets many mute states in one batch and returns the
  results, so a caller sees which inputs OBS rejected.
- `Inputs.CreateInputAsync<T>(kind, name, settings, ...)` creates an input with typed settings.
- `Filters.CreateSourceFilterAsync<T>(source, filterName, kind, settings, ct)` adds a typed filter.

**Media**

- `MediaInputs.PlayMediaAsync`, `PauseMediaAsync`, `StopMediaAsync` and `RestartMediaAsync` are
  shorthands over `TriggerMediaActionAsync(name, MediaInputAction, ct)`.

**Screenshots**

- `Sources.GetSourceScreenshotBytesAsync(source, ...)` returns decoded image bytes.
- `Sources.GetSourceScreenshotOnCanvasBytesAsync(source, ...)` does the same at canvas dimensions.
- `Sources.SaveSourceScreenshotToFileAsync(source, filePath, ...)` writes straight to disk.

**Outputs**

- `Record.SetRecordActiveAndWaitAsync(activate, timeout, ct)`,
  `Stream.SetStreamActiveAndWaitAsync(...)` and `Outputs.SetVirtualCamActiveAndWaitAsync(...)` start
  or stop the output and wait for OBS to confirm, returning the resulting `OutputState`.
- `Record.IsRecordActiveAsync(ct)`, `Stream.IsStreamActiveAsync(ct)` and
  `Outputs.IsVirtualCamActiveAsync(ct)` read current state.

**Application state**

- `Config.EnsureProfileActiveAsync(name, ct)` and `Config.EnsureSceneCollectionActiveAsync(name, ct)`
  switch only if needed, returning whether the target is active rather than throwing when it does
  not exist.
- `General.TriggerHotkeyAsync(hotkeyName, ct)` fires a hotkey by name.

## Observing events

Every OBS event is exposed as an async sequence on its category group. The stream subscribes for the
lifetime of the loop and unsubscribes when it ends, so there is no handler bookkeeping:

```csharp
await foreach (var e in client.Scenes.CurrentProgramSceneChangedStream(cancellationToken: ct))
{
    Console.WriteLine($"Program scene is now {e.EventData.SceneName}");
}
```

Streams buffer a bounded number of events and drop the oldest when a consumer falls behind, so a
slow loop cannot stall the receive loop. Pass `capacity` to change that.

The classic handler sits on the same group, so subscribing and streaming read alike and there is no
second place to look:

```csharp
client.Scenes.CurrentProgramSceneChanged += (_, e) =>
    Console.WriteLine($"Program scene is now {e.EventData.SceneName}");
```

Both work over the same event at once. The group's event is the client's event, so a handler added
through one can be removed through the other; `client.CurrentProgramSceneChanged` remains for the
low-level path, the way `CallAsync` remains alongside the generated requests.

Connection lifecycle events stay on the client, since `Connected`, `Disconnected`,
`ConnectionFailed` and `AuthenticationFailure` belong to no protocol category.

To wait for a single occurrence, use `WaitForEventAsync`. It subscribes before returning, so you can
start the wait and then trigger the action without racing it:

```csharp
var changed = await client.WaitForEventAsync<CurrentProgramSceneChangedEventArgs>(ct);

var intro = await client.WaitForEventAsync<CurrentProgramSceneChangedEventArgs>(
    e => e.EventData.SceneName == "Intro",
    TimeSpan.FromSeconds(5),
    ct
);
```

It throws `TimeoutException` when the wait elapses.

## Common use cases

### Update a text source

```csharp
await client.Inputs.SetInputTextAsync("NewsTicker", "Breaking: Live now!", ct);

// or several properties at once, with a typed settings object
var settings = new TextGdiPlusInputSettings(Text: "Breaking: Live now!", WordWrap: true);
await client.Inputs.SetInputSettingsAsync("NewsTicker", settings, cancellationToken: ct);
```

`TextGdiPlusInputSettings` is a built-in library type, as are `TextFreetype2InputSettings`,
`BrowserSourceSettings` and the filter settings types, which live in
`ObsWebSocket.Core.Protocol.Common.InputSettings` and `.FilterSettings`.

### Check and save the replay buffer

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

Define your own type to target exactly what you need, and stay AOT-safe by passing its `JsonTypeInfo`:

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

Both `Set` overloads take `overlay` before the cancellation token. It defaults to `true`, merging
your values onto the existing settings; pass `overlay: false` to replace them.

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

`results.Get(reference)` restates neither the position nor the type, so a request type may appear
many times in one batch and each reference still resolves to its own result.

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

With `haltOnFailure: true` OBS stops at the first failure, so fewer results come back than requests
were sent; reading a reference past that point throws, and `Count` reports how many ran.

`Add` covers anything the generated methods do not, including a raw `JsonElement`, and an overload
taking a `JsonTypeInfo<T>` keeps a custom payload AOT-safe:

```csharp
batch.Add("GetStats");
batch.Add("SetInputSettings", myJsonElement);
```

### Running requests in parallel

`RequestBatchExecutionType.Parallel` works, but OBS mislabels what comes back. It collects results
in completion order and labels them from the submission order, so on any one row the
`requestType` and `requestId` belong to a different request than the `requestStatus` and
`responseData` beside them. That happens before the response leaves OBS, so it cannot be corrected
here. See [#16](https://github.com/Agash/ObsWebSocket/issues/16).

Only the labelling is wrong. `requestStatus` and `responseData` come from the same object, so each
row's status does belong to the payload beside it; it is the `requestType` and `requestId` on that
row that name a different request. `Get` and the indexer therefore throw rather than hand back data
under the wrong reference, and `TryGet` reports `false`.

Nothing is lost, though, and `Raw` still reaches all of it. `GetData<T>` reads the payload without
consulting the label, so every response is recoverable as a set:

```csharp
BatchResults results = await client.CallBatchAsync(
    batch, executionType: RequestBatchExecutionType.Parallel, haltOnFailure: false, cancellationToken: ct);

foreach (RequestResponsePayload<object> row in results.Raw)
{
    if (!row.RequestStatus.Result)
    {
        Console.WriteLine($"one request failed with {row.RequestStatus.Code}");
        continue;   // the code is right, the requestType naming it is not
    }

    // Correct data, from one of the requests in the batch. Which one is not knowable.
    GetSceneItemListResponseData? data = row.GetData<GetSceneItemListResponseData>();
}
```

That works when every request in the batch returns the **same** type, so it does not matter which
row is which, and when the order is not what you needed.

A parallel batch of **different** request types is a different matter, and the transport decides
whether it is merely awkward or actively unsafe:

- On JSON, a payload read as the wrong record throws `ObsWebSocketSerializationException`, so you
  can try each type you expect and let the mismatch tell you. Ugly, but sound.
- On MessagePack it is **not detectable**. The format maps by key name, so reading a payload as the
  wrong record quietly leaves every unmatched property at its default and returns an object. A
  reading of `cpu=0.00, memory=0.0` is indistinguishable from a genuine one.

So do not mix request types in a parallel batch and expect to sort the results out afterwards. Use a
serial batch, or concurrent requests.

Anything that does not depend on which row is which stays exact:

```csharp
ObsBatchBuilder batch = new();
foreach (string input in inputs)
{
    _ = batch.Inputs.SetInputMute(new(inputName: input, inputMuted: true));
}

BatchResults results = await client.CallBatchAsync(
    batch, executionType: RequestBatchExecutionType.Parallel, cancellationToken: ct);

bool everythingWorked = results.AllSucceeded();   // reliable: order does not change the verdict
int failureCount = results.GetFailures().Count(); // reliable count, unreliable names
```

So `Parallel` suits a set of writes you want applied as fast as possible, where you only need to
know whether they all took. It does not suit reading anything back.

When you need results attributed, use concurrent requests rather than a parallel batch. The client
multiplexes on the request id, so anything in flight at once is matched back to its own caller:

```csharp
Task<GetVersionResponseData> version = client.General.GetVersionAsync(ct);
Task<GetStatsResponseData> stats = client.General.GetStatsAsync(ct);
Task<GetSceneItemListResponseData>[] perScene =
[
    .. sceneNames.Select(n => client.SceneItems.GetSceneItemListAsync(new(sceneName: n), ct)),
];

await Task.WhenAll([version, stats, .. perScene.Cast<Task>()]);

Console.WriteLine(version.Result.ObsVersion);   // each result belongs to its own request
```

That costs one round trip per request rather than one for the set. Use a serial batch when the round
trip is what you are saving, and concurrent requests when you need the answers attributed.

## Dropping to the low level

Nothing above is a wall. Every generated request is a thin wrapper over the same primitives, and
they stay available for a request this build does not model, an OBS newer than this library, or a
vendor plugin:

```csharp
// A request with a reference type response.
GetVersionResponseData? v = await client.CallAsync<GetVersionResponseData>("GetVersion", null, cancellationToken: ct);

// A value type response, JsonElement included. CallAsync is constrained to classes, so a struct
// response goes through CallAsyncValue.
JsonElement? raw = await client.CallAsyncValue<JsonElement>(
    "SomeNewRequest", new { someField = 1 }, cancellationToken: ct);

// A batch assembled by hand, without the typed builder.
List<RequestResponsePayload<object>> results = await client.CallBatchAsync(
    [new BatchRequestItem("GetVersion", null), new BatchRequestItem("GetStats", null)],
    executionType: RequestBatchExecutionType.SerialRealtime,
    cancellationToken: ct);

foreach (RequestResponsePayload<object> result in results)
{
    GetVersionResponseData? data = result.GetData<GetVersionResponseData>();
}
```

The same applies to events and enums: `client.SceneCreated` remains alongside
`client.Scenes.SceneCreated`, and `ToWireValue()` / `FromWireValue()` convert an enum to and from
the protocol string when you are building a payload by hand.

## Protocol types

The protocol definition is looser than C#: it has one numeric type because JSON does, and it types
enum-valued fields as plain strings. The generated surface narrows both, so callers get the C# type
rather than the wire representation.

**Numbers.** A scene item id and a volume multiplier are both `Number` with a `>= 0` restriction, so
which ones are integral is not recoverable from the definition. Fields holding whole numbers are
generated as `int` or `long`, from an explicit list in the generator rather than a rule over field
names, so a volume can never be truncated by a naming coincidence:

```csharp
int id = await client.SceneItems.FindSceneItemIdAsync("Intro", "Logo", ct) ?? throw new(...);
await client.SceneItems.SetSceneItemIndexAsync(new(sceneItemId: id, sceneItemIndex: 0, sceneName: "Intro"), ct);

long bytes = (await client.Stream.GetStreamStatusAsync(ct)).OutputBytes;
double volume = (await client.Inputs.GetInputVolumeAsync(new("Mic"), ct)).InputVolumeMul;
```

**Enums.** Fields carrying a protocol enum are that enum, on both the read and the write side, so
there is nothing to convert at the call site:

```csharp
client.Outputs.StreamStateChanged += (_, e) =>
{
    string what = e.EventData.OutputState switch
    {
        OutputState.Started => "live",
        OutputState.Starting or OutputState.Reconnecting => "coming up",
        OutputState.Stopped or OutputState.Stopping => "going down",
        OutputState.Unknown => "in a state this build does not recognise",
        _ => "in between",
    };
};

await client.MediaInputs.TriggerMediaActionAsync("Stinger", MediaInputAction.Restart, ct);
```

A value OBS sends that this build does not know maps to the enum's zero member rather than throwing,
so a state added by a newer OBS does not fail the whole message.

This covers the enums the protocol declares. `mediaState`, `monitorType`, `sceneItemBlendMode` and
`inputKind` carry fixed vocabularies too, but the protocol types them as strings and never lists
their values, so they stay strings rather than being given an enum this library would have to keep
correct by hand.

The wire values also remain as `const` strings on `ObsOutputState` and `ObsMediaInputAction`, and
`ToWireValue()` converts an enum back, for payloads built by hand. See
[Dropping to the low level](#dropping-to-the-low-level).

## Host integration

```csharp
builder.AddObsWebSocketClient("obs")   // reads ConnectionStrings:obs
       .WithAutoConnect()              // connects on start, disconnects on stop
       .WithHealthCheck();
```

The password may travel in the connection string or be set on the options; either way it is kept off
`ServerUri`. A connection that cannot be established at startup is logged rather than thrown, because
OBS is often started after the application, and reconnect takes over from there.

Options are read through `IOptionsMonitor`, so editing configuration takes effect without a restart.
Timeouts and reconnect settings apply to the next call that uses them; changing the endpoint,
password or transport reconnects, which `WithAutoConnect` performs.

To configure in code instead:

```csharp
builder.Services.AddObsWebSocketClient(o =>
{
    o.ServerUri = new Uri("ws://localhost:4455");
    o.Password = "secret";
    o.Format = SerializationFormat.MsgPack;
});
```

Options are validated when the client is resolved, so a missing or malformed `ServerUri` fails at
startup with the offending option named, rather than on the first connection attempt.

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

Each client gets its own options instance, its own connection service and a health check named after
its key, so the two do not collide.

## Errors

Failures are typed, so they can be caught by category rather than matched by message:

```csharp
try
{
    await client.Ui.SetStudioModeEnabledAsync(new(true), ct);
}
catch (ObsWebSocketRequestException ex)
{
    Console.WriteLine($"{ex.RequestType} failed with {ex.Status?.Code}: {ex.Comment}");
}
catch (ObsWebSocketTimeoutException)
{
    // No response within the request timeout.
}
```

`StatusCode` reports the status as the `RequestStatusCode` enum, so a filter can name the reason
instead of a number:

```csharp
using ObsWebSocket.Core.Protocol.Generated;

catch (ObsWebSocketRequestException ex) when (ex.StatusCode is RequestStatusCode.ResourceNotFound)
{
    // The scene, input or filter does not exist.
}
```

`ObsWebSocketSerializationException` covers payloads that cannot be written or read, and all three
derive from `ObsWebSocketException`.

Requests return their response data non-nullable; a successful request that carries no payload
raises `ObsWebSocketException` rather than handing back null.

## Reconnect

Reconnect delays grow by `ReconnectBackoffMultiplier`, are capped at `MaxReconnectDelayMs`, and carry
jitter so several clients recovering from one outage do not retry in lockstep. Authentication
failures are never retried, since they cannot succeed on a second attempt.

`WithReconnectPipeline()` registers the default pipeline explicitly, which is worth doing when a
host has its own resilience configuration and you want this client's to be visible alongside it:

```csharp
builder.AddObsWebSocketClient("obs")
       .WithAutoConnect()
       .WithReconnectPipeline();
```

To replace the policy outright rather than tune those options, register your own pipeline under
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
requests failed, events received and reconnect attempts, plus a request-duration histogram. The
instruments are created from `IMeterFactory`, so they belong to the container that built them.

Timeouts and reconnect delays run on an injectable `TimeProvider`, so tests can drive them with
`FakeTimeProvider` instead of waiting.

## Serialization

JSON and MessagePack are both supported, selected with `Format`. Everything in this document behaves
identically on either, and the validation suite exercises both.

## Example app

`ObsWebSocket.Example` is a host-based sample with configuration and DI.

- **Interactive mode**: command loop (`help`, `version`, `scene`, `watch`, `media`, `status`, `batch-example`, and more)
- **Transport validation mode**: exercises the surface on JSON and MessagePack, then enters the interactive loop
- **One-shot mode**: `ObsWebSocket.Example run-transport-tests`

`run-transport-tests` creates its own scene and input, so it does not depend on a particular OBS
layout, and removes them afterwards. On each transport it asserts real values for the settings modes,
event streams and buffering, `WaitForEventAsync`, the typed batch builder including duplicate request
types, partial failure and truncation, typed protocol enums, screenshots, and the scene, input,
volume and output helpers.

## Native AOT

```bash
dotnet publish ObsWebSocket.Example/ObsWebSocket.Example.csproj -c Release -r win-x64 --self-contained true
```

## Contributing

Contributions are welcome. See [`CONTRIBUTING.md`](CONTRIBUTING.md).

## License

MIT. See [`LICENSE.txt`](LICENSE.txt).
