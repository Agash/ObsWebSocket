using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Events;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Example;

internal sealed partial class Worker(
    ILogger<Worker> logger,
    ObsWebSocketClient obsClient,
    IOptions<ObsWebSocketClientOptions> obsOptions,
    IHostApplicationLifetime lifetime
) : BackgroundService
{
    private readonly ILogger<Worker> _logger = logger;
    private readonly ObsWebSocketClient _obsClient = obsClient;
    private readonly IHostApplicationLifetime _lifetime = lifetime;

    private static readonly JsonSerializerOptions s_serializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // Store the *intended* subscription flags (initialized from options, updated by set-subs)
    // Note: The client doesn't currently expose the *actual* negotiated flags from the server.
    private uint _currentSubscriptionFlags =
        obsOptions.Value.EventSubscriptions ?? (uint)EventSubscription.All; // Default to All if null

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // --- Subscribe to Client Connection Events ---
        _obsClient.Connecting += OnObsConnecting;
        _obsClient.Connected += OnObsConnected;
        _obsClient.Disconnected += OnObsDisconnected;
        _obsClient.ConnectionFailed += OnObsConnectionFailed;
        _obsClient.AuthenticationFailure += OnObsAuthenticationFailure;

        // --- Subscribe to Specific OBS Events ---
        _obsClient.CurrentProgramSceneChanged += OnCurrentProgramSceneChanged;
        _obsClient.InputMuteStateChanged += OnInputMuteStateChanged;
        _obsClient.StudioModeStateChanged += OnStudioModeStateChanged;
        _obsClient.InputCreated += OnInputCreated;
        _obsClient.StreamStateChanged += OnStreamStateChanged;
        _obsClient.SceneCreated += OnSceneCreated;
        _obsClient.SourceFilterCreated += OnSourceFilterCreated;

        _logger.LogInformation("Example Worker running.");
        _logger.LogInformation(
            "Connecting to OBS WebSocket at {Uri}...",
            obsOptions.Value.ServerUri
        );

        try
        {
            // --- Connect to OBS ---
            // ConnectAsync now uses the IOptions internally
            await _obsClient.ConnectAsync(stoppingToken);

            if (_obsClient.IsConnected)
            {
                await RunCommandLoopAsync(stoppingToken);
            }
            else
            {
                _logger.LogError("Failed to connect to OBS. Shutting down.");
                _lifetime.StopApplication();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("OBS connection cancelled by shutdown request.");
        }
        catch (ObsWebSocketException ex) // Catch client-specific exceptions
        {
            _logger.LogError(ex, "OBS WebSocket connection failed: {ErrorMessage}", ex.Message);
            _lifetime.StopApplication(); // Stop host if initial connect fails
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unexpected error occurred during connection or command loop.");
            _lifetime.StopApplication();
        }
        finally
        {
            // --- Unsubscribe from Events ---
            _obsClient.Connecting -= OnObsConnecting;
            _obsClient.Connected -= OnObsConnected;
            _obsClient.Disconnected -= OnObsDisconnected;
            _obsClient.ConnectionFailed -= OnObsConnectionFailed;
            _obsClient.AuthenticationFailure -= OnObsAuthenticationFailure;
            _obsClient.CurrentProgramSceneChanged -= OnCurrentProgramSceneChanged;
            _obsClient.InputMuteStateChanged -= OnInputMuteStateChanged;
            _obsClient.StudioModeStateChanged -= OnStudioModeStateChanged;
            // Unsubscribe new handlers
            _obsClient.InputCreated -= OnInputCreated;
            _obsClient.StreamStateChanged -= OnStreamStateChanged;
            _obsClient.SceneCreated -= OnSceneCreated;
            _obsClient.SourceFilterCreated -= OnSourceFilterCreated;

            // Ensure disconnection on exit
            if (_obsClient.IsConnected)
            {
                _logger.LogInformation("Disconnecting from OBS...");
                await _obsClient.DisconnectAsync(cancellationToken: CancellationToken.None); // Use independent token for cleanup
            }
        }
    }

    private async Task RunCommandLoopAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Command loop started. Type 'help' for commands, 'exit' to quit.");

        while (!stoppingToken.IsCancellationRequested && _obsClient.IsConnected)
        {
            Console.Write("> ");
            string? commandLine = await Console.In.ReadLineAsync(stoppingToken);
            if (string.IsNullOrWhiteSpace(commandLine))
            {
                continue;
            }

            string[] parts = commandLine.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
            string command = parts[0].ToLowerInvariant();
            string[] args = parts.Length > 1 ? parts[1..] : [];

            try
            {
                bool exit = await ProcessCommandAsync(command, args, stoppingToken);
                if (exit)
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Command processing cancelled.");
                break;
            }
            catch (InvalidOperationException ex) // e.g., calling command when not connected
            {
                _logger.LogWarning("Command failed: {ErrorMessage}", ex.Message);
            }
            catch (ObsWebSocketException ex) // Errors from OBS
            {
                _logger.LogError(
                    "OBS Request Error (Code {ObsCode}): {ObsMessage}",
                    ExtractObsErrorCode(ex), // Helper to get code if available
                    ex.Message
                );
            }
            catch (Exception ex) // Catch unexpected command errors
            {
                _logger.LogError(ex, "Error processing command '{Command}'", command);
            }
        }

        _logger.LogInformation("Command loop finished.");
    }

    // Helper to extract OBS error code from exception message if possible
    private static string ExtractObsErrorCode(ObsWebSocketException ex)
    {
        // Basic parsing, assumes format like "... code XXX: ..."
        System.Text.RegularExpressions.Match match = ObsErrorCodeRegex().Match(ex.Message);
        return match.Success ? match.Groups[1].Value : "N/A";
    }

    private async Task<bool> ProcessCommandAsync(
        string command,
        string[] args,
        CancellationToken cancellationToken
    )
    {
        switch (command)
        {
            case "help":
                Console.WriteLine(
                    """
                    Available commands:
                      help                      - Show this help
                      exit                      - Exit the application
                      status                    - Show connection status
                      version                   - Get OBS & WebSocket version info
                      scene                     - Get current program scene
                      mute [input name]         - Toggle mute for the specified audio input
                      unmute [input name]       - (Alias for mute)
                      get-input-settings [scene name] [input name] - Get settings for an input
                      set-text [scene] [input] [text...] - Set text for a Text (GDI+) source
                      list-filters [source name] - List filters for a source (input/scene)
                      toggle-filter [source] [filter] - Toggle enable state of a filter
                      batch-example             - Run a sample batch request sequence
                      list-subs                 - Show current event subscription flags (intended)
                      set-subs <numeric_flags>  - Change event subscriptions via Reidentify
                    """
                );
                return false;

            case "exit":
                _logger.LogInformation("Exit command received.");
                _lifetime.StopApplication(); // Graceful shutdown
                return true;

            case "status":
                Console.WriteLine($"Client Connected: {_obsClient.IsConnected}");
                return false;

            case "version":
                GetVersionResponseData? version = await _obsClient.GetVersionAsync(
                    cancellationToken: cancellationToken
                );
                if (version is not null)
                {
                    Console.WriteLine($"OBS Version: {version.ObsVersion}");
                    Console.WriteLine($"WebSocket Version: {version.ObsWebSocketVersion}");
                    Console.WriteLine($"RPC Version: {version.RpcVersion}");
                    Console.WriteLine(
                        $"Platform: {version.Platform} ({version.PlatformDescription})"
                    );
                    Console.WriteLine(
                        $"Supported Image Formats: {string.Join(", ", version.SupportedImageFormats ?? [])}"
                    );
                    Console.WriteLine(
                        $"Available Requests ({version.AvailableRequests?.Count ?? 0})"
                    ); // Only show count
                }
                else
                {
                    Console.WriteLine("Could not get version info.");
                }

                return false;

            case "scene":
                GetCurrentProgramSceneResponseData? scene =
                    await _obsClient.GetCurrentProgramSceneAsync(
                        cancellationToken: cancellationToken
                    );
                Console.WriteLine(
                    scene is not null
                        ? $"Current Program Scene: {scene.SceneName ?? "N/A"} (UUID: {scene.SceneUuid ?? "N/A"})"
                        : "Could not get current scene."
                );
                return false;

            case "mute":
            case "unmute":
                if (args.Length == 0)
                {
                    Console.WriteLine($"Usage: {command} [input name]");
                    return false;
                }

                string inputNameToMute = string.Join(" ", args);
                _logger.LogInformation("Toggling mute for input: {InputName}", inputNameToMute);
                ToggleInputMuteResponseData? muteState = await _obsClient.ToggleInputMuteAsync(
                    new ToggleInputMuteRequestData(inputNameToMute),
                    cancellationToken: cancellationToken
                );
                Console.WriteLine(
                    muteState is not null
                        ? $"Input '{inputNameToMute}' is now {(muteState.InputMuted ? "MUTED" : "UNMUTED")}"
                        : $"Could not toggle mute state for {inputNameToMute}. Does it exist?"
                );
                return false;

            // --- New Commands ---
            case "get-input-settings":
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: get-input-settings [scene name] [input name]");
                    return false;
                }

                string sceneForGetSettings = args[0];
                string inputForGetSettings = string.Join(" ", args[1..]);
                try
                {
                    // First, find the scene item ID within the specified scene
                    double sceneItemId = await GetSceneItemIdAsync(
                        sceneForGetSettings,
                        inputForGetSettings,
                        cancellationToken
                    );

                    // Now get the input settings using the *source name* (not the scene item ID)
                    GetInputSettingsResponseData? settings = await _obsClient.GetInputSettingsAsync(
                        new GetInputSettingsRequestData(inputForGetSettings),
                        cancellationToken: cancellationToken
                    );

                    if (settings?.InputSettings is not null)
                    {
                        Console.WriteLine($"--- Settings for '{inputForGetSettings}' ---");
                        Console.WriteLine($"Kind: {settings.InputKind ?? "Unknown"}"); // Kind comes from response
                        Console.WriteLine(
                            JsonSerializer.Serialize(settings.InputSettings, s_serializerOptions)
                        );
                    }
                    else
                    {
                        Console.WriteLine(
                            $"Could not get settings for input '{inputForGetSettings}'. It might not exist or have no specific settings."
                        );
                    }
                }
                catch (SceneItemNotFoundException ex)
                {
                    _logger.LogWarning("Cannot get settings: {Reason}", ex.Message); // Log specific error
                }

                return false;

            case "set-text":
                if (args.Length < 3)
                {
                    Console.WriteLine(
                        "Usage: set-text [scene name] [text source name] [new text...]"
                    );
                    return false;
                }

                string sceneForSetText = args[0];
                string inputForSetText = args[1];
                string newText = string.Join(" ", args[2..]);
                try
                {
                    // Find the scene item ID first (optional but good practice)
                    double sceneItemId = await GetSceneItemIdAsync(
                        sceneForSetText,
                        inputForSetText,
                        cancellationToken
                    );
                    _logger.LogInformation(
                        "Found scene item ID {ItemId} for '{InputName}' in scene '{SceneName}'. Setting text...",
                        sceneItemId,
                        inputForSetText,
                        sceneForSetText
                    );

                    // Construct the settings object for Text (GDI+) source
                    // IMPORTANT: The exact property name ('text') depends on the input kind.
                    // Assume 'text' for 'text_gdiplus_v3'. Inspect with 'get-input-settings' if unsure.
                    JsonElement newSettings = JsonSerializer.SerializeToElement(
                        new { text = newText }
                    );

                    // Send the request
                    await _obsClient.SetInputSettingsAsync(
                        new SetInputSettingsRequestData(
                            newSettings,
                            inputName: inputForSetText, // Identify input by name
                            overlay: true // Only update the 'text' field
                        ),
                        cancellationToken: cancellationToken
                    );

                    Console.WriteLine(
                        $"Successfully set text for '{inputForSetText}' to: '{newText}'"
                    );
                }
                catch (SceneItemNotFoundException ex)
                {
                    _logger.LogWarning("Cannot set text: {Reason}", ex.Message);
                }
                catch (ObsWebSocketException ex)
                {
                    // Catch specific OBS errors, e.g., if the input isn't a text source
                    _logger.LogError(
                        "Failed to set text for '{InputName}': OBS Error (Code {Code}) - {Comment}",
                        inputForSetText,
                        ExtractObsErrorCode(ex),
                        ex.Message
                    );
                }

                return false;

            case "list-filters":
                if (args.Length == 0)
                {
                    Console.WriteLine("Usage: list-filters [source name]");
                    return false;
                }

                string sourceForFilters = string.Join(" ", args);
                GetSourceFilterListResponseData? filterList =
                    await _obsClient.GetSourceFilterListAsync(
                        new GetSourceFilterListRequestData(sourceForFilters),
                        cancellationToken: cancellationToken
                    );
                if (filterList?.Filters is not null && filterList.Filters.Count > 0)
                {
                    Console.WriteLine($"--- Filters for '{sourceForFilters}' ---");
                    foreach (Core.Protocol.Common.FilterStub filterElement in filterList.Filters)
                    {
                        Console.WriteLine(
                            $"  - [{filterElement.FilterIndex}] {filterElement.FilterName} ({filterElement.FilterKind}) - Enabled: {filterElement.FilterEnabled}"
                        );
                    }
                }
                else
                {
                    Console.WriteLine($"No filters found for source '{sourceForFilters}'.");
                }

                return false;

            case "toggle-filter":
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: toggle-filter [source name] [filter name]");
                    return false;
                }

                string sourceForToggle = args[0];
                string filterToToggle = string.Join(" ", args[1..]);

                // 1. Get current filter state
                GetSourceFilterResponseData? currentFilterState =
                    await _obsClient.GetSourceFilterAsync(
                        new GetSourceFilterRequestData
                        {
                            SourceName = sourceForToggle,
                            FilterName = filterToToggle,
                        },
                        cancellationToken: cancellationToken
                    );

                if (currentFilterState is null)
                {
                    Console.WriteLine(
                        $"Could not find filter '{filterToToggle}' on source '{sourceForToggle}'."
                    );
                    return false;
                }

                // 2. Toggle the state
                bool newState = !currentFilterState.FilterEnabled;
                await _obsClient.SetSourceFilterEnabledAsync(
                    new SetSourceFilterEnabledRequestData
                    {
                        SourceName = sourceForToggle,
                        FilterName = filterToToggle,
                        FilterEnabled = newState,
                    },
                    cancellationToken: cancellationToken
                );

                Console.WriteLine(
                    $"Filter '{filterToToggle}' on '{sourceForToggle}' toggled to {(newState ? "ENABLED" : "DISABLED")}"
                );
                return false;

            case "batch-example":
                _logger.LogInformation("Running batch example...");
                List<BatchRequestItem> batchItems =
                [
                    new("GetVersion", null),
                    new("GetCurrentProgramScene", null),
                    new("GetInputList", new { inputKind = "text_gdiplus_v3" }), // Get only text inputs
                    new("Sleep", new { sleepMillis = 100 }), // Wait 100ms
                    new(
                        "SetInputSettings", // Example: Set specific text source's text (requires knowing name)
                        new
                        {
                            inputName = "MyTextSource", // REPLACE WITH YOUR ACTUAL TEXT SOURCE NAME
                            inputSettings = new { text = "Batch updated!" },
                            overlay = true,
                        }
                    ),
                ];

                List<RequestResponsePayload<object>> batchResults = await _obsClient.CallBatchAsync(
                    batchItems,
                    executionType: RequestBatchExecutionType.SerialRealtime,
                    haltOnFailure: false, // Continue even if one fails
                    cancellationToken: cancellationToken
                );

                Console.WriteLine($"--- Batch Results ({batchResults.Count} items) ---");
                foreach (RequestResponsePayload<object> result in batchResults)
                {
                    Console.WriteLine(
                        $"  [{result.RequestType} / {result.RequestId[(result.RequestId.LastIndexOf('_') + 1)..]}]: {(result.RequestStatus.Result ? "Success" : "Failed")} (Code: {result.RequestStatus.Code})"
                    );
                    if (!result.RequestStatus.Result)
                    {
                        Console.WriteLine($"      Error: {result.RequestStatus.Comment ?? "N/A"}");
                    }
                    else if (result.ResponseData is not null)
                    {
                        // Attempt to pretty print response data
                        string responseJson = "Could not serialize response data";
                        try
                        {
                            responseJson = JsonSerializer.Serialize(
                                result.ResponseData,
                                s_serializerOptions
                            );
                        }
                        catch
                        { /* Ignore serialization errors for logging */
                        }

                        Console.WriteLine($"      Response:\n{responseJson}\n");
                    }
                }

                _logger.LogInformation("Batch example finished.");
                return false;

            case "list-subs":
                Console.WriteLine(
                    $"Current Intended Event Subscriptions: {_currentSubscriptionFlags} ({(EventSubscription)_currentSubscriptionFlags})"
                );
                Console.WriteLine(
                    "Note: This reflects the last requested flags, not necessarily the flags acknowledged by the server."
                );
                return false;

            case "set-subs":
                if (args.Length == 0 || !uint.TryParse(args[0], out uint newFlags))
                {
                    Console.WriteLine("Usage: set-subs <numeric_flags>");
                    Console.WriteLine(
                        "Example: set-subs 65 (General | Scenes | Inputs, 1 | 4 | 8 = 13)"
                    );
                    Console.WriteLine(
                        "See ObsWebSocket.Core.Protocol.Generated.EventSubscription for flags."
                    );
                    return false;
                }

                _logger.LogInformation(
                    "Attempting to re-identify with new subscription flags: {NewFlags} ({EnumFlags})",
                    newFlags,
                    (EventSubscription)newFlags
                );
                await _obsClient.ReidentifyAsync(
                    (uint)newFlags,
                    cancellationToken: cancellationToken
                );
                _currentSubscriptionFlags = newFlags; // Update our stored value *after* successful re-identify
                Console.WriteLine(
                    $"Re-identified successfully. Intended subscriptions set to: {_currentSubscriptionFlags} ({(EventSubscription)_currentSubscriptionFlags})"
                );
                return false;

            case "get-all-filter-settings": // New Command
                await GetAllFilterSettingsForSource(cancellationToken);
                return false;
            // --- End of New Commands ---

            default:
                Console.WriteLine($"Unknown command: '{command}'. Type 'help'.");
                return false;
        }
    }

    // --- Helper to find Scene Item ID ---
    private async Task<double> GetSceneItemIdAsync(
        string sceneName,
        string sourceName,
        CancellationToken cancellationToken
    )
    {
        GetSceneItemIdResponseData? response = await _obsClient.GetSceneItemIdAsync(
            new GetSceneItemIdRequestData { SceneName = sceneName, SourceName = sourceName },
            cancellationToken: cancellationToken
        );

        return response?.SceneItemId == null
            ? throw new SceneItemNotFoundException(
                $"Source '{sourceName}' not found in scene '{sceneName}'."
            )
            : response.SceneItemId;
    }

    // Worker.cs - Replace the existing GetAllFilterSettingsForSource method

    private async Task GetAllFilterSettingsForSource(CancellationToken cancellationToken)
    {
        // Note: The 'sourceName' parameter is no longer strictly needed by the core logic,
        // but we keep it in the command signature for user context.
        // We *could* potentially use it to filter filter kinds that are applicable
        // to the source type, but GetSourceFilterKindListAsync doesn't support that.
        _logger.LogInformation(
            "Attempting to get default settings for all available filter kinds..."
        );

        List<string> availableFilterKinds;
        Dictionary<string, JsonElement?> filterDefaultSettings = new(
            StringComparer.OrdinalIgnoreCase
        );

        // 1. Get all available filter kinds
        try
        {
            GetSourceFilterKindListResponseData? response =
                await _obsClient.GetSourceFilterKindListAsync(cancellationToken: cancellationToken);
            if (response?.SourceFilterKinds is null || response.SourceFilterKinds.Count == 0)
            {
                _logger.LogWarning("Could not retrieve filter kinds from OBS.");
                return;
            }

            availableFilterKinds = response.SourceFilterKinds;
            _logger.LogInformation(
                "Found {Count} available filter kinds.",
                availableFilterKinds.Count
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get filter kind list.");
            return;
        }

        // 2. Get default settings for each filter kind
        _logger.LogInformation("Retrieving default settings for each filter kind...");
        foreach (string filterKind in availableFilterKinds)
        {
            try
            {
                GetSourceFilterDefaultSettingsResponseData? defaultSettingsResponse =
                    await _obsClient.GetSourceFilterDefaultSettingsAsync(
                        new GetSourceFilterDefaultSettingsRequestData(filterKind),
                        cancellationToken: cancellationToken
                    );

                if (defaultSettingsResponse != null)
                {
                    // Store default settings using the KIND as the key
                    filterDefaultSettings[filterKind] =
                        defaultSettingsResponse.DefaultFilterSettings;
                    _logger.LogDebug(
                        "Retrieved default settings for kind: {FilterKind}",
                        filterKind
                    );
                }
                else
                {
                    _logger.LogWarning(
                        "Received null response when getting default settings for filter kind '{FilterKind}'.",
                        filterKind
                    );
                    // Store null or an empty element to indicate we tried but got nothing back
                    filterDefaultSettings[filterKind] = null;
                }
            }
            catch (ObsWebSocketException ex)
            {
                _logger.LogWarning(
                    "Could not get default settings for filter kind '{FilterKind}'. OBS Error: {ErrorMessage}",
                    filterKind,
                    ex.Message
                );
                // Optionally store null or skip based on error type if needed
                filterDefaultSettings[filterKind] = null; // Indicate failure
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unexpected error getting default settings for filter kind '{FilterKind}'.",
                    filterKind
                );
                filterDefaultSettings[filterKind] = null; // Indicate failure
            }
        }

        // 3. Output the results as JSON
        _logger.LogInformation("Default settings retrieval complete. Outputting JSON...");
        Console.WriteLine("\n--- FILTER DEFAULT SETTINGS START ---");
        try
        {
            string jsonOutput = JsonSerializer.Serialize(filterDefaultSettings);
            Console.WriteLine(jsonOutput);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to serialize filter default settings results to JSON.");
            Console.WriteLine("{\"error\": \"Failed to serialize results.\"}");
        }

        Console.WriteLine("--- FILTER DEFAULT SETTINGS END ---\n");

        _logger.LogInformation("Default filter settings retrieval finished.");
        // No cleanup needed as we didn't add filters to the source
    }

    // --- Event Handlers ---
    private void OnObsConnecting(object? sender, ConnectingEventArgs e) =>
        _logger.LogInformation(
            "[Connecting] Attempt {AttemptNumber} to {ServerUri}...",
            e.AttemptNumber,
            e.ServerUri
        );

    private void OnObsConnected(object? sender, EventArgs e) =>
        _logger.LogInformation("[Connected] Successfully connected to OBS WebSocket!");

    private void OnObsDisconnected(object? sender, DisconnectedEventArgs e) =>
        _logger.LogWarning(
            "[Disconnected] Reason: {Reason}",
            e.ReasonException?.Message ?? "Graceful disconnect"
        );

    private void OnObsConnectionFailed(object? sender, ConnectionFailedEventArgs e) =>
        _logger.LogWarning(
            "[ConnectionFailed] Attempt {AttemptNumber} failed: {ErrorMessage}",
            e.AttemptNumber,
            e.ErrorException.Message
        );

    private void OnObsAuthenticationFailure(object? sender, AuthenticationFailureEventArgs e) =>
        _logger.LogError(
            "[AuthenticationFailure] Attempt {AttemptNumber} failed: {ErrorMessage}",
            e.AttemptNumber,
            e.ErrorException.Message
        );

    private void OnCurrentProgramSceneChanged(
        object? sender,
        CurrentProgramSceneChangedEventArgs e
    ) =>
        _logger.LogInformation(
            "[OBS Event] Program Scene Changed: {SceneName} (UUID: {SceneUuid})",
            e.EventData.SceneName,
            e.EventData.SceneUuid
        );

    private void OnInputMuteStateChanged(object? sender, InputMuteStateChangedEventArgs e) =>
        _logger.LogInformation(
            "[OBS Event] Input Mute Changed: {InputName} is now {MuteState}",
            e.EventData.InputName,
            e.EventData.InputMuted ? "MUTED" : "UNMUTED"
        );

    private void OnStudioModeStateChanged(object? sender, StudioModeStateChangedEventArgs e) =>
        _logger.LogInformation(
            "[OBS Event] Studio Mode Changed: {EnabledState}",
            e.EventData.StudioModeEnabled ? "ENABLED" : "DISABLED"
        );

    // --- New Event Handlers ---
    private void OnInputCreated(object? sender, InputCreatedEventArgs e) =>
        _logger.LogInformation(
            "[OBS Event] Input Created: Name={InputName}, Kind={InputKind}, UUID={InputUuid}",
            e.EventData.InputName,
            e.EventData.InputKind,
            e.EventData.InputUuid
        );

    private void OnStreamStateChanged(object? sender, StreamStateChangedEventArgs e) =>
        _logger.LogInformation(
            "[OBS Event] Stream State Changed: Active={OutputActive}, State={OutputState}",
            e.EventData.OutputActive,
            e.EventData.OutputState
        );

    private void OnSceneCreated(object? sender, SceneCreatedEventArgs e) =>
        _logger.LogInformation(
            "[OBS Event] Scene Created: Name={SceneName}, IsGroup={IsGroup}, UUID={SceneUuid}",
            e.EventData.SceneName,
            e.EventData.IsGroup,
            e.EventData.SceneUuid
        );

    private void OnSourceFilterCreated(object? sender, SourceFilterCreatedEventArgs e) =>
        _logger.LogInformation(
            "[OBS Event] Source Filter Created: Source={SourceName}, Filter={FilterName}, Kind={FilterKind}, Index={FilterIndex}",
            e.EventData.SourceName,
            e.EventData.FilterName,
            e.EventData.FilterKind,
            e.EventData.FilterIndex
        );

    // Custom exception for helper method
    private sealed class SceneItemNotFoundException(string message) : Exception(message);

    [System.Text.RegularExpressions.GeneratedRegex(@"code (\d+):")]
    private static partial System.Text.RegularExpressions.Regex ObsErrorCodeRegex();
}
