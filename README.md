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

builder.AddObsWebSocketClient("obs");   // endpoint from ConnectionStrings:obs
builder.Services.WithAutoConnect();     // connect on start, disconnect on stop
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

        await foreach (var e in client.CurrentProgramSceneChangedStream(cancellationToken: ct))
        {
            Console.WriteLine($"Scene changed: {e.EventData.SceneName}");
        }
    }
}
```

## Everything is grouped by category

The client mirrors the categories the OBS protocol defines, and the conveniences this library adds
sit in the same group as the requests they wrap, so there is one way to reach anything:

```csharp
await client.Scenes.GetSceneListAsync(new(), ct);                                   // generated request
await client.Scenes.SwitchProgramSceneAndWaitAsync("Intro", cancellationToken: ct);  // convenience
await client.Inputs.SetInputVolumeDbAsync("Mic", -6, ct);
await client.SceneItems.SetSceneItemEnabledAsync("Intro", "Logo", false, ct);
```

The groups are `Canvases`, `Config`, `Filters`, `General`, `Inputs`, `MediaInputs`, `Outputs`,
`Record`, `SceneItems`, `Scenes`, `Sources`, `Stream`, `Transitions` and `Ui`. They come from the
protocol definition, so a refresh that recategorises a request moves it here too.

`WaitForEventAsync` and `CallBatchAsync` stay directly on the client, since neither belongs to one
category.

## Observing events

Every OBS event is exposed as an async sequence. The stream subscribes for the lifetime of the loop
and unsubscribes when it ends, so there is no handler bookkeeping:

```csharp
await foreach (var e in client.CurrentProgramSceneChangedStream(cancellationToken: ct))
{
    Console.WriteLine($"Program scene is now {e.EventData.SceneName}");
}
```

Streams buffer a bounded number of events and drop the oldest when a consumer falls behind, so a
slow loop cannot stall the receive loop. Pass `capacity` to change that.

The classic events still work, including alongside a stream over the same event:

```csharp
client.CurrentProgramSceneChanged += (_, e) =>
    Console.WriteLine($"Program scene is now {e.EventData.SceneName}");
```

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

> `RequestBatchExecutionType.Parallel` is best avoided when you care about the results. OBS collects
> them in completion order but labels them from the submission order, so every result carries
> another request's `responseData` and `requestStatus`. That happens before the response leaves OBS,
> so it cannot be corrected here; references refuse to resolve on such a batch and say why. See
> [#16](https://github.com/Agash/ObsWebSocket/issues/16).

## Typed protocol enums

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

The wire constants remain available as `const` strings on `ObsOutputState` and `ObsMediaInputAction`,
and `ToWireValue()` converts an enum back.

## Host integration

```csharp
builder.AddObsWebSocketClient("obs");          // reads ConnectionStrings:obs
builder.Services.WithAutoConnect();            // connects on start, disconnects on stop
builder.Services.AddHealthChecks().AddObsWebSocket();
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
    Console.WriteLine($"{ex.RequestType} failed with {ex.Status?.Code}: {ex.Comment}");
}
catch (ObsWebSocketTimeoutException)
{
    // No response within the request timeout.
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
