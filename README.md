﻿# ObsWebSocket.Core: Your Modern C# Bridge to OBS! 🚀🎬✨

[![Build Status](https://img.shields.io/github/actions/workflow/status/Agash/ObsWebSocket/build.yml?branch=master&style=flat-square&logo=github&logoColor=white)](https://github.com/Agash/ObsWebSocket/actions)
[![NuGet Version](https://img.shields.io/nuget/v/ObsWebSocket.Core.svg?style=flat-square&logo=nuget&logoColor=white)](https://www.nuget.org/packages/ObsWebSocket.Core/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg?style=flat-square)](https://opensource.org/licenses/MIT)

Hey OBS Power Users, Stream Tool Crafters, and Automation Fans! 👋

Ready to take control of OBS Studio directly from your C#/.NET applications? Want to build custom stream dashboards, trigger actions based on game events, automate scene switching, or create unique chat interactions linked to OBS? You've come to the right place! 😎

**ObsWebSocket.Core** is a sleek, modern, and developer-friendly .NET 9 library built for interacting with the **OBS Studio WebSocket API v5**. Forget wrestling with raw WebSocket messages – this library provides a clean, asynchronous, and strongly-typed way to talk to OBS.

Built with the latest C# 13/.NET 9 goodies, including source generators that build the API directly from the official `protocol.json`, ensuring you're always aligned with the latest OBS WebSocket features! 🔧

Perfect for:

-   Building custom remote controls or Stream Deck alternatives 🎛️
-   Automating scene changes based on external triggers (game events, chat commands) 🤖
-   Creating dynamic overlays that react to OBS events 📊
-   Developing sophisticated broadcasting tools and dashboards 📈
-   Integrating OBS control into larger .NET applications or services 🔗
-   Anything else your creative mind can dream up to enhance your stream! 🧠💡

## Features That Rock 🎸

-   ✅ **Full OBS WebSocket v5 Support:** Auto-generated client methods, request/response types, and event arguments directly from the protocol spec.
-   ⚡ **Modern Async Everywhere:** Built with `async/await`, `Task`, `ValueTask`, and `CancellationToken` for responsive applications.
-   🔧 **Easy DI Integration:** Simple setup in your .NET host with `AddObsWebSocketClient()`.
-   ⚙️ **Flexible Configuration:** Use `IOptions<ObsWebSocketClientOptions>` and `appsettings.json` for easy setup.
-   ↔️ **JSON & MessagePack:** Choose between human-readable JSON (default) or efficient binary MessagePack serialization.
-   💪 **Connection Resilience:** Optional, configurable automatic reconnection keeps your connection stable.
-   🔒 **Strongly-Typed:** Compile-time safety and great IntelliSense thanks to generated DTOs and event args.
-   🔔 **Standard .NET Events:** Subscribe to OBS events using familiar `event EventHandler<TEventArgs>` patterns.
-   ✨ **Convenience Helpers:** Includes extension methods for common tasks like switching scenes (with transitions & waiting), setting text source content, managing scene item visibility, checking if sources exist, setting multiple mutes at once, getting typed filter settings, waiting for specific event conditions, and more! _(New!)_
-   🌐 **Cross-Platform:** Built on .NET 9.

## Version Alert! ⚠️

> This library is exclusively for **OBS WebSocket API v5** (the version built into OBS Studio 28 and later). It **will not work** with the older v4.x plugin. Ensure `obs-websocket` is enabled in OBS (usually under `Tools -> WebSocket Server Settings`).

## Get Started Fast 💨

Using `ObsWebSocket.Core` with .NET's Generic Host and Dependency Injection is the smoothest path.

**1. Install the Package:**

```bash
dotnet add package ObsWebSocket.Core
```

**2. Configure `appsettings.json`:**

Add an `"Obs"` section to your `appsettings.json`:

```json
{
    "Logging": {
        "LogLevel": {
            "Default": "Information",
            "ObsWebSocket.Core": "Information" // Use "Trace" for verbose library logs
        }
    },
    "Obs": {
        // REQUIRED: Update with your OBS WebSocket server details
        "ServerUri": "ws://localhost:4455",
        // Optional: Add password if authentication is enabled in OBS
        "Password": "YourSuperSecretPassword",
        // Optional: Specify event subscriptions (defaults to 'All' non-high-volume).
        // See ObsWebSocket.Core.Protocol.Generated.EventSubscription enum flags.
        // Example: 13 (General | Scenes | Inputs -> 1 | 4 | 8 = 13)
        "EventSubscriptions": null,
        // Optional: Choose serialization format ('Json' or 'MsgPack')
        "Format": "Json"
    }
}
```

**3. Register in `Program.cs`:**

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ObsWebSocket.Core;
using YourApplicationNamespace; // <-- Your namespace

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Standard logging setup (example)
builder.Logging.ClearProviders();
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
builder.Logging.AddConsole();

// 1. Read OBS settings from the "Obs" section
builder.Services.Configure<ObsWebSocketClientOptions>(builder.Configuration.GetSection("Obs"));

// 2. Add the OBS WebSocket client services
builder.Services.AddObsWebSocketClient();

// 3. Add your application's service that will use the client
builder.Services.AddHostedService<MyObsControllerService>(); // <-- Your service

using IHost host = builder.Build();
await host.RunAsync();
```

**4. Use it in Your Service!**

Inject `ObsWebSocketClient` and start controlling OBS:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Events; // For connection events EventArgs
using ObsWebSocket.Core.Events.Generated; // For specific OBS EventArgs
using ObsWebSocket.Core.Protocol.Requests; // For Request DTOs
using ObsWebSocket.Core.Protocol.Responses; // For Response DTOs
using ObsWebSocket.Core.Protocol.Generated; // For enums like MediaInputAction
using ObsWebSocket.Core.Protocol.Common.FilterSettings; // For predefined filter types

namespace YourApplicationNamespace; // <-- Your namespace

public class MyObsControllerService : BackgroundService
{
    private readonly ILogger<MyObsControllerService> _logger;
    private readonly ObsWebSocketClient _obsClient;

    public MyObsControllerService(ILogger<MyObsControllerService> logger, ObsWebSocketClient obsClient)
    {
        _logger = logger;
        _obsClient = obsClient;

        // --- Subscribe to Events ---
        _obsClient.Connected += OnObsConnected;
        _obsClient.Disconnected += OnObsDisconnected;
        _obsClient.CurrentProgramSceneChanged += OnCurrentProgramSceneChanged;
        // Add more event subscriptions as needed!
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Connecting to OBS...");
            // Connect using the options configured in Program.cs/appsettings.json
            await _obsClient.ConnectAsync(stoppingToken);

            if (_obsClient.IsConnected)
            {
                _logger.LogInformation("Connection to OBS successful!");
                // Now you can safely send requests!
                await GetAndLogObsVersion(stoppingToken);
                await ExampleHelperUsage(stoppingToken);

                // Keep running until shutdown is requested
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            else
            {
                 _logger.LogError("Failed to connect to OBS WebSocket.");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("OBS Controller stopping.");
        }
        catch (ObsWebSocketException ex) // Handle OBS-specific connection/request errors
        {
             _logger.LogError(ex, "OBS WebSocket error: {ErrorMessage}", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in OBS Controller Service.");
        }
        finally
        {
            // --- Unsubscribe from Events ---
            _obsClient.Connected -= OnObsConnected;
            _obsClient.Disconnected -= OnObsDisconnected;
            _obsClient.CurrentProgramSceneChanged -= OnCurrentProgramSceneChanged;

            // Ensure disconnection on shutdown
            if (_obsClient.IsConnected)
            {
                _logger.LogInformation("Disconnecting from OBS...");
                await _obsClient.DisconnectAsync();
            }
        }
    }

    // --- Example Helper Usage ---
    private async Task ExampleHelperUsage(CancellationToken cancellationToken)
    {
        try
        {
            // Check if a source exists
            if (await _obsClient.SourceExistsAsync("MyCameraSource", cancellationToken))
            {
                _logger.LogInformation("MyCameraSource exists!");

                // Toggle visibility of a specific item in a scene
                bool isNowVisible = await _obsClient.SetSceneItemEnabledAsync(
                    sceneName: "MainScene",
                    sourceName: "MyCameraSource", // Find item by source name
                    isEnabled: null, // Toggle
                    cancellationToken: cancellationToken
                );
                 _logger.LogInformation("MyCameraSource visibility toggled. Is now visible: {IsVisible}", isNowVisible);

                 // Get typed filter settings (assuming ColorCorrectionFilterSettings record exists)
                 var colorSettings = await _obsClient.GetSourceFilterSettingsAsync<ColorCorrectionFilterSettings>(
                     sourceName: "MyCameraSource",
                     filterName: "Color Correction",
                     cancellationToken: cancellationToken
                 );
                 if (colorSettings != null) {
                     _logger.LogInformation("Camera saturation: {Saturation}", colorSettings.Saturation);
                 }
            } else {
                 _logger.LogWarning("MyCameraSource does not exist.");
            }

            // Set text on a text source
            await _obsClient.SetInputTextAsync("MyTextLabel", "Hello from ObsWebSocket.Core!", cancellationToken);

            // Switch scene using a specific transition and wait for it
            await _obsClient.SwitchSceneAndWaitAsync(
                sceneName: "GameplayScene",
                transitionName: "Fade",
                transitionDurationMs: 750,
                timeout: TimeSpan.FromSeconds(5), // Wait up to 5s for the change
                cancellationToken: cancellationToken
            );
             _logger.LogInformation("Switched to GameplayScene and transition finished.");

             // --- Example: Wait for a specific event ---
            string mediaSourceName = "IntroVideo";
            _logger.LogInformation("Restarting media source '{MediaSourceName}' and waiting for it to end...", mediaSourceName);

            // 1. Trigger the action
            await _obsClient.TriggerMediaInputActionAsync(
                new TriggerMediaInputActionRequestData(mediaSourceName, MediaInputAction.Restart),
                cancellationToken: cancellationToken
            );

            // 2. Wait for the *specific* event matching the predicate
            var endedArgs = await _obsClient.WaitForEventAsync<MediaInputPlaybackEndedEventArgs>(
                predicate: args => args.EventData.InputName == mediaSourceName, // Only care about *this* media source
                timeout: TimeSpan.FromSeconds(30), // Wait up to 30 seconds
                cancellationToken: cancellationToken
            );

            // 3. Check the result
            if (endedArgs != null)
            {
                 _logger.LogInformation("Media source '{MediaSourceName}' finished playback.", mediaSourceName);
            }
            else
            {
                 _logger.LogWarning("Timed out or was canceled while waiting for media source '{MediaSourceName}' to end.", mediaSourceName);
            }

        }
        catch (ObsWebSocketException ex) { _logger.LogError(ex, "Error during helper usage: {Msg}", ex.Message); }
        catch (TimeoutException ex) { _logger.LogWarning("Timeout occurred: {Msg}", ex.Message); }
        catch (OperationCanceledException) { }
    }

    // --- Example Request Methods ---
    private async Task GetAndLogObsVersion(CancellationToken cancellationToken)
    {
        try
        {
            GetVersionResponseData? version = await _obsClient.GetVersionAsync(cancellationToken: cancellationToken);
            if (version != null)
            {
                _logger.LogInformation("Connected to OBS v{ObsVersion} (WebSocket v{WsVersion})",
                    version.ObsVersion, version.ObsWebSocketVersion);
            }
        }
        catch (ObsWebSocketException ex) { _logger.LogError(ex, "Error getting OBS version."); }
        catch (OperationCanceledException) { } // Ignore cancellation
    }

    // --- Event Handlers ---
    private void OnObsConnected(object? sender, EventArgs e)
    {
        _logger.LogInformation("Event Handler: Connected to OBS!");
        // Good place to maybe fetch initial state if needed
    }

    private void OnObsDisconnected(object? sender, DisconnectedEventArgs e)
    {
        _logger.LogWarning("Event Handler: Disconnected from OBS. Reason: {Reason}", e.ReasonException?.Message ?? "Client/Server Request");
    }

     private void OnCurrentProgramSceneChanged(object? sender, CurrentProgramSceneChangedEventArgs e)
     {
        _logger.LogInformation("Event Handler: Program scene changed to {SceneName}", e.EventData.SceneName);
     }
}
```

## Diving Deeper 🏊‍♂️

-   **Sending Requests:** Use the `_obsClient.RequestNameAsync(...)` extension methods for direct protocol access. They are generated based on the OBS WebSocket protocol. IntelliSense is your friend! Need to set input settings? `_obsClient.SetInputSettingsAsync(...)`. Need the version? `_obsClient.GetVersionAsync()`.
-   **Using Helpers:** For common tasks, explore the helper extension methods available directly on the `_obsClient` instance (like `_obsClient.SetInputTextAsync(...)`, `_obsClient.SwitchSceneAndWaitAsync(...)`, `_obsClient.SourceExistsAsync(...)`, `_obsClient.WaitForEventAsync(...)`, etc.). These often combine multiple direct requests into a simpler call.
-   **Request/Response Data:** Many direct requests require input data, and many return data. These use generated C# `record` types found in the `ObsWebSocket.Core.Protocol.Requests` and `ObsWebSocket.Core.Protocol.Responses` namespaces (e.g., `GetVersionResponseData`, `SetInputSettingsRequestData`). The helper methods often abstract these away.
-   **Handling OBS Events:** Subscribe to events like `_obsClient.SceneCreated += ...`. The second argument (`e`) of your handler will be a strongly-typed `EventArgs` (like `SceneCreatedEventArgs`) containing an `EventData` property with the specific event details (e.g., `e.EventData.SceneName`). Find all generated EventArgs in `ObsWebSocket.Core.Events.Generated`.
-   **Waiting for Events:** Use the `_obsClient.WaitForEventAsync<TEventArgs>(predicate, timeout, ...)` helper to reliably wait for a specific event that meets certain criteria after performing an action. This is crucial for synchronizing actions.
-   **Filter Settings:** Use `_obsClient.GetSourceFilterSettingsAsync<T>(...)` and `_obsClient.SetSourceFilterSettingsAsync<T>(...)` with the predefined records in `ObsWebSocket.Core.Protocol.Common.FilterSettings` (like `ColorCorrectionFilterSettings`) for type-safe filter manipulation.
-   **Batching Requests:** Use `_obsClient.CallBatchAsync(...)` to send multiple commands at once for efficiency, especially useful for complex sequences or animations.
-   **Re-Identifying:** Use `_obsClient.ReidentifyAsync(...)` to change event subscriptions after the initial connection without disconnecting.
-   **Configuration Options:** Check the `ObsWebSocketClientOptions` class for all available settings (timeouts, reconnection behavior, serialization format, etc.).
-   **Logging:** Leverage `Microsoft.Extensions.Logging`. Setting the `ObsWebSocket.Core` category to `Trace` provides _very_ detailed logs of connection steps, message sending/receiving, and event processing.

## ⚠️ Important Considerations ⚠️

-   **OBS WebSocket v5 Required:** Double-check you're running OBS Studio 28+ and have the WebSocket server enabled (`Tools -> WebSocket Server Settings`).
-   **Firewall:** Ensure your firewall allows connections to the port OBS WebSocket is listening on (default: 4455).
-   **Error Handling:** Requests can fail! Wrap calls to `_obsClient` methods in `try-catch` blocks to handle `ObsWebSocketException` (for OBS-side errors) and other potential exceptions (like `InvalidOperationException` if not connected, or `TimeoutException` from waiting helpers).

## Contributing 🤝

Got ideas? Found a bug? Contributions are highly encouraged! Check out the [Contribution Guidelines](CONTRIBUTING.md) to get started.

## License 📄

This project rocks the **MIT License**. See the [LICENSE.txt](LICENSE.txt) file for the full text.

---

Happy Automating! 🎉
