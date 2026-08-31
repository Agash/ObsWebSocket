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

## Three levels

The client exposes the protocol at three levels. Most code lives in the middle one and reaches for
the others where they pay.

| Level | Looks like | Reach for it when |
|---|---|---|
| [Handles](#handles) | `client.Input("Mic").SetMuteAsync(true, ct)` | Several calls concern one scene, input, source, item or filter; identity has to survive a rename; you already hold a uuid |
| [Category groups](#the-category-groups) | `client.Inputs.SetInputMuteAsync(new("Mic", true), ct)` | Anything. One method per protocol request, plus the helpers |
| [Raw](#dropping-to-the-low-level) | `client.CallAsync<T>("SetInputMute", data, ct)` | A request this build does not model: a newer OBS, a vendor plugin |

Each level forwards to the one below it, so mixing them costs nothing and no level hides anything
the one below can reach.

Handles cover the 66 requests that act on one named thing. The rest — `GetVersion`, `GetStats`,
record and stream control, profiles, video settings — are not about a particular thing, so they
exist only on their group. The hand-written helpers (`SetInputVolumeDbAsync`,
`SwitchProgramSceneAndWaitAsync`, the typed settings pairs) live on the group too.

## The category groups

The client mirrors the categories the OBS protocol defines. Requests, event streams and the
helpers this library adds all sit in the group their category owns:

```csharp
await client.Scenes.GetSceneListAsync(new(), ct);                                   // generated request
await client.Scenes.SwitchProgramSceneAndWaitAsync("Intro", cancellationToken: ct);  // convenience
await client.Inputs.SetInputVolumeDbAsync("Mic", -6, ct);
await client.SceneItems.SetSceneItemEnabledAsync("Intro", "Logo", false, ct);

client.Scenes.CurrentProgramSceneChanged += (_, e) => { };                           // classic event
await foreach (var e in client.Scenes.CurrentProgramSceneChangedStream(cancellationToken: ct)) { break; }
```

The groups are `Canvases`, `Config`, `Filters`, `General`, `Inputs`, `MediaInputs`, `Outputs`,
`Record`, `SceneItems`, `Scenes`, `Sources`, `Stream`, `Transitions` and `Ui`. They come from the
protocol definition, so a refresh that recategorises a request moves it here too.

`WaitForEventAsync` and `CallBatchAsync` stay directly on the client, since neither belongs to one
category.

## Handles

Every OBS request that acts on something takes its identity as two optional fields, a name and a
uuid, and resolves them in a fixed order: a uuid wins outright, a name is read only when no uuid was
sent, the canvas is consulted only on the name path, and neither field present is
`MissingRequestField`. So `new SetCurrentProgramSceneRequestData()` compiles and fails at runtime,
and passing both silently ignores the name.

A handle makes that choice once. A string is a name, a `Guid` is a uuid:

```csharp
await client.Scene("Intro").SetCurrentProgramAsync(ct);
await client.Scene(sceneGuid).SetNameAsync("Outro", ct);
await client.Input("Mic").SetMuteAsync(true, ct);
await client.Input("Mic").Filter("EQ").SetEnabledAsync(false, ct);
```

The entry points are `Scene`, `Input`, `Source`, `SceneItem` and `Filter`. Each carries every request
the protocol defines about that kind of thing, named without the part the handle already says:
`SetSceneItemEnabled` is `SetEnabledAsync` on a scene item, `GetInputMute` is `GetMuteAsync` on an
input. The protocol name stays in the XML docs and on the category group.

Handles hold identity, nothing else. They cache no state and are safe to keep for the life of an
application.

### Resolving a name to a uuid

A name handle is fine when you just typed the name. A uuid handle survives a rename, and is the only
form OBS will accept once it drops names, which the maintainers have said is the plan.

Resolving costs a round trip, so it is explicit:

```csharp
SceneOperations intro = await client.Scene("Intro").ResolveAsync(ct);
// intro.Handle.IsResolved is true; a rename in OBS can no longer move it
```

The protocol has no narrow lookup — nothing answers "what is the uuid of the scene called X" — so
this is `GetSceneList` and a scan. The list is not wasted: on a miss you get the names that do
exist, where OBS itself can only answer `ResourceNotFound`.

```
ObsWebSocketResourceNotFoundException: No scene named 'Intor'. Available: 'Intro', 'Gameplay', 'BRB'.
```

### Handles that cost nothing

An event already says which scene it concerns, by uuid. Reading the name back off it and addressing
by name again is the round trip and the rename race that the uuid was there to avoid:

```csharp
client.Scenes.CurrentProgramSceneChanged += async (_, e) =>
    await client.Scene(e.EventData.Scene).GetItemListAsync();   // already resolved
```

Forty-seven events and responses carry one, including the creation requests, which answer with the
uuid of what they just made:

```csharp
CreateSceneResponseData created = await client.Scenes.CreateSceneAsync(new("Intro"), ct);
await client.Scene(created.Scene).SetCurrentProgramAsync(ct);   // no lookup
```

### Scene items

Scene items are the one case where a lookup is not a convenience: OBS addresses them by a number
that only `GetSceneItemId` reports. A scene item known by its source name is therefore a different
type from one that can be acted on, and the missing lookup is a compile error rather than a runtime
one:

```csharp
SceneItemOperations logo = await client.Scene("Intro").ItemAsync("Logo", cancellationToken: ct);
await logo.SetEnabledAsync(false, ct);
await logo.Scene.GetItemListAsync(ct);                       // navigate back up

await client.Scene("Intro").Item(3).SetIndexAsync(0, ct);    // an id needs no lookup
```

`Item(int)` and `Filter(string)` never send anything, since an id and a filter name are already the
whole identity.

### Canvases

A canvas has no name in the protocol: every canvas-scoped request takes a uuid, and `canvasName`
appears only in `GetCanvasList`. `CanvasHandle` is the one handle whose name form cannot be sent
anywhere before it is resolved:

```csharp
CanvasHandle vertical = await client.Canvases.ResolveAsync("Vertical", ct);
await client.Scene(vertical.Scene("Intro")).GetItemListAsync(ct);
```

Omitting the canvas means the main one, which is `CanvasHandle.Main` rather than a null check at
every call site. A canvas only scopes a *name*: OBS ignores `canvasUuid` beside a uuid, so a resolved
handle drops it.

## The helper set

Alongside the generated request per protocol request, each group carries helpers for things that
otherwise take several calls or a lookup. These are hand-written, so they exist only on the group,
not on a handle.

Every typed settings helper has two overloads: an implicit one for library-registered types, and an
explicit one taking a `JsonTypeInfo<T>` for consumer-provided types. Use the explicit overload to
stay AOT-safe.

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

The group's event *is* the client's event, so both work at once and a handler added through one can
be removed through the other.

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

It throws `ObsWebSocketTimeoutException` when the wait elapses, the same type a request
timeout raises, so one `catch (ObsWebSocketException)` covers both.

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

`RequestBatchExecutionType.Parallel` works, but OBS mislabels what comes back. It collects results in
completion order and labels them from the submission order, so on any row the `requestType` and
`requestId` name a different request than the `requestStatus` and `responseData` beside them. That
happens inside OBS, so it cannot be corrected here. See
[#16](https://github.com/Agash/ObsWebSocket/issues/16).

Only the labelling is wrong — status and payload do come from the same object — so `Get` and the
indexer throw rather than return data under the wrong reference, and `TryGet` returns `false`.
Anything that does not depend on which row is which stays exact:

```csharp
BatchResults results = await client.CallBatchAsync(
    batch, executionType: RequestBatchExecutionType.Parallel, cancellationToken: ct);

bool everythingWorked = results.AllSucceeded();    // reliable: order does not change the verdict
int failureCount = results.GetFailures().Count();  // reliable count, unreliable names
```

`results.Raw` still reaches every payload, and `GetData<T>` reads one without consulting the label,
so a batch where every request returns the same type is fully recoverable. A batch of mixed types is
not: `GetData<T>` rejects a payload carrying none of `T`'s fields, but that rejects rather than
identifies, and two records with the same field names cannot be told apart.

So `Parallel` suits a set of writes you want applied as fast as possible and only need a pass/fail
on. When you need answers attributed to requests, send them concurrently instead — the client
multiplexes on the request id:

```csharp
Task<GetVersionResponseData> version = client.General.GetVersionAsync(ct);
Task<GetStatsResponseData> stats = client.General.GetStatsAsync(ct);

await Task.WhenAll(version, stats);
```

That costs a round trip per request. Use a serial batch when the round trip is what you are saving.

## Dropping to the low level

Every generated request is a thin wrapper over the same primitives, which stay available for a
request this build does not model, an OBS newer than this library, or a vendor plugin:

```csharp
// A request with a reference type response.
GetVersionResponseData? v = await client.CallAsync<GetVersionResponseData>("GetVersion", null, cancellationToken: ct);

// A value type response, JsonElement included. CallAsync is constrained to classes, so a struct
// response goes through CallAsyncValue.
JsonElement? raw = await client.CallAsyncValue<JsonElement>("GetStats", null, cancellationToken: ct);

// Request data is written through a source generated context, so it must be a JsonElement, a type
// the library knows, or a type you supply metadata for. An anonymous object has no metadata
// anywhere and throws ObsWebSocketSerializationException.

// Your own type, with your own context. AOT safe, and nothing to hand build.
[JsonSerializable(typeof(MyRequest))]
internal sealed partial class MyContext : JsonSerializerContext;

JsonElement? answer = await client.CallAsyncValue<JsonElement>(
    "SomeNewRequest", new MyRequest(1), MyContext.Default.MyRequest, cancellationToken: ct);

// Or a JsonElement built by hand, when a one-off payload does not deserve a type.
using JsonDocument body = JsonDocument.Parse("""{"someField":1}""");
JsonElement? viaElement = await client.CallAsyncValue<JsonElement>(
    "SomeNewRequest", body.RootElement, cancellationToken: ct);

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

Those two are the AOT-safe ways to build a payload. `JsonSerializer.SerializeToElement` without a
`JsonTypeInfo`, and the `JsonNode` and `JsonObject` routes, all work at runtime but carry `IL2026`
and `IL3050`, so they are not options under Native AOT.

Events and enums have the same escape hatch: `client.SceneCreated` remains alongside
`client.Scenes.SceneCreated`, and `ToWireValue()` / `FromWireValue()` convert an enum to and from the
protocol string when you are building a payload by hand.

## Protocol types

The protocol definition is looser than C#: it has one numeric type because JSON does, and it types
enum-valued fields as plain strings. The generated surface narrows both, so callers get the C# type
rather than the wire representation.

**Numbers.** A scene item id and a volume multiplier are both `Number` with a `>= 0` restriction, so
which ones are integral is not recoverable from the definition. Fields holding whole numbers are
generated as `int` or `long`, from an explicit list in the generator rather than a rule over field
names, so a volume can never be truncated by a naming coincidence:

```csharp
long id = await client.SceneItems.FindSceneItemIdAsync("Intro", "Logo", ct) ?? throw new(...);
await client.SceneItems.SetSceneItemIndexAsync(new(sceneItemId: id, sceneItemIndex: 0, sceneName: "Intro"), ct);

long bytes = (await client.Stream.GetStreamStatusAsync(ct)).OutputBytes;
double volume = (await client.Inputs.GetInputVolumeAsync(new("Mic"), ct)).InputVolumeMul;
```

The width comes from what fills the field upstream, not from how large the value looks. Where
obs-websocket validates a range — resolutions are 8..4096, indices 0..8192 — `int` is enough.
Where it copies a value straight out of libobs, the C type decides: scene item ids and settings
durations are `int64_t`, frame counters and output dimensions are `uint32_t`, and session message
counts are `uint64_t`, so all of those are `long`. This is not cosmetic. An out-of-range value does
not truncate one field, it fails the whole response — an idle virtual camera reporting an
uninitialised `outputHeight` made every `GetOutputList` unreadable.

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
    Console.WriteLine($"{ex.RequestType} failed with {ex.StatusCode}: {ex.Comment}");
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

- **Interactive mode**: a command loop — `help` lists it. `mute`, `list-filters` and `toggle-filter`
  go through handles, `set-text` and `get-input-settings` deliberately do not, and `resolve` shows
  what resolving buys and costs.
- **Validation mode**: `ObsWebSocket.Example run-transport-tests` runs the same checks on JSON and
  on MessagePack against a scene, input and filter it creates and removes itself.

The validation run asserts real values rather than that a call returned. It covers the three
settings modes, event streams and their buffering, `WaitForEventAsync`, the typed batch builder
including duplicate request types and partial failure, parallel batches, the low-level path, typed
enums, screenshots, and the handles — including that a uuid handle still resolves after a rename and
a name handle does not. It also calls every read request and every safely sendable write request in
the protocol, and fails on any response it cannot deserialize.

## Native AOT

```bash
dotnet publish ObsWebSocket.Example/ObsWebSocket.Example.csproj -c Release -r win-x64 --self-contained true
```

## Contributing

Contributions are welcome. See [`CONTRIBUTING.md`](CONTRIBUTING.md).

## License

MIT. See [`LICENSE.txt`](LICENSE.txt).
