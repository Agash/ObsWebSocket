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

## What You Get

- Strongly typed request/response DTOs generated from OBS protocol
- Strongly typed OBS event args
- Async-first API (`Task`, `ValueTask`, cancellation support)
- DI helpers via `AddObsWebSocketClient()`
- Configurable JSON or MessagePack transport
- Reconnect and timeout options via `ObsWebSocketClientOptions`
- Convenience helpers for common scene/input/filter workflows

## Important Caveats

- This library is for **OBS WebSocket v5** only (OBS Studio 28+).
- Make sure OBS WebSocket server is enabled (`Tools -> WebSocket Server Settings`).
- If you use authentication, provide the correct password in options/config.

## Quick Start (DI)

`appsettings.json`:

```json
{
  "Obs": {
    "ServerUri": "ws://localhost:4455",
    "Password": "",
    "EventSubscriptions": null,
    "Format": "Json"
  }
}
```

`Program.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ObsWebSocket.Core;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ObsWebSocketClientOptions>(
    builder.Configuration.GetSection("Obs"));

builder.Services.AddObsWebSocketClient();
builder.Services.AddHostedService<Worker>();

await builder.Build().RunAsync();
```

Worker example:

```csharp
using Microsoft.Extensions.Hosting;
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
        if (client.IsConnected)
        {
            await client.DisconnectAsync();
        }
    }

    private static void OnSceneChanged(object? sender, CurrentProgramSceneChangedEventArgs e)
    {
        Console.WriteLine($"Program scene: {e.EventData.SceneName}");
    }
}
```

## Helpers and Advanced Usage

Common helper APIs include:

- `SwitchSceneAndWaitAsync(...)`
- `SetInputTextAsync(...)`
- `SetSceneItemEnabledAsync(...)`
- `SourceExistsAsync(...)`
- `GetSourceFilterSettingsAsync<T>(...)`
- `WaitForEventAsync<TEventArgs>(...)`

For generated request models and direct requests, see:

- `ObsWebSocket.Core.Protocol.Requests`
- `ObsWebSocket.Core.Protocol.Responses`
- `ObsWebSocket.Core.Events.Generated`

Batch API note (AOT-safe path):

- `BatchRequestItem.RequestData` should be `null`, `JsonElement`, or a generated `*RequestData` DTO from `ObsWebSocket.Core.Protocol.Requests`.
- Arbitrary anonymous/POCO objects are not guaranteed to be serializable in Native AOT builds.

## Example App

`ObsWebSocket.Example` contains a host-based sample using configuration + DI.

- Interactive mode: starts a command loop (`help`, `version`, `scene`, `batch-example`, etc.)
- Transport validation mode: runs JSON + MsgPack validation cycles (scene/input/filter stub-heavy calls), then enters the interactive loop
- One-shot mode: pass a command as process arguments to run it directly and exit (for CI/automation), for example:
  - `ObsWebSocket.Example run-transport-tests`

`appsettings.json`:

```json
{
  "Obs": {
    "ServerUri": "ws://localhost:4455",
    "Password": "",
    "EventSubscriptions": null,
    "Format": "Json"
  },
  "ExampleValidation": {
    "RunValidationOnStartup": false,
    "ValidationIterations": 1
  }
}
```

Interactive command to run validation on demand:

- `run-transport-tests`

## Native AOT Example

Build and run the example as Native AOT:

```bash
dotnet publish ObsWebSocket.Example/ObsWebSocket.Example.csproj -c Release -r win-x64 --self-contained true
./ObsWebSocket.Example/bin/Release/net10.0/win-x64/publish/ObsWebSocket.Example.exe
```

## Contributing

Contributions are welcome. See [`CONTRIBUTING.md`](CONTRIBUTING.md).

## License

MIT. See [`LICENSE.txt`](LICENSE.txt).
