using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Events;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Common;
using ObsWebSocket.Core.Protocol.Common.FilterSettings;
using ObsWebSocket.Core.Protocol.Common.InputSettings;
using ObsWebSocket.Core.Protocol.Common.NestedTypes;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;
using ObsWebSocket.Core.Serialization;
using Spectre.Console;

namespace ObsWebSocket.Example;

internal sealed partial class Worker(
    ILogger<Worker> logger,
    ObsWebSocketClient obsClient,
    IOptions<ObsWebSocketClientOptions> obsOptions,
    IOptions<ExampleValidationOptions> validationOptions,
    ExampleStartupCommandOptions startupCommandOptions,
    ILoggerFactory loggerFactory,
    IWebSocketConnectionFactory connectionFactory,
    IHostApplicationLifetime lifetime
) : BackgroundService
{
    private readonly ILogger<Worker> _logger = logger;
    private readonly ObsWebSocketClient _obsClient = obsClient;
    private readonly ObsWebSocketClientOptions _baseOptions = obsOptions.Value;
    private readonly ExampleValidationOptions _validationOptions = validationOptions.Value;
    private readonly ExampleStartupCommandOptions _startupCommandOptions = startupCommandOptions;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly IWebSocketConnectionFactory _connectionFactory = connectionFactory;
    private readonly IHostApplicationLifetime _lifetime = lifetime;

    // Store the *intended* subscription flags (initialized from options, updated by set-subs)
    // Note: The client doesn't currently expose the *actual* negotiated flags from the server.
    private EventSubscription _currentSubscriptionFlags =
        obsOptions.Value.EventSubscriptions ?? EventSubscription.All;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // --- Subscribe to Client Connection Events ---
        _obsClient.Connecting += OnObsConnecting;
        _obsClient.Connected += OnObsConnected;
        _obsClient.Disconnected += OnObsDisconnected;
        _obsClient.ConnectionFailed += OnObsConnectionFailed;
        _obsClient.AuthenticationFailure += OnObsAuthenticationFailure;

        // --- Subscribe to Specific OBS Events ---
        _obsClient.Scenes.CurrentProgramSceneChanged += OnCurrentProgramSceneChanged;
        _obsClient.Inputs.InputMuteStateChanged += OnInputMuteStateChanged;
        _obsClient.Ui.StudioModeStateChanged += OnStudioModeStateChanged;
        _obsClient.Inputs.InputCreated += OnInputCreated;
        _obsClient.Outputs.StreamStateChanged += OnStreamStateChanged;
        _obsClient.Scenes.SceneCreated += OnSceneCreated;
        _obsClient.Filters.SourceFilterCreated += OnSourceFilterCreated;

        _logger.LogInformation("Example Worker running.");
        _logger.LogInformation(
            "Connecting to OBS WebSocket at {Uri}...",
            obsOptions.Value.ServerUri
        );

        try
        {
            if (!string.IsNullOrWhiteSpace(_startupCommandOptions.Command))
            {
                string startupCommand = _startupCommandOptions.Command;
                _logger.LogInformation("Running startup command: {Command}", startupCommand);
                _ = await ProcessCommandAsync(
                        startupCommand,
                        _startupCommandOptions.Arguments,
                        stoppingToken
                    )
                    .ConfigureAwait(false);
                _lifetime.StopApplication();
                return;
            }

            // --- Connect to OBS ---
            // ConnectAsync now uses the IOptions internally
            await _obsClient.ConnectAsync(stoppingToken);

            if (_obsClient.IsConnected)
            {
                if (!string.IsNullOrWhiteSpace(_startupCommandOptions.Command))
                {
                    string startupCommand = _startupCommandOptions.Command!;
                    _logger.LogInformation("Running startup command: {Command}", startupCommand);
                    _ = await ProcessCommandAsync(
                            startupCommand,
                            _startupCommandOptions.Arguments,
                            stoppingToken
                        )
                        .ConfigureAwait(false);
                    _lifetime.StopApplication();
                    return;
                }

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
            _obsClient.Scenes.CurrentProgramSceneChanged -= OnCurrentProgramSceneChanged;
            _obsClient.Inputs.InputMuteStateChanged -= OnInputMuteStateChanged;
            _obsClient.Ui.StudioModeStateChanged -= OnStudioModeStateChanged;
            // Unsubscribe new handlers
            _obsClient.Inputs.InputCreated -= OnInputCreated;
            _obsClient.Outputs.StreamStateChanged -= OnStreamStateChanged;
            _obsClient.Scenes.SceneCreated -= OnSceneCreated;
            _obsClient.Filters.SourceFilterCreated -= OnSourceFilterCreated;

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
        RenderCommandHelp();

        while (!stoppingToken.IsCancellationRequested && _obsClient.IsConnected)
        {
            AnsiConsole.Markup("[grey]> [/] ");
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
                RenderCommandHelp();
                return false;

            case "exit":
                _logger.LogInformation("Exit command received.");
                _lifetime.StopApplication(); // Graceful shutdown
                return true;

            case "status":
                RenderKeyValueTable(
                    "Connection Status",
                    [("Client Connected", _obsClient.IsConnected ? "Yes" : "No")]
                );
                return false;

            case "version":
                GetVersionResponseData? version = await _obsClient.General.GetVersionAsync(
                    cancellationToken: cancellationToken
                );
                if (version is not null)
                {
                    RenderKeyValueTable(
                        "Version Info",
                        [
                            ("OBS Version", version.ObsVersion ?? "N/A"),
                            ("WebSocket Version", version.ObsWebSocketVersion ?? "N/A"),
                            ("RPC Version", version.RpcVersion.ToString()),
                            (
                                "Platform",
                                $"{version.Platform ?? "N/A"} ({version.PlatformDescription ?? "N/A"})"
                            ),
                            (
                                "Supported Image Formats",
                                string.Join(", ", version.SupportedImageFormats ?? [])
                            ),
                            (
                                "Available Requests",
                                (version.AvailableRequests?.Count ?? 0).ToString()
                            ),
                        ]
                    );
                }
                else
                {
                    UiWarn("Could not get version info.");
                }

                return false;

            case "scene":
                GetCurrentProgramSceneResponseData? scene =
                    await _obsClient.Scenes.GetCurrentProgramSceneAsync(
                        cancellationToken: cancellationToken
                    );
                if (scene is null)
                {
                    UiWarn("Could not get current scene.");
                    return false;
                }

                RenderKeyValueTable(
                    "Current Scene",
                    [("Name", scene.SceneName ?? "N/A"), ("UUID", scene.SceneUuid ?? "N/A")]
                );
                return false;

            case "resolve":
                if (args.Length == 0)
                {
                    UiWarn("Usage: resolve [scene name] [source name in that scene]");
                    return false;
                }

                await ResolveDemoAsync(
                    args[0],
                    args.Length > 1 ? string.Join(" ", args[1..]) : null,
                    cancellationToken
                );
                return false;

            case "mute":
            case "unmute":
                if (args.Length == 0)
                {
                    UiWarn($"Usage: {command} [input name]");
                    return false;
                }

                string inputNameToMute = string.Join(" ", args);
                _logger.LogInformation("Toggling mute for input: {InputName}", inputNameToMute);

                // Handle form. One call about one input, so the identity is said once and the
                // method name drops the word the handle already carries.
                ToggleInputMuteResponseData? muteState = await _obsClient
                    .Input(inputNameToMute)
                    .ToggleMuteAsync(cancellationToken);
                if (muteState is null)
                {
                    UiWarn($"Could not toggle mute state for {inputNameToMute}. Does it exist?");
                    return false;
                }

                UiSuccess(
                    $"Input '{inputNameToMute}' is now {(muteState.InputMuted ? "MUTED" : "UNMUTED")}"
                );
                return false;

            // --- New Commands ---
            case "get-input-settings":
                if (args.Length < 2)
                {
                    UiWarn("Usage: get-input-settings [scene name] [input name]");
                    return false;
                }

                string sceneForGetSettings = args[0];
                string inputForGetSettings = string.Join(" ", args[1..]);
                try
                {
                    // First, find the scene item ID within the specified scene
                    long sceneItemId = await GetSceneItemIdAsync(
                        sceneForGetSettings,
                        inputForGetSettings,
                        cancellationToken
                    );

                    // Now get the input settings using the *source name* (not the scene item ID)
                    GetInputSettingsResponseData? settings =
                        await _obsClient.Inputs.GetInputSettingsAsync(
                            new GetInputSettingsRequestData(inputForGetSettings),
                            cancellationToken: cancellationToken
                        );

                    if (settings?.InputSettings is JsonElement inputSettingsElement)
                    {
                        UiInfo(
                            $"Settings for '{inputForGetSettings}' (kind: {settings.InputKind ?? "Unknown"})"
                        );
                        RenderJsonPanel("Input Settings", inputSettingsElement.GetRawText());
                    }
                    else
                    {
                        UiWarn(
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
                    UiWarn("Usage: set-text [scene name] [text source name] [new text...]");
                    return false;
                }

                string sceneForSetText = args[0];
                string inputForSetText = args[1];
                string newText = string.Join(" ", args[2..]);
                try
                {
                    // Find the scene item ID first (optional but good practice)
                    long sceneItemId = await GetSceneItemIdAsync(
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

                    // Protocol level on purpose. SetInputTextAsync is a hand-written group helper
                    // that serializes TextGdiPlusInputSettings internally; the handles are
                    // generated from the protocol, so nothing hand-written appears on them.
                    await _obsClient.Inputs.SetInputTextAsync(
                        inputForSetText,
                        newText,
                        cancellationToken
                    );
                    UiSuccess($"Successfully set text for '{inputForSetText}' to: '{newText}'");
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
                    UiWarn("Usage: list-filters [source name]");
                    return false;
                }

                string sourceForFilters = string.Join(" ", args);

                // A filter list belongs to the source, not to the Filters category, and the handle
                // says so: client.Source(x).GetFilterListAsync, not Filters.GetSourceFilterList.
                GetSourceFilterListResponseData? filterList = await _obsClient
                    .Source(sourceForFilters)
                    .GetFilterListAsync(cancellationToken);
                if (filterList?.Filters is not null && filterList.Filters.Count > 0)
                {
                    Table table = new()
                    {
                        Title = new TableTitle($"Filters for '{sourceForFilters}'"),
                    };
                    _ = table.AddColumn("Index");
                    _ = table.AddColumn("Name");
                    _ = table.AddColumn("Kind");
                    _ = table.AddColumn("Enabled");
                    foreach (Core.Protocol.Common.FilterStub filterElement in filterList.Filters)
                    {
                        string filterIndex = filterElement.FilterIndex.ToString(
                            CultureInfo.InvariantCulture
                        );
                        string filterName =
                            Markup.Escape(filterElement.FilterName ?? "N/A") ?? "N/A";
                        string filterKind =
                            Markup.Escape(filterElement.FilterKind ?? "N/A") ?? "N/A";
                        _ = table.AddRow(
                            filterIndex,
                            filterName,
                            filterKind,
                            filterElement.FilterEnabled == true ? "[green]Yes[/]" : "[grey]No[/]"
                        );
                    }

                    AnsiConsole.Write(table);
                }
                else
                {
                    UiInfo($"No filters found for source '{sourceForFilters}'.");
                }

                return false;

            case "toggle-filter":
                if (args.Length < 2)
                {
                    UiWarn("Usage: toggle-filter [source name] [filter name]");
                    return false;
                }

                string sourceForToggle = args[0];
                string filterToToggle = string.Join(" ", args[1..]);

                // Read then write, both about the same filter. Through the category group that is
                // four strings across two request records, any one of which can be misspelled into
                // a ResourceNotFound. Held as a handle it is two strings, once.
                FilterOperations filter = _obsClient.Source(sourceForToggle).Filter(filterToToggle);

                GetSourceFilterResponseData? currentFilterState = await filter.GetAsync(
                    cancellationToken
                );

                if (currentFilterState is null)
                {
                    UiWarn(
                        $"Could not find filter '{filterToToggle}' on source '{sourceForToggle}'."
                    );
                    return false;
                }

                bool newState = !currentFilterState.FilterEnabled;
                await filter.SetEnabledAsync(newState, cancellationToken);

                UiSuccess(
                    $"Filter '{filterToToggle}' on '{sourceForToggle}' toggled to {(newState ? "ENABLED" : "DISABLED")}"
                );
                return false;

            case "watch":
            {
                // Streams are the ergonomic way to observe events: subscribe for the lifetime
                // of the loop, no handler bookkeeping, and cancellation ends it cleanly. The
                // classic events on the client are untouched and still work alongside this.
                //
                // The event carries the scene uuid, so acting on it costs no extra request.
                // Reading SceneName back off it and addressing the scene by name would add a round
                // trip and reintroduce the rename race the uuid exists to close.
                int seconds =
                    args.Length > 0 && int.TryParse(args[0], out int parsed) ? parsed : 15;
                UiInfo($"Watching scene changes for {seconds}s. Switch scenes in OBS.");

                using CancellationTokenSource watchCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                watchCts.CancelAfter(TimeSpan.FromSeconds(seconds));

                try
                {
                    await foreach (
                        CurrentProgramSceneChangedEventArgs sceneEvent in _obsClient.Scenes.CurrentProgramSceneChangedStream(
                            cancellationToken: watchCts.Token
                        )
                    )
                    {
                        GetSceneItemListResponseData items = await _obsClient
                            .Scene(sceneEvent.EventData.Scene)
                            .GetItemListAsync(watchCts.Token);

                        UiSuccess(
                            $"Program scene is now '{sceneEvent.EventData.SceneName}' "
                                + $"({items.SceneItems.Count} item(s))"
                        );
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected: the watch window elapsed or the user cancelled.
                }

                UiInfo("Watch finished.");
                return false;
            }

            case "batch-example":
            {
                _logger.LogInformation("Running batch example...");
                ArrayBufferWriter<byte> batchSettingsBuffer = new();
                using (Utf8JsonWriter batchSettingsWriter = new(batchSettingsBuffer))
                {
                    batchSettingsWriter.WriteStartObject();
                    batchSettingsWriter.WriteString("text", "Batch updated!");
                    batchSettingsWriter.WriteEndObject();
                    batchSettingsWriter.Flush();
                }

                using JsonDocument batchSettingsDocument = JsonDocument.Parse(
                    batchSettingsBuffer.WrittenMemory
                );
                JsonElement batchSettingsPayload = batchSettingsDocument.RootElement.Clone();

                // The typed builder pairs each request type with its own data record, so a
                // request name can never be sent with the wrong payload. Add() remains for
                // raw items and hand-built JsonElement payloads.
                ObsBatchBuilder exampleBatch = new();
                _ = exampleBatch.General.GetVersion();
                _ = exampleBatch.Scenes.GetCurrentProgramScene();
                _ = exampleBatch.Inputs.GetInputList(
                    new GetInputListRequestData("text_gdiplus_v3")
                );
                _ = exampleBatch.General.Sleep(new SleepRequestData(sleepMillis: 100));
                _ = exampleBatch.Inputs.SetInputSettings(
                    new SetInputSettingsRequestData(
                        batchSettingsPayload,
                        inputName: "MyTextSource", // REPLACE WITH YOUR ACTUAL TEXT SOURCE NAME
                        overlay: true
                    )
                );

                // Add remains for anything the generated methods do not cover.
                _ = exampleBatch.Add("GetStats");

                // BatchResults is itself the list of results, so there is no reason to drop to
                // Raw here; keeping it means the typed references stay usable further down.
                BatchResults batchResults = await _obsClient
                    .CallBatchAsync(
                        exampleBatch,
                        executionType: RequestBatchExecutionType.SerialRealtime,
                        haltOnFailure: false, // Continue even if one fails
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false);

                Table batchTable = new()
                {
                    Title = new TableTitle($"Batch Results ({batchResults.Count} items)"),
                };
                _ = batchTable.AddColumn("Request");
                _ = batchTable.AddColumn("Status");
                _ = batchTable.AddColumn("Code");
                _ = batchTable.AddColumn("Details");
                foreach (RequestResponsePayload<object> result in batchResults)
                {
                    string shortId = result.RequestId[(result.RequestId.LastIndexOf('_') + 1)..];
                    string status = result.RequestStatus.Result
                        ? "[green]Success[/]"
                        : "[red]Failed[/]";
                    string details = string.Empty;
                    if (!result.RequestStatus.Result)
                    {
                        details = $"Error: {Markup.Escape(result.RequestStatus.Comment ?? "N/A")}";
                    }
                    else if (result.ResponseData is not null)
                    {
                        string responseJson = "Could not serialize response data";
                        try
                        {
                            responseJson = result.ResponseData is JsonElement jsonElement
                                ? jsonElement.GetRawText()
                                : result.ResponseData.ToString() ?? string.Empty;
                        }
                        catch
                        { /* Ignore serialization errors for logging */
                        }

                        details =
                            responseJson.Length > 140
                                ? $"{Markup.Escape(responseJson[..140])}..."
                                : Markup.Escape(responseJson);
                    }

                    _ = batchTable.AddRow(
                        $"{Markup.Escape(result.RequestType ?? "N/A")} / {Markup.Escape(shortId)}",
                        status,
                        result.RequestStatus.Code.ToString(),
                        details
                    );
                }
                AnsiConsole.Write(batchTable);

                _logger.LogInformation("Batch example finished.");
                return false;
            }

            case "cleanup-fixtures":
            {
                // The sweeps name everything they create with a known prefix, so a run that dies
                // before its teardown leaves findable litter rather than a puzzle.
                GetInputListResponseData allInputs = await _obsClient
                    .Inputs.GetInputListAsync(new(), cancellationToken)
                    .ConfigureAwait(false);
                int removedInputs = 0;
                foreach (
                    InputStub leftover in allInputs.Inputs.Where(i =>
                        i.InputName.StartsWith("__obsws", StringComparison.Ordinal)
                    )
                )
                {
                    await _obsClient
                        .Inputs.RemoveInputAsync(
                            new(inputName: leftover.InputName),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    removedInputs++;
                }

                GetSceneListResponseData allScenes = await _obsClient
                    .Scenes.GetSceneListAsync(new(), cancellationToken)
                    .ConfigureAwait(false);
                int removedScenes = 0;
                foreach (
                    SceneStub leftover in allScenes.Scenes.Where(sc =>
                        sc.SceneName.StartsWith("__obsws", StringComparison.Ordinal)
                    )
                )
                {
                    await _obsClient
                        .Scenes.RemoveSceneAsync(
                            new(sceneName: leftover.SceneName),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    removedScenes++;
                }

                UiSuccess(
                    $"Removed {removedInputs} leftover input(s) and {removedScenes} scene(s)."
                );
                return false;
            }

            case "list-subs":
                RenderKeyValueTable(
                    "Event Subscriptions",
                    [
                        (
                            "Current Intended Flags",
                            $"{_currentSubscriptionFlags} ({(EventSubscription)_currentSubscriptionFlags})"
                        ),
                        ("Note", "Reflects last requested flags, not server-acknowledged state."),
                    ]
                );
                return false;

            case "media":
            {
                // Typed enum rather than a protocol string constant.
                if (
                    args.Length < 2
                    || MediaInputActionExtensions.FromWireValue(args[1]) is null
                        && !Enum.TryParse(args[1], ignoreCase: true, out MediaInputAction _)
                )
                {
                    UiWarn("Usage: media <inputName> <play|pause|stop|restart|next|previous>");
                    return false;
                }

                if (!Enum.TryParse(args[1], ignoreCase: true, out MediaInputAction action))
                {
                    UiWarn($"Unknown media action '{args[1]}'.");
                    return false;
                }

                try
                {
                    await _obsClient.MediaInputs.TriggerMediaActionAsync(
                        args[0],
                        action,
                        cancellationToken
                    );
                    UiSuccess($"Sent {action} ({action.ToWireValue()}) to '{args[0]}'.");
                }
                catch (ObsWebSocketRequestException ex)
                {
                    // Typed failure carries the protocol status, so no message matching.
                    UiWarn(
                        $"OBS rejected {ex.RequestType} with code {(int?)ex.StatusCode}: {ex.Comment}"
                    );
                }

                return false;
            }

            case "set-subs":
                if (args.Length == 0 || !uint.TryParse(args[0], out uint newFlags))
                {
                    UiWarn("Usage: set-subs <numeric_flags>");
                    UiInfo("Example: set-subs 65 (General | Scenes | Inputs, 1 | 4 | 8 = 13)");
                    UiInfo("See ObsWebSocket.Core.Protocol.Generated.EventSubscription for flags.");
                    return false;
                }

                _logger.LogInformation(
                    "Attempting to re-identify with new subscription flags: {NewFlags} ({EnumFlags})",
                    newFlags,
                    (EventSubscription)newFlags
                );
                await _obsClient.ReidentifyAsync(newFlags, cancellationToken: cancellationToken);
                _currentSubscriptionFlags = (EventSubscription)newFlags;
                UiSuccess(
                    $"Re-identified successfully. Intended subscriptions set to: {_currentSubscriptionFlags}"
                );
                return false;

            case "get-all-settings-types":
                await GetAllSettingsTypesAsync(cancellationToken);
                return false;

            case "add-browser-source":
                await AddBrowserSourceAsync(cancellationToken);
                return false;
            // --- End of New Commands ---

            default:
                UiWarn($"Unknown command: '{command}'. Type 'help'.");
                return false;
        }
    }

    private IWebSocketMessageSerializer CreateSerializer(SerializationFormat format) =>
        format switch
        {
            SerializationFormat.MsgPack => new MsgPackMessageSerializer(
                _loggerFactory.CreateLogger<MsgPackMessageSerializer>()
            ),
            _ => new JsonMessageSerializer(_loggerFactory.CreateLogger<JsonMessageSerializer>()),
        };

    private (
        bool Observed,
        bool Valid,
        int ExtensionBagCount,
        int ExtensionEntryCount
    ) ValidateStubExtensionData(
        GetSceneListResponseData? scenes,
        GetInputListResponseData? inputs,
        string? firstInputName,
        SerializationFormat format
    )
    {
        List<Dictionary<string, JsonElement>?> extensionBags =
        [
            .. (scenes?.Scenes ?? []).Select(scene => scene.ExtensionData),
            .. (inputs?.Inputs ?? []).Select(input => input.ExtensionData),
        ];

        int extensionBagCount = extensionBags.Count(bag => bag is { Count: > 0 });
        int extensionEntryCount = extensionBags
            .Where(bag => bag is { Count: > 0 })
            .Sum(bag => bag!.Count);

        bool valid = true;
        foreach (
            Dictionary<string, JsonElement>? bag in extensionBags.Where(bag =>
                bag is { Count: > 0 }
            )
        )
        {
            foreach ((string _, JsonElement value) in bag!)
            {
                if (!IsValidExtensionDataValue(value))
                {
                    valid = false;
                    break;
                }
            }

            if (!valid)
            {
                break;
            }
        }

        bool observed = extensionBagCount > 0;
        if (observed)
        {
            _logger.LogInformation(
                "[{Format}] Stub ExtensionData validated: {BagCount} bag(s), {EntryCount} entries.",
                format,
                extensionBagCount,
                extensionEntryCount
            );
        }
        else
        {
            _logger.LogWarning(
                "[{Format}] Stub ExtensionData was not present in GetSceneList/GetInputList responses for input '{InputName}'.",
                format,
                firstInputName ?? "N/A"
            );
        }

        return (observed, valid, extensionBagCount, extensionEntryCount);
    }

    private static bool IsValidExtensionDataValue(JsonElement value)
    {
        try
        {
            if (value.ValueKind == JsonValueKind.Undefined)
            {
                return false;
            }

            _ = value.GetRawText();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryFindCustomEventPayloadByTestId(
        JsonElement source,
        string testId,
        out JsonElement payload
    ) => TryFindCustomEventPayloadByTestIdCore(source, testId, depth: 0, out payload);

    private static bool TryFindCustomEventPayloadByTestIdCore(
        JsonElement source,
        string testId,
        int depth,
        out JsonElement payload
    )
    {
        payload = default;
        if (depth > 8)
        {
            return false;
        }

        switch (source.ValueKind)
        {
            case JsonValueKind.Object:
            {
                if (
                    source.TryGetProperty("testId", out JsonElement idProperty)
                    && idProperty.ValueKind == JsonValueKind.String
                    && string.Equals(idProperty.GetString(), testId, StringComparison.Ordinal)
                )
                {
                    payload = source.Clone();
                    return true;
                }

                foreach (JsonProperty property in source.EnumerateObject())
                {
                    if (
                        TryFindCustomEventPayloadByTestIdCore(
                            property.Value,
                            testId,
                            depth + 1,
                            out payload
                        )
                    )
                    {
                        return true;
                    }
                }

                return false;
            }

            case JsonValueKind.Array:
            {
                foreach (JsonElement element in source.EnumerateArray())
                {
                    if (
                        TryFindCustomEventPayloadByTestIdCore(
                            element,
                            testId,
                            depth + 1,
                            out payload
                        )
                    )
                    {
                        return true;
                    }
                }

                return false;
            }

            case JsonValueKind.String:
            {
                string? rawString = source.GetString();
                if (string.IsNullOrWhiteSpace(rawString))
                {
                    return false;
                }

                string trimmed = rawString.Trim();
                if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
                {
                    return false;
                }

                try
                {
                    using JsonDocument parsed = JsonDocument.Parse(trimmed);
                    return TryFindCustomEventPayloadByTestIdCore(
                        parsed.RootElement,
                        testId,
                        depth + 1,
                        out payload
                    );
                }
                catch (JsonException)
                {
                    return false;
                }
            }

            default:
                return false;
        }
    }

    private ObsWebSocketClientOptions CloneOptionsForFormat(SerializationFormat format) =>
        new()
        {
            ServerUri = _baseOptions.ServerUri,
            Password = _baseOptions.Password,
            EventSubscriptions = _baseOptions.EventSubscriptions,
            HandshakeTimeoutMs = _baseOptions.HandshakeTimeoutMs,
            RequestTimeoutMs = _baseOptions.RequestTimeoutMs,
            Format = format,
            AutoReconnectEnabled = false,
            InitialReconnectDelayMs = _baseOptions.InitialReconnectDelayMs,
            MaxReconnectAttempts = _baseOptions.MaxReconnectAttempts,
            ReconnectBackoffMultiplier = _baseOptions.ReconnectBackoffMultiplier,
            MaxReconnectDelayMs = _baseOptions.MaxReconnectDelayMs,
        };

    // --- Helper to find Scene Item ID ---
    private async Task<long> GetSceneItemIdAsync(
        string sceneName,
        string sourceName,
        CancellationToken cancellationToken
    )
    {
        GetSceneItemIdResponseData? response = await _obsClient.SceneItems.GetSceneItemIdAsync(
            new GetSceneItemIdRequestData { SceneName = sceneName, SourceName = sourceName },
            cancellationToken: cancellationToken
        );

        return response?.SceneItemId == null
            ? throw new SceneItemNotFoundException(
                $"Source '{sourceName}' not found in scene '{sceneName}'."
            )
            : response.SceneItemId;
    }

    /// <summary>
    /// Shows what resolving a handle costs, what it buys, and what a miss reports.
    /// </summary>
    /// <remarks>
    /// The rest of the interactive commands address things by name, which is right for a name the
    /// operator just typed. This one is the counterpart: it turns a name into a uuid once, and
    /// everything after that survives a rename in OBS.
    /// </remarks>
    private async Task ResolveDemoAsync(
        string sceneName,
        string? sourceName,
        CancellationToken cancellationToken
    )
    {
        // One round trip. The protocol has no "uuid of the scene called X" request, so this is
        // GetSceneList and a scan.
        SceneOperations resolved = await _obsClient
            .Scene(sceneName)
            .ResolveAsync(cancellationToken);

        RenderKeyValueTable(
            "Resolved scene",
            [
                ("Given", sceneName),
                ("UUID", resolved.Handle.Uuid ?? "N/A"),
                ("Survives a rename", resolved.Handle.IsResolved ? "yes" : "no"),
            ]
        );

        // Held by uuid, so this reads the same scene even if it is renamed between the two calls.
        GetSceneItemListResponseData items = await resolved.GetItemListAsync(cancellationToken);
        UiInfo($"'{sceneName}' holds {items.SceneItems.Count} scene item(s).");

        if (sourceName is not null)
        {
            // The one lookup that is not a convenience: OBS addresses scene items by a number that
            // only GetSceneItemId reports, so an item known by source name cannot be acted on until
            // it has been resolved. The type system says so: ItemAsync returns the actable type.
            SceneItemOperations item = await resolved.ItemAsync(
                sourceName,
                cancellationToken: cancellationToken
            );
            GetSceneItemEnabledResponseData enabled = await item.GetEnabledAsync(cancellationToken);

            RenderKeyValueTable(
                "Resolved scene item",
                [
                    ("Source", sourceName),
                    ("Item id", item.Handle.SceneItemId.ToString(CultureInfo.InvariantCulture)),
                    ("Enabled", enabled.SceneItemEnabled ? "yes" : "no"),
                    ("Back up to", item.Scene.Handle.Uuid ?? item.Scene.Handle.Name ?? "N/A"),
                ]
            );
        }

        // The lookup already fetched the list, so a miss can name what
        // does exist. OBS itself can only answer ResourceNotFound and the name you gave it.
        try
        {
            _ = await _obsClient.Scenes.ResolveAsync(
                sceneName + "__no_such_scene",
                cancellationToken
            );
        }
        catch (ObsWebSocketResourceNotFoundException ex)
        {
            UiInfo($"A miss reports what exists: {ex.Message}");
        }
    }

    private async Task GetAllSettingsTypesAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Fetching all settings type schemas from OBS...");

        await DumpKindDefaultSettingsAsync(
            "Filter Kind Defaults",
            async ct =>
            {
                GetSourceFilterKindListResponseData? r =
                    await _obsClient.Filters.GetSourceFilterKindListAsync(cancellationToken: ct);
                return r?.SourceFilterKinds ?? [];
            },
            async (kind, ct) =>
            {
                GetSourceFilterDefaultSettingsResponseData? r =
                    await _obsClient.Filters.GetSourceFilterDefaultSettingsAsync(
                        new GetSourceFilterDefaultSettingsRequestData(kind),
                        cancellationToken: ct
                    );
                return r?.DefaultFilterSettings;
            },
            cancellationToken
        );

        await DumpKindDefaultSettingsAsync(
            "Input Kind Defaults",
            async ct =>
            {
                GetInputKindListResponseData? r = await _obsClient.Inputs.GetInputKindListAsync(
                    new GetInputKindListRequestData(unversioned: false),
                    cancellationToken: ct
                );
                return r?.InputKinds ?? [];
            },
            async (kind, ct) =>
            {
                GetInputDefaultSettingsResponseData? r =
                    await _obsClient.Inputs.GetInputDefaultSettingsAsync(
                        new GetInputDefaultSettingsRequestData(kind),
                        cancellationToken: ct
                    );
                return r?.DefaultInputSettings;
            },
            cancellationToken
        );

        await DumpOutputSettingsAsync(cancellationToken);
        await DumpStreamServiceSettingsAsync(cancellationToken);
    }

    private async Task DumpKindDefaultSettingsAsync(
        string panelTitle,
        Func<CancellationToken, Task<List<string>>> getKinds,
        Func<string, CancellationToken, Task<JsonElement?>> getDefaults,
        CancellationToken cancellationToken
    )
    {
        List<string> kinds;
        try
        {
            kinds = await getKinds(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve kind list for '{Panel}'.", panelTitle);
            UiError($"[red]Could not retrieve kind list for {panelTitle}.[/]");
            return;
        }

        if (kinds.Count == 0)
        {
            _logger.LogWarning("No kinds returned for '{Panel}'.", panelTitle);
            return;
        }

        _logger.LogInformation(
            "Found {Count} kinds for '{Panel}'. Fetching defaults...",
            kinds.Count,
            panelTitle
        );

        Dictionary<string, JsonElement?> results = new(StringComparer.OrdinalIgnoreCase);
        foreach (string kind in kinds)
        {
            try
            {
                results[kind] = await getDefaults(kind, cancellationToken);
            }
            catch (ObsWebSocketException ex)
            {
                _logger.LogWarning("Could not get defaults for '{Kind}': {Msg}", kind, ex.Message);
                results[kind] = null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error getting defaults for '{Kind}'.", kind);
                results[kind] = null;
            }
        }

        RenderJsonPanel(panelTitle, SerializeKindDefaults(results));
    }

    private async Task DumpOutputSettingsAsync(CancellationToken cancellationToken)
    {
        List<OutputStub> outputs;
        try
        {
            GetOutputListResponseData? response = await _obsClient.Outputs.GetOutputListAsync(
                cancellationToken: cancellationToken
            );
            outputs = response?.Outputs ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve output list.");
            UiError("[red]Could not retrieve output list.[/]");
            return;
        }

        if (outputs.Count == 0)
        {
            _logger.LogWarning("No outputs configured on this OBS instance.");
            return;
        }

        Dictionary<string, JsonElement?> results = new(StringComparer.OrdinalIgnoreCase);
        foreach (OutputStub output in outputs)
        {
            if (output.OutputName is not { } name)
            {
                continue;
            }

            string key = output.OutputKind is { } kind ? $"{name} ({kind})" : name;
            try
            {
                GetOutputSettingsResponseData? r = await _obsClient.Outputs.GetOutputSettingsAsync(
                    new GetOutputSettingsRequestData(outputName: name),
                    cancellationToken: cancellationToken
                );
                results[key] = r?.OutputSettings;
            }
            catch (ObsWebSocketException ex)
            {
                _logger.LogWarning(
                    "Could not get settings for output '{Name}': {Msg}",
                    name,
                    ex.Message
                );
                results[key] = null;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unexpected error getting settings for output '{Name}'.",
                    name
                );
                results[key] = null;
            }
        }

        RenderJsonPanel("Output Settings (current instances)", SerializeKindDefaults(results));
    }

    private async Task DumpStreamServiceSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            GetStreamServiceSettingsResponseData? response =
                await _obsClient.Config.GetStreamServiceSettingsAsync(
                    cancellationToken: cancellationToken
                );

            ArrayBufferWriter<byte> buf = new();
            using (Utf8JsonWriter w = new(buf, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("streamServiceType", response?.StreamServiceType);
                w.WritePropertyName("streamServiceSettings");
                if (response?.StreamServiceSettings is JsonElement el)
                {
                    el.WriteTo(w);
                }
                else
                {
                    w.WriteNullValue();
                }

                w.WriteEndObject();
                w.Flush();
            }

            RenderJsonPanel(
                "Stream Service Settings",
                System.Text.Encoding.UTF8.GetString(buf.WrittenSpan)
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve stream service settings.");
            UiError("[red]Could not retrieve stream service settings.[/]");
        }
    }

    private static string SerializeKindDefaults(Dictionary<string, JsonElement?> results)
    {
        ArrayBufferWriter<byte> buf = new();
        using Utf8JsonWriter w = new(buf, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        foreach ((string kind, JsonElement? value) in results)
        {
            w.WritePropertyName(kind);
            if (value is JsonElement el)
            {
                el.WriteTo(w);
            }
            else
            {
                w.WriteNullValue();
            }
        }
        w.WriteEndObject();
        w.Flush();
        return System.Text.Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    private async Task AddBrowserSourceAsync(CancellationToken cancellationToken)
    {
        // Step 1: Fetch scene list and determine current program scene
        GetSceneListResponseData? sceneList = await _obsClient.Scenes.GetSceneListAsync(
            new(),
            cancellationToken: cancellationToken
        );

        if (sceneList?.Scenes is null || sceneList.Scenes.Count == 0)
        {
            UiWarn("Could not retrieve scene list from OBS.");
            return;
        }

        string currentProgramScene = sceneList.CurrentProgramSceneName ?? string.Empty;

        List<string> sceneNames =
        [
            .. sceneList
                .Scenes.Select(s => s.SceneName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!),
        ];

        // Place current program scene first, then alphabetically
        List<string> orderedSceneNames = !string.IsNullOrEmpty(currentProgramScene)
            ?
            [
                .. sceneNames.Where(n => n == currentProgramScene),
                .. sceneNames.Where(n => n != currentProgramScene).OrderBy(n => n),
            ]
            : [.. sceneNames.OrderBy(n => n)];

        if (orderedSceneNames.Count == 0)
        {
            UiWarn("No scenes available in OBS.");
            return;
        }

        // Map display labels (with "(current program)" suffix) to actual names
        Dictionary<string, string> displayToSceneName = orderedSceneNames.ToDictionary(
            n => n == currentProgramScene ? $"{n} (current program)" : n,
            n => n
        );

        // Step 2: Prompt user to select a scene
        string selectedSceneDisplay = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select target [cyan]scene[/]:")
                .PageSize(15)
                .AddChoices(displayToSceneName.Keys)
        );

        string selectedScene = displayToSceneName[selectedSceneDisplay];

        // Step 3: Fetch scene items and all global browser_source inputs in parallel
        Task<GetSceneItemListResponseData> sceneItemsTask =
            _obsClient.SceneItems.GetSceneItemListAsync(
                new GetSceneItemListRequestData(sceneName: selectedScene),
                cancellationToken: cancellationToken
            );
        Task<GetInputListResponseData> browserInputsTask = _obsClient.Inputs.GetInputListAsync(
            new GetInputListRequestData("browser_source"),
            cancellationToken: cancellationToken
        );

        await Task.WhenAll(sceneItemsTask, browserInputsTask).ConfigureAwait(false);

        GetSceneItemListResponseData? sceneItemList = await sceneItemsTask;
        GetInputListResponseData? browserInputList = await browserInputsTask;

        // Find browser sources that already exist in the selected scene
        HashSet<string> sceneSourceNames =
            sceneItemList
                ?.SceneItems?.Select(si => si.SourceName ?? string.Empty)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? [];

        List<string> existingBrowserSourcesInScene =
            browserInputList
                ?.Inputs?.Where(i => sceneSourceNames.Contains(i.InputName ?? string.Empty))
                .Select(i => i.InputName!)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n)
                .ToList()
            ?? [];

        // Step 4: Prompt to create a new source or update an existing browser source
        const string CreateNewChoice = "+ Create new browser source";
        List<string> sourceChoices = [CreateNewChoice, .. existingBrowserSourcesInScene];

        string selectedSourceChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"Browser source in scene [cyan]{Markup.Escape(selectedScene)}[/]:")
                .PageSize(15)
                .AddChoices(sourceChoices)
        );

        bool isNewSource = selectedSourceChoice == CreateNewChoice;
        string sourceName = isNewSource
            ? AnsiConsole.Prompt(
                new TextPrompt<string>("New browser source [cyan]name[/]:").Validate(s =>
                    !string.IsNullOrWhiteSpace(s)
                        ? ValidationResult.Success()
                        : ValidationResult.Error("[red]Name cannot be empty.[/]")
                )
            )
            : selectedSourceChoice;

        // Step 5: Get canvas dimensions from video settings
        GetVideoSettingsResponseData? videoSettings = await _obsClient.Config.GetVideoSettingsAsync(
            cancellationToken: cancellationToken
        );

        if (videoSettings is null)
        {
            UiWarn("Could not retrieve video settings from OBS.");
            return;
        }

        int canvasWidth = (int)videoSettings.BaseWidth;
        int canvasHeight = (int)videoSettings.BaseHeight;
        UiInfo($"Canvas resolution: {canvasWidth}x{canvasHeight}");

        // Step 6: Prompt for the overlay URL
        string url = AnsiConsole.Prompt(
            new TextPrompt<string>("Browser source [cyan]URL[/]:").Validate(s =>
                !string.IsNullOrWhiteSpace(s)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]URL cannot be empty.[/]")
            )
        );

        // Step 7: Build the browser source settings payload
        const string OverlayCss =
            "body { background-color: rgba(0, 0, 0, 0); margin: 0px auto; overflow: hidden; }";

        BrowserSourceSettings browserSettings = new(
            Url: url,
            Width: canvasWidth,
            Height: canvasHeight,
            FpsCustom: false,
            Fps: 30,
            Css: OverlayCss,
            RerouteAudio: true,
            WebpageControlLevel: 5,
            RestartWhenActive: true
        );

        // The two paths below reach the same scene item by different routes: creating one answers
        // with its id, finding an existing one costs the lookup only OBS can answer. From here on
        // the rest of the method does not care which, because both produce the same handle.
        SceneItemOperations item;

        // Step 8: Create new input or update existing source settings
        if (isNewSource)
        {
            UiInfo($"Creating browser source '{sourceName}' in scene '{selectedScene}'...");

            // Protocol level: the typed-settings CreateInput is a hand-written group helper, so it
            // has no handle form. Its response carries the new scene item's id, which is what the
            // handle below is built from.
            CreateInputResponseData? createResult = await _obsClient.Inputs.CreateInputAsync(
                inputKind: "browser_source",
                inputName: sourceName,
                settings: browserSettings,
                sceneName: selectedScene,
                sceneItemEnabled: true,
                cancellationToken: cancellationToken
            );

            if (createResult is null)
            {
                UiWarn($"No response received when creating browser source '{sourceName}'.");
                return;
            }

            item = _obsClient.Scene(selectedScene).Item(createResult.SceneItemId);
            UiSuccess($"Created '{sourceName}' (scene item ID: {createResult.SceneItemId}).");
        }
        else
        {
            UiInfo($"Updating browser source '{sourceName}' settings...");

            // overlay: false resets to defaults, then applies all new settings cleanly
            await _obsClient.Inputs.SetInputSettingsAsync(
                inputName: sourceName,
                settings: browserSettings,
                overlay: false,
                cancellationToken: cancellationToken
            );

            item = await _obsClient
                .Scene(selectedScene)
                .ItemAsync(sourceName, cancellationToken: cancellationToken);
            UiSuccess($"Updated '{sourceName}' (scene item ID: {item.Handle.SceneItemId}).");
        }

        // Step 9: Set Blend Mode to Normal (explicit, even though it is the default)
        await item.SetBlendModeAsync("OBS_BLEND_NORMAL", cancellationToken);

        // The obs-websocket v5 protocol does not expose SetSceneItemPrivateSettings,
        // so Blending Method (SRGB Off) cannot be set programmatically via this API.
        AnsiConsole.MarkupLine(
            "[yellow]Action required:[/] Set [bold]Blending Method[/] to [bold]sRGB Off[/] manually in OBS."
        );
        AnsiConsole.MarkupLine(
            "[grey]  Right-click the scene item → Blending → Method → sRGB Off[/]"
        );

        RenderKeyValueTable(
            $"Browser Source: {(isNewSource ? "Created" : "Updated")}",
            [
                ("Name", sourceName),
                ("Scene", selectedScene),
                ("URL", url),
                ("Dimensions", $"{canvasWidth} x {canvasHeight} (matches canvas)"),
                ("FPS", "30 (fps_custom: false, uses OBS default)"),
                ("CSS", OverlayCss),
                ("Audio", "OBS handled (reroute_audio: true)"),
                ("OBS Control Level", "Full (webpage_control_level: 5)"),
                ("Refresh on Scene Active", "Yes (restart_when_active: true)"),
                ("Blend Mode", "Normal (OBS_BLEND_NORMAL)"),
                ("Blending Method", "sRGB Off, set manually in OBS (not exposed by WebSocket v5)"),
            ]
        );
    }

    private static void RenderCommandHelp()
    {
        Table commandTable = new() { Title = new TableTitle("Available Commands") };
        _ = commandTable.AddColumn("Command");
        _ = commandTable.AddColumn("Description");
        _ = commandTable.AddRow(Markup.Escape("help"), Markup.Escape("Show this help"));
        _ = commandTable.AddRow(Markup.Escape("exit"), Markup.Escape("Exit the application"));
        _ = commandTable.AddRow(Markup.Escape("status"), Markup.Escape("Show connection status"));
        _ = commandTable.AddRow(
            Markup.Escape("version"),
            Markup.Escape("Get OBS and WebSocket version info")
        );
        _ = commandTable.AddRow(Markup.Escape("scene"), Markup.Escape("Get current program scene"));
        _ = commandTable.AddRow(
            Markup.Escape("resolve [scene] [source]"),
            Markup.Escape("Resolve a name to a uuid handle, and show what a miss reports")
        );
        _ = commandTable.AddRow(
            Markup.Escape("mute [input name]"),
            Markup.Escape("Toggle mute for audio input, through an input handle")
        );
        _ = commandTable.AddRow(
            Markup.Escape("unmute [input name]"),
            Markup.Escape("Alias for mute")
        );
        _ = commandTable.AddRow(
            Markup.Escape("get-input-settings [scene] [input]"),
            Markup.Escape("Get settings for an input")
        );
        _ = commandTable.AddRow(
            Markup.Escape("set-text [scene] [input] [text...]"),
            Markup.Escape("Set text on text source")
        );
        _ = commandTable.AddRow(
            Markup.Escape("list-filters [source]"),
            Markup.Escape("List filters for source, through a source handle")
        );
        _ = commandTable.AddRow(
            Markup.Escape("toggle-filter [source] [filter]"),
            Markup.Escape("Toggle filter enabled state, through one filter handle")
        );
        _ = commandTable.AddRow(
            Markup.Escape("media [input] [action]"),
            Markup.Escape("Media transport via the typed MediaInputAction enum")
        );
        _ = commandTable.AddRow(
            Markup.Escape("watch [seconds]"),
            Markup.Escape(
                "Stream scene changes with await foreach, acting on the event's free handle (default 15s)"
            )
        );
        _ = commandTable.AddRow(
            Markup.Escape("batch-example"),
            Markup.Escape("Run sample batch request sequence via the typed builder")
        );
        _ = commandTable.AddRow(
            Markup.Escape("list-subs"),
            Markup.Escape("Show intended event subscription flags")
        );
        _ = commandTable.AddRow(
            Markup.Escape("set-subs <numeric_flags>"),
            Markup.Escape("Reidentify with new event flags")
        );
        _ = commandTable.AddRow(
            Markup.Escape("get-all-settings-types"),
            Markup.Escape(
                "Dump default settings for all filter kinds, input kinds, and current stream service"
            )
        );
        _ = commandTable.AddRow(
            Markup.Escape("add-browser-source"),
            Markup.Escape("Create or update a fullscreen browser source overlay in a scene")
        );
        AnsiConsole.Write(commandTable);
    }

    private static void RenderKeyValueTable(
        string title,
        IReadOnlyList<(string Key, string Value)> rows
    )
    {
        Table table = new() { Title = new TableTitle(title) };
        _ = table.AddColumn("Property");
        _ = table.AddColumn("Value");
        foreach ((string key, string value) in rows)
        {
            _ = table.AddRow(Markup.Escape(key), Markup.Escape(value));
        }

        AnsiConsole.Write(table);
    }

    private static void RenderJsonPanel(string title, string json)
    {
        Panel panel = new(new Markup(Markup.Escape(json)))
        {
            Header = new PanelHeader(title),
            Border = BoxBorder.Rounded,
            Expand = true,
        };
        AnsiConsole.Write(panel);
    }

    private static void UiInfo(string message) =>
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(message)}[/]");

    private static void UiWarn(string message) =>
        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(message)}[/]");

    private static void UiSuccess(string message) =>
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(message)}[/]");

    private static void UiError(string message) =>
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");

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

    private void OnStreamStateChanged(object? sender, StreamStateChangedEventArgs e)
    {
        string description = e.EventData.OutputState switch
        {
            OutputState.Starting => "starting up",
            OutputState.Started => "live",
            OutputState.Stopping => "shutting down",
            OutputState.Stopped => "offline",
            OutputState.Reconnecting => "reconnecting",
            OutputState.Reconnected => "reconnected",
            OutputState.Paused => "paused",
            OutputState.Unknown => "in an unknown state",
            _ => "in an unhandled state",
        };

        _logger.LogInformation(
            "[OBS Event] Stream State Changed: Active={OutputActive}, State={State}",
            e.EventData.OutputActive,
            description
        );
    }

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

// ── Consumer-defined settings types (Mode 3 example) ──────────────────────────
// These are NOT registered in the library's ObsWebSocketSettingsJsonContext.
// They represent what a consumer app would define to map only the fields it cares about,
// using its own JsonSerializerContext and passing an explicit JsonTypeInfo<T> to the helpers.

internal sealed record WorkerBrowserUrlSettings(
    [property: JsonPropertyName("url")] string? Url = null
);

internal sealed record WorkerGainDbSettings([property: JsonPropertyName("db")] double? Db = null);

[JsonSerializable(typeof(WorkerBrowserUrlSettings))]
[JsonSerializable(typeof(WorkerGainDbSettings))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
)]
internal sealed partial class WorkerSettingsJsonContext : JsonSerializerContext { }
