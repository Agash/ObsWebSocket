using System.Buffers;
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
    HealthCheckService healthChecks,
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
            if (_validationOptions.RunValidationOnStartup)
            {
                _logger.LogInformation("Running startup transport validation suite...");
                await RunTransportValidationSuiteAsync(stoppingToken).ConfigureAwait(false);
            }

            if (
                string.Equals(
                    _startupCommandOptions.Command,
                    "run-transport-tests",
                    StringComparison.OrdinalIgnoreCase
                )
            )
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

            case "mute":
            case "unmute":
                if (args.Length == 0)
                {
                    UiWarn($"Usage: {command} [input name]");
                    return false;
                }

                string inputNameToMute = string.Join(" ", args);
                _logger.LogInformation("Toggling mute for input: {InputName}", inputNameToMute);
                ToggleInputMuteResponseData? muteState =
                    await _obsClient.Inputs.ToggleInputMuteAsync(
                        new ToggleInputMuteRequestData(inputNameToMute),
                        cancellationToken: cancellationToken
                    );
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
                    int sceneItemId = await GetSceneItemIdAsync(
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
                    int sceneItemId = await GetSceneItemIdAsync(
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

                    // Uses SetInputTextAsync helper which serializes TextGdiPlusInputSettings internally.
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
                GetSourceFilterListResponseData? filterList =
                    await _obsClient.Filters.GetSourceFilterListAsync(
                        new GetSourceFilterListRequestData(sourceName: sourceForFilters),
                        cancellationToken: cancellationToken
                    );
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
                            System.Globalization.CultureInfo.InvariantCulture
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

                // 1. Get current filter state
                GetSourceFilterResponseData? currentFilterState =
                    await _obsClient.Filters.GetSourceFilterAsync(
                        new GetSourceFilterRequestData
                        {
                            SourceName = sourceForToggle,
                            FilterName = filterToToggle,
                        },
                        cancellationToken: cancellationToken
                    );

                if (currentFilterState is null)
                {
                    UiWarn(
                        $"Could not find filter '{filterToToggle}' on source '{sourceForToggle}'."
                    );
                    return false;
                }

                // 2. Toggle the state
                bool newState = !currentFilterState.FilterEnabled;
                await _obsClient.Filters.SetSourceFilterEnabledAsync(
                    new SetSourceFilterEnabledRequestData
                    {
                        SourceName = sourceForToggle,
                        FilterName = filterToToggle,
                        FilterEnabled = newState,
                    },
                    cancellationToken: cancellationToken
                );

                UiSuccess(
                    $"Filter '{filterToToggle}' on '{sourceForToggle}' toggled to {(newState ? "ENABLED" : "DISABLED")}"
                );
                return false;

            case "watch":
            {
                // Streams are the ergonomic way to observe events: subscribe for the lifetime
                // of the loop, no handler bookkeeping, and cancellation ends it cleanly. The
                // classic events on the client are untouched and still work alongside this.
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
                        UiSuccess($"Program scene is now '{sceneEvent.EventData.SceneName}'");
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

            case "run-transport-tests":
                await RunTransportValidationSuiteAsync(cancellationToken).ConfigureAwait(false);
                return false;

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

    private async Task RunTransportValidationSuiteAsync(CancellationToken cancellationToken)
    {
        int iterations = Math.Max(1, _validationOptions.ValidationIterations);
        Rule rule = new("[cyan]Transport Validation[/]") { Justification = Justify.Left };
        AnsiConsole.Write(rule);
        for (int i = 0; i < iterations; i++)
        {
            _logger.LogInformation(
                "Running transport validation iteration {Current}/{Total} (JSON then MsgPack)...",
                i + 1,
                iterations
            );
            UiInfo($"Iteration {i + 1}/{iterations}: JSON then MsgPack");
            await RunTransportValidationCycleAsync(SerializationFormat.Json, cancellationToken)
                .ConfigureAwait(false);
            await RunTransportValidationCycleAsync(SerializationFormat.MsgPack, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RunTransportValidationCycleAsync(
        SerializationFormat format,
        CancellationToken cancellationToken
    )
    {
        ObsWebSocketClientOptions cycleOptions = CloneOptionsForFormat(format);
        IWebSocketMessageSerializer serializer = CreateSerializer(format);

        await using ObsWebSocketClient cycleClient = new(
            _loggerFactory.CreateLogger<ObsWebSocketClient>(),
            serializer,
            Options.Create(cycleOptions),
            _connectionFactory
        );

        await cycleClient.ConnectAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GetVersionResponseData? version = await cycleClient
                .General.GetVersionAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (
                version is null
                || string.IsNullOrWhiteSpace(version.ObsVersion)
                || string.IsNullOrWhiteSpace(version.ObsWebSocketVersion)
                || version.RpcVersion <= 0
            )
            {
                throw new InvalidOperationException(
                    $"[{format}] Invalid GetVersion response. ObsVersion='{version?.ObsVersion}', ObsWebSocketVersion='{version?.ObsWebSocketVersion}', RpcVersion={version?.RpcVersion}."
                );
            }

            GetVersionResponseData validatedVersion = version;

            _logger.LogInformation(
                "[{Format}] Connected to OBS {ObsVersion} (RPC {RpcVersion})",
                format,
                validatedVersion.ObsVersion,
                validatedVersion.RpcVersion
            );

            GetSceneListResponseData? scenes = await cycleClient
                .Scenes.GetSceneListAsync(new(), cancellationToken)
                .ConfigureAwait(false);
            if (scenes?.Scenes is null || scenes.Scenes.Count == 0)
            {
                throw new InvalidOperationException($"[{format}] GetSceneList returned no scenes.");
            }

            _logger.LogInformation(
                "[{Format}] Scene stubs deserialized: {SceneCount}",
                format,
                scenes?.Scenes?.Count ?? 0
            );
            int sceneCount = scenes?.Scenes?.Count ?? 0;

            GetInputListResponseData? inputs = await cycleClient
                .Inputs.GetInputListAsync(new(), cancellationToken)
                .ConfigureAwait(false);
            if (inputs?.Inputs is null || inputs.Inputs.Count == 0)
            {
                throw new InvalidOperationException($"[{format}] GetInputList returned no inputs.");
            }

            _logger.LogInformation(
                "[{Format}] Input stubs deserialized: {InputCount}",
                format,
                inputs?.Inputs?.Count ?? 0
            );
            int inputCount = inputs?.Inputs?.Count ?? 0;

            int filterCount = 0;
            string? inputName = inputs?.Inputs?.FirstOrDefault()?.InputName;
            if (!string.IsNullOrWhiteSpace(inputName))
            {
                GetSourceFilterListResponseData? filters = await cycleClient
                    .Filters.GetSourceFilterListAsync(
                        new GetSourceFilterListRequestData(sourceName: inputName),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "[{Format}] Filter stubs deserialized for '{InputName}': {FilterCount}",
                    format,
                    inputName,
                    filters?.Filters?.Count ?? 0
                );
                filterCount = filters?.Filters?.Count ?? 0;
            }

            GetSourceFilterKindListResponseData? filterKinds = await cycleClient
                .Filters.GetSourceFilterKindListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (filterKinds?.SourceFilterKinds is null || filterKinds.SourceFilterKinds.Count == 0)
            {
                throw new InvalidOperationException(
                    $"[{format}] GetSourceFilterKindList returned no filter kinds."
                );
            }

            _logger.LogInformation(
                "[{Format}] Filter kind entries: {KindCount}",
                format,
                filterKinds?.SourceFilterKinds?.Count ?? 0
            );
            int filterKindCount = filterKinds?.SourceFilterKinds?.Count ?? 0;

            (
                bool extensionDataObserved,
                bool extensionDataValid,
                int extensionBagCount,
                int extensionEntryCount
            ) = ValidateStubExtensionData(scenes, inputs, inputName, format);

            if (extensionDataObserved && !extensionDataValid)
            {
                throw new InvalidOperationException(
                    $"[{format}] Stub ExtensionData validation failed."
                );
            }

            string testId = $"{format}-custom-{Guid.NewGuid():N}";
            JsonElement customPayload = JsonDocument
                .Parse(
                    $$"""
                    {
                      "testId": "{{testId}}",
                      "format": "{{format}}",
                      "nested": {
                        "enabled": true,
                        "levels": [1, 2, 3]
                      }
                    }
                    """
                )
                .RootElement.Clone();

            Task<CustomEventEventArgs> waitForCustomEvent =
                cycleClient.WaitForEventAsync<CustomEventEventArgs>(
                    predicate: _ => true,
                    timeout: TimeSpan.FromSeconds(2),
                    cancellationToken: cancellationToken
                );

            await cycleClient
                .General.BroadcastCustomEventAsync(
                    new BroadcastCustomEventRequestData(customPayload),
                    cancellationToken
                )
                .ConfigureAwait(false);

            CustomEventEventArgs? customEvent = null;
            try
            {
                customEvent = await waitForCustomEvent.ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Reported below as unverified.
            }
            bool customEventVerified = false;
            if (
                customEvent?.EventData.EventData is JsonElement receivedCustomData
                && TryFindCustomEventPayloadByTestId(
                    receivedCustomData,
                    testId,
                    out JsonElement actualCustomData
                )
                && actualCustomData.GetProperty("testId").GetString() == testId
                && actualCustomData.GetProperty("nested").GetProperty("enabled").GetBoolean()
                && actualCustomData.GetProperty("nested").GetProperty("levels").GetArrayLength()
                    == 3
            )
            {
                customEventVerified = true;
                _logger.LogInformation(
                    "[{Format}] CustomEvent roundtrip verified with testId {TestId}.",
                    format,
                    testId
                );
            }
            else
            {
                _logger.LogWarning(
                    "[{Format}] CustomEvent payload verification skipped (event received: {Received}).",
                    format,
                    customEvent is not null
                );
            }

            // The low level path, on purpose: the raw overload takes request items rather than the
            // typed builder, and still works for anyone who needs to hand roll a batch.
            List<RequestResponsePayload<object>> batch = await cycleClient
                .CallBatchAsync(
                    [new("GetVersion", null), new("GetSceneList", null)],
                    executionType: RequestBatchExecutionType.SerialRealtime,
                    haltOnFailure: false,
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
            if (batch.Count != 2 || batch.Any(result => !result.RequestStatus.Result))
            {
                throw new InvalidOperationException(
                    $"[{format}] Batch validation failed. Count={batch.Count}."
                );
            }

            _logger.LogInformation(
                "[{Format}] Batch call results: {ResultCount}",
                format,
                batch.Count
            );

            List<(string Label, bool Pass, string Detail)> settingsResults =
                await ValidateSettingsModesAsync(cycleClient, inputs, cancellationToken)
                    .ConfigureAwait(false);

            List<(string Label, bool Pass, string Detail)> modernResults =
                await ValidateModernApisAsync(cycleClient, healthChecks, cancellationToken)
                    .ConfigureAwait(false);

            Table summary = new() { Title = new TableTitle($"{format} Validation Summary") };
            _ = summary.AddColumn("Check");
            _ = summary.AddColumn("Result");
            _ = summary.AddRow("OBS Version", Markup.Escape(validatedVersion.ObsVersion ?? "N/A"));
            _ = summary.AddRow("RPC", validatedVersion.RpcVersion.ToString());
            _ = summary.AddRow("Scenes", sceneCount.ToString());
            _ = summary.AddRow("Inputs", inputCount.ToString());
            _ = summary.AddRow("Filters (first input)", filterCount.ToString());
            _ = summary.AddRow("Filter Kinds", filterKindCount.ToString());
            _ = summary.AddRow(
                "Stub ExtensionData",
                extensionDataObserved
                    ? $"[green]Pass[/] ({extensionBagCount} bag(s), {extensionEntryCount} entries)"
                    : "[yellow]Unverified[/]"
            );
            _ = summary.AddRow(
                "CustomEvent",
                customEventVerified ? "[green]Pass[/]" : "[yellow]Unverified[/]"
            );
            _ = summary.AddRow("Batch", $"{batch.Count} result(s)");
            foreach ((string label, bool pass, string detail) in settingsResults)
            {
                _ = summary.AddRow(
                    Markup.Escape(label),
                    pass
                        ? $"[green]Pass[/] — {Markup.Escape(detail)}"
                        : $"[red]Fail[/] — {Markup.Escape(detail)}"
                );
            }
            foreach ((string label, bool pass, string detail) in modernResults)
            {
                _ = summary.AddRow(
                    Markup.Escape(label),
                    pass
                        ? $"[green]Pass[/] - {Markup.Escape(detail)}"
                        : $"[red]Fail[/] - {Markup.Escape(detail)}"
                );
            }
            AnsiConsole.Write(summary);
        }
        finally
        {
            if (cycleClient.IsConnected)
            {
                await cycleClient
                    .DisconnectAsync(cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Validates all three settings API modes for both InputSettings and FilterSettings.
    /// All operations are read-then-write-back (overlay:true) so they are non-destructive.
    /// Requires at least one browser_source and one input with a gain_filter in OBS.
    /// </summary>
    private static async Task<
        List<(string Label, bool Pass, string Detail)>
    > ValidateSettingsModesAsync(
        ObsWebSocketClient client,
        GetInputListResponseData? inputs,
        CancellationToken cancellationToken
    )
    {
        List<(string Label, bool Pass, string Detail)> results = [];
        if (inputs is null)
        {
            results.Add(("Settings [all modes]", false, "GetInputList returned null"));
            return results;
        }

        // ── InputSettings ─────────────────────────────────────────────────────
        string? browserInputName = inputs
            .Inputs?.FirstOrDefault(i =>
                string.Equals(i.InputKind, "browser_source", StringComparison.OrdinalIgnoreCase)
            )
            ?.InputName;

        if (string.IsNullOrEmpty(browserInputName))
        {
            results.Add(
                ("InputSettings [all modes]", false, "No browser_source in OBS — add one to test")
            );
        }
        else
        {
            // Mode 1: raw JsonElement via protocol-level call
            results.Add(
                await TrySettingsCheckAsync(
                    "InputSettings Mode1 (raw JsonElement)",
                    async () =>
                    {
                        GetInputSettingsResponseData? r = await client.Inputs.GetInputSettingsAsync(
                            new GetInputSettingsRequestData(browserInputName),
                            cancellationToken
                        );
                        if (r?.InputSettings is not JsonElement el)
                        {
                            return (false, "null InputSettings in response");
                        }

                        await client.Inputs.SetInputSettingsAsync(
                            new SetInputSettingsRequestData(
                                el,
                                inputName: browserInputName,
                                overlay: true
                            ),
                            cancellationToken
                        );
                        string url = el.TryGetProperty("url", out JsonElement p)
                            ? p.GetString() ?? "(no url)"
                            : "(no url key)";
                        return (true, $"'{browserInputName}' url={url}");
                    }
                )
            );

            // Mode 2: library-registered type via implicit GetTypeInfo lookup
            results.Add(
                await TrySettingsCheckAsync(
                    "InputSettings Mode2 (BrowserSourceSettings)",
                    async () =>
                    {
                        BrowserSourceSettings? s =
                            await client.Inputs.GetInputSettingsAsync<BrowserSourceSettings>(
                                browserInputName,
                                cancellationToken
                            );
                        if (s is null)
                        {
                            return (false, "null result");
                        }

                        await client.Inputs.SetInputSettingsAsync(
                            browserInputName,
                            s,
                            overlay: true,
                            cancellationToken: cancellationToken
                        );
                        return (true, $"'{browserInputName}' url={s.Url ?? "(null)"}");
                    }
                )
            );

            // Mode 3: consumer-defined type with explicit JsonTypeInfo<T>
            results.Add(
                await TrySettingsCheckAsync(
                    "InputSettings Mode3 (consumer JsonTypeInfo)",
                    async () =>
                    {
                        JsonTypeInfo<WorkerBrowserUrlSettings> typeInfo = WorkerSettingsJsonContext
                            .Default
                            .WorkerBrowserUrlSettings;
                        WorkerBrowserUrlSettings? s = await client.Inputs.GetInputSettingsAsync(
                            browserInputName,
                            typeInfo,
                            cancellationToken
                        );
                        if (s is null)
                        {
                            return (false, "null result");
                        }

                        await client.Inputs.SetInputSettingsAsync(
                            browserInputName,
                            s,
                            typeInfo,
                            overlay: true,
                            cancellationToken: cancellationToken
                        );
                        return (true, $"'{browserInputName}' url={s.Url ?? "(null)"}");
                    }
                )
            );
        }

        // ── FilterSettings ────────────────────────────────────────────────────
        // Find first gain_filter across the first 5 inputs.
        string? filterSourceName = null;
        string? gainFilterName = null;
        foreach (
            Core.Protocol.Common.InputStub input in inputs
                .Inputs?.Where(i => !string.IsNullOrEmpty(i.InputName))
                .Take(5)
                ?? []
        )
        {
            try
            {
                GetSourceFilterListResponseData? fl = await client.Filters.GetSourceFilterListAsync(
                    new GetSourceFilterListRequestData(sourceName: input.InputName!),
                    cancellationToken
                );
                Core.Protocol.Common.FilterStub? gain = fl?.Filters?.FirstOrDefault(f =>
                    string.Equals(f.FilterKind, "gain_filter", StringComparison.OrdinalIgnoreCase)
                );
                if (gain?.FilterName is not null)
                {
                    filterSourceName = input.InputName;
                    gainFilterName = gain.FilterName;
                    break;
                }
            }
            catch
            { /* skip inputs we can't query */
            }
        }

        if (string.IsNullOrEmpty(filterSourceName) || string.IsNullOrEmpty(gainFilterName))
        {
            results.Add(
                (
                    "FilterSettings [all modes]",
                    false,
                    "No gain_filter found — add one to an input in OBS"
                )
            );
        }
        else
        {
            // Mode 1: raw JsonElement via protocol-level call
            results.Add(
                await TrySettingsCheckAsync(
                    "FilterSettings Mode1 (raw JsonElement)",
                    async () =>
                    {
                        GetSourceFilterResponseData? r = await client.Filters.GetSourceFilterAsync(
                            new GetSourceFilterRequestData
                            {
                                SourceName = filterSourceName,
                                FilterName = gainFilterName,
                            },
                            cancellationToken
                        );
                        if (r?.FilterSettings is not JsonElement el)
                        {
                            return (false, "null FilterSettings in response");
                        }

                        await client.Filters.SetSourceFilterSettingsAsync(
                            new SetSourceFilterSettingsRequestData(
                                gainFilterName,
                                el,
                                sourceName: filterSourceName,
                                overlay: true
                            ),
                            cancellationToken
                        );
                        string db = el.TryGetProperty("db", out JsonElement p)
                            ? p.GetDouble().ToString("F1")
                            : "(no db key)";
                        return (true, $"'{filterSourceName}/{gainFilterName}' db={db}");
                    }
                )
            );

            // Mode 2: library-registered type via implicit GetTypeInfo lookup
            results.Add(
                await TrySettingsCheckAsync(
                    "FilterSettings Mode2 (GainFilterSettings)",
                    async () =>
                    {
                        GainFilterSettings? s =
                            await client.Filters.GetSourceFilterSettingsAsync<GainFilterSettings>(
                                filterSourceName,
                                gainFilterName,
                                cancellationToken
                            );
                        if (s is null)
                        {
                            return (false, "null result");
                        }

                        await client.Filters.SetSourceFilterSettingsAsync(
                            filterSourceName,
                            gainFilterName,
                            s,
                            overlay: true,
                            cancellationToken: cancellationToken
                        );
                        return (
                            true,
                            $"'{filterSourceName}/{gainFilterName}' db={s.Db?.ToString("F1") ?? "(null)"}"
                        );
                    }
                )
            );

            // Mode 3: consumer-defined type with explicit JsonTypeInfo<T>
            results.Add(
                await TrySettingsCheckAsync(
                    "FilterSettings Mode3 (consumer JsonTypeInfo)",
                    async () =>
                    {
                        JsonTypeInfo<WorkerGainDbSettings> typeInfo = WorkerSettingsJsonContext
                            .Default
                            .WorkerGainDbSettings;
                        WorkerGainDbSettings? s = await client.Filters.GetSourceFilterSettingsAsync(
                            filterSourceName,
                            gainFilterName,
                            typeInfo,
                            cancellationToken
                        );
                        if (s is null)
                        {
                            return (false, "null result");
                        }

                        await client.Filters.SetSourceFilterSettingsAsync(
                            filterSourceName,
                            gainFilterName,
                            s,
                            typeInfo,
                            overlay: true,
                            cancellationToken: cancellationToken
                        );
                        return (
                            true,
                            $"'{filterSourceName}/{gainFilterName}' db={s.Db?.ToString("F1") ?? "(null)"}"
                        );
                    }
                )
            );
        }

        return results;
    }

    /// <summary>
    /// Exercises the modern conveniences against a scene and input this method creates itself,
    /// so the run does not depend on any particular OBS layout. Everything it makes is removed
    /// again, whether the checks pass or not.
    /// </summary>
    private static async Task<
        List<(string Label, bool Pass, string Detail)>
    > ValidateModernApisAsync(
        ObsWebSocketClient client,
        HealthCheckService healthChecks,
        CancellationToken cancellationToken
    )
    {
        List<(string Label, bool Pass, string Detail)> results = [];

        string suffix = Guid.NewGuid().ToString("N")[..8];
        string sceneName = $"__obsws_validation_{suffix}";
        string inputName = $"__obsws_input_{suffix}";

        GetSceneListResponseData? sceneList = await client
            .Scenes.GetSceneListAsync(new(), cancellationToken)
            .ConfigureAwait(false);
        string originalScene = sceneList?.CurrentProgramSceneName ?? string.Empty;

        bool sceneCreated = false;
        bool inputCreated = false;

        try
        {
            await client
                .Scenes.CreateSceneAsync(new CreateSceneRequestData(sceneName), cancellationToken)
                .ConfigureAwait(false);
            sceneCreated = true;

            results.Add(
                await TrySettingsCheckAsync(
                        "SceneExistsAsync",
                        async () =>
                        {
                            bool present = await client
                                .Scenes.SceneExistsAsync(sceneName, cancellationToken)
                                .ConfigureAwait(false);
                            bool absent = await client
                                .Scenes.SceneExistsAsync(sceneName + "__nope", cancellationToken)
                                .ConfigureAwait(false);
                            return (present && !absent, $"present={present}, absent={!absent}");
                        }
                    )
                    .ConfigureAwait(false)
            );

            // A media source carries audio, so the volume and media transport helpers apply.
            _ = await client
                .Inputs.CreateInputAsync(
                    "ffmpeg_source",
                    inputName,
                    new MediaSourceSettings(IsLocalFile: true),
                    sceneName: sceneName,
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
            inputCreated = true;

            results.Add(
                await TrySettingsCheckAsync(
                        "FindSceneItemIdAsync",
                        async () =>
                        {
                            double? hit = await client
                                .SceneItems.FindSceneItemIdAsync(
                                    sceneName,
                                    inputName,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            double? miss = await client
                                .SceneItems.FindSceneItemIdAsync(
                                    sceneName,
                                    "__not_here__",
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            return (
                                hit is not null && miss is null,
                                $"hit={hit}, miss={(miss is null ? "null" : "unexpected")}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "SetSceneItemEnabledAsync (toggle)",
                        async () =>
                        {
                            bool off = await client
                                .SceneItems.SetSceneItemEnabledAsync(
                                    sceneName,
                                    inputName,
                                    false,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            bool toggled = await client
                                .SceneItems.SetSceneItemEnabledAsync(
                                    sceneName,
                                    inputName,
                                    null,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            return (!off && toggled, $"set false -> {off}, toggled -> {toggled}");
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "SetInputVolumeDbAsync",
                        async () =>
                        {
                            await client
                                .Inputs.SetInputVolumeDbAsync(inputName, -6, cancellationToken)
                                .ConfigureAwait(false);
                            GetInputVolumeResponseData? volume = await client
                                .Inputs.GetInputVolumeAsync(
                                    new GetInputVolumeRequestData(inputName: inputName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            double db = volume?.InputVolumeDb ?? double.NaN;
                            return (Math.Abs(db + 6) < 0.5, $"db={db:0.##}");
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Media transport (typed enum)",
                        async () =>
                        {
                            await client
                                .MediaInputs.TriggerMediaActionAsync(
                                    inputName,
                                    MediaInputAction.Stop,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            // Read the state back, so this proves the action landed rather than only that
                            // the request was accepted.
                            GetMediaInputStatusResponseData? status = await client
                                .MediaInputs.GetMediaInputStatusAsync(
                                    new GetMediaInputStatusRequestData(inputName: inputName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            string? state = status?.MediaState;
                            bool stopped =
                                state is not null
                                    && state.Contains("STOPPED", StringComparison.Ordinal)
                                || state is not null
                                    && state.Contains("NONE", StringComparison.Ordinal);

                            return (
                                stopped,
                                $"sent {MediaInputAction.Stop.ToWireValue()}, state={state}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Event stream (await foreach)",
                        async () =>
                        {
                            using CancellationTokenSource streamCts =
                                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            streamCts.CancelAfter(TimeSpan.FromSeconds(10));

                            List<string> observed = [];
                            Task consume = Task.Run(
                                async () =>
                                {
                                    try
                                    {
                                        await foreach (
                                            CurrentProgramSceneChangedEventArgs sceneEvent in client
                                                .Scenes.CurrentProgramSceneChangedStream(
                                                    cancellationToken: streamCts.Token
                                                )
                                                .ConfigureAwait(false)
                                        )
                                        {
                                            observed.Add(
                                                sceneEvent.EventData.SceneName ?? string.Empty
                                            );
                                            if (observed.Count >= 2)
                                            {
                                                await streamCts.CancelAsync().ConfigureAwait(false);
                                            }
                                        }
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        // Expected once both switches are seen or the window elapses.
                                    }
                                },
                                CancellationToken.None
                            );

                            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                            await client
                                .Scenes.SwitchProgramSceneAsync(
                                    sceneName,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);
                            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
                            if (!string.IsNullOrEmpty(originalScene))
                            {
                                await client
                                    .Scenes.SwitchProgramSceneAsync(
                                        originalScene,
                                        cancellationToken: cancellationToken
                                    )
                                    .ConfigureAwait(false);
                            }

                            await consume.ConfigureAwait(false);
                            return (
                                observed.Count >= 2,
                                $"observed {observed.Count}: {string.Join(" -> ", observed)}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "WaitForEventAsync (timeout overload)",
                        async () =>
                        {
                            Task<SceneItemEnableStateChangedEventArgs> wait =
                                client.WaitForEventAsync<SceneItemEnableStateChangedEventArgs>(
                                    TimeSpan.FromSeconds(5),
                                    cancellationToken
                                );
                            _ = await client
                                .SceneItems.SetSceneItemEnabledAsync(
                                    sceneName,
                                    inputName,
                                    false,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            try
                            {
                                SceneItemEnableStateChangedEventArgs observed =
                                    await wait.ConfigureAwait(false);
                                return (true, $"enabled={observed.EventData.SceneItemEnabled}");
                            }
                            catch (TimeoutException)
                            {
                                return (false, "timed out");
                            }
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Typed batch builder",
                        async () =>
                        {
                            ObsBatchBuilder batch = new();
                            BatchRef<GetVersionResponseData> versionRef =
                                batch.General.GetVersion();
                            _ = batch.General.Sleep(new SleepRequestData(sleepMillis: 25));
                            BatchRef<GetSceneListResponseData> scenesRef =
                                batch.Scenes.GetSceneList(new GetSceneListRequestData());
                            BatchRef<GetStatsResponseData> statsRef = batch.General.GetStats();

                            BatchResults typedBatch = await client
                                .CallBatchAsync(
                                    batch,
                                    executionType: RequestBatchExecutionType.SerialRealtime,
                                    haltOnFailure: false,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            // Each result is read through the reference its request handed back, so neither
                            // the position nor the response type is restated here.
                            GetVersionResponseData version = typedBatch.Get(versionRef);
                            GetSceneListResponseData scenes = typedBatch.Get(scenesRef);
                            GetStatsResponseData stats = typedBatch.Get(statsRef);

                            return (
                                typedBatch.Count == 4
                                    && typedBatch.AllSucceeded()
                                    && version.ObsVersion is not null
                                    && scenes.Scenes is not null,
                                $"{typedBatch.Count} result(s), OBS {version.ObsVersion}, {scenes.Scenes?.Count} scene(s), {stats.ActiveFps:0} fps"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Batch order and duplicates",
                        async () =>
                        {
                            // Repeats one request type with different payloads and interleaves others, so a
                            // result can only be matched to its request by position.
                            GetSceneListResponseData? allScenes = await client
                                .Scenes.GetSceneListAsync(
                                    new GetSceneListRequestData(),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            string otherScene = allScenes!
                                .Scenes!.Select(scene => scene.SceneName!)
                                .First(name =>
                                    !string.Equals(name, sceneName, StringComparison.Ordinal)
                                );

                            // The same request type appears three times with two different payloads, so a
                            // result can only be matched to its request through the reference it returned.
                            ObsBatchBuilder mixedBatch = new();
                            BatchRef<GetSceneItemListResponseData> firstRef =
                                mixedBatch.SceneItems.GetSceneItemList(
                                    new GetSceneItemListRequestData(sceneName: sceneName)
                                );
                            BatchRef<GetVersionResponseData> versionRef =
                                mixedBatch.General.GetVersion();
                            BatchRef<GetSceneItemListResponseData> secondRef =
                                mixedBatch.SceneItems.GetSceneItemList(
                                    new GetSceneItemListRequestData(sceneName: otherScene)
                                );
                            BatchRef<GetSceneItemListResponseData> thirdRef =
                                mixedBatch.SceneItems.GetSceneItemList(
                                    new GetSceneItemListRequestData(sceneName: sceneName)
                                );
                            _ = mixedBatch.General.GetStats();

                            BatchResults mixed = await client
                                .CallBatchAsync(
                                    mixedBatch,
                                    executionType: RequestBatchExecutionType.SerialRealtime,
                                    haltOnFailure: false,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            if (mixed.Count != 5 || !mixed.AllSucceeded())
                            {
                                return (
                                    false,
                                    $"expected 5 successes, got {mixed.Count} with {mixed.GetFailures().Count()} failure(s)"
                                );
                            }

                            GetSceneItemListResponseData first = mixed.Get(firstRef);
                            GetVersionResponseData version = mixed.Get(versionRef);
                            GetSceneItemListResponseData second = mixed.Get(secondRef);
                            GetSceneItemListResponseData third = mixed.Get(thirdRef);

                            // The two lookups of the same scene must agree, and differ from the other scene.
                            int firstCount = first.SceneItems?.Count ?? -1;
                            int secondCount = second.SceneItems?.Count ?? -1;
                            int thirdCount = third.SceneItems?.Count ?? -1;
                            bool repeatsAgree = firstCount == thirdCount;
                            bool distinguishable =
                                firstCount != secondCount
                                || !string.Equals(sceneName, otherScene, StringComparison.Ordinal);

                            return (
                                repeatsAgree && distinguishable && version.ObsVersion is not null,
                                $"[{firstCount}, v{version.ObsVersion}, {secondCount}, {thirdCount}] repeats agree = {repeatsAgree}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Batch partial failure",
                        async () =>
                        {
                            // haltOnFailure false, so the good requests either side of a bad one still run.
                            ObsBatchBuilder partialBatch = new();
                            BatchRef<GetVersionResponseData> goodRef =
                                partialBatch.General.GetVersion();
                            BatchRef<GetSceneItemListResponseData> badRef =
                                partialBatch.SceneItems.GetSceneItemList(
                                    new GetSceneItemListRequestData(sceneName: "__no_such_scene__")
                                );
                            _ = partialBatch.General.GetStats();

                            BatchResults partial = await client
                                .CallBatchAsync(
                                    partialBatch,
                                    executionType: RequestBatchExecutionType.SerialRealtime,
                                    haltOnFailure: false,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            RequestResponsePayload<object>[] failures = [.. partial.GetFailures()];
                            if (partial.Count != 3 || failures.Length != 1)
                            {
                                return (
                                    false,
                                    $"{partial.Count} result(s), {failures.Length} failure(s)"
                                );
                            }

                            // GetRequiredData surfaces the OBS status rather than a null payload.
                            string caught;
                            try
                            {
                                _ = partial.Get(badRef);
                                caught = "no exception";
                            }
                            catch (ObsWebSocketRequestException ex)
                            {
                                caught = $"code {(int?)ex.StatusCode}";
                            }

                            // TryGet reports the failure without throwing.
                            bool tryGetReportedFailure = !partial.TryGet(badRef, out _);
                            bool neighboursOk =
                                tryGetReportedFailure
                                && partial.Get(goodRef).ObsVersion is not null;

                            return (
                                neighboursOk
                                    && caught.StartsWith("code ", StringComparison.Ordinal),
                                $"1 failed ({caught}), neighbours ran"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Event stream buffering",
                        async () =>
                        {
                            // A stream keeps the newest events when a consumer falls behind rather than
                            // stalling the receive loop, so a small capacity drops the oldest.
                            using CancellationTokenSource cts =
                                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            cts.CancelAfter(TimeSpan.FromSeconds(10));

                            IAsyncEnumerator<SceneItemEnableStateChangedEventArgs> enumerator =
                                client
                                    .SceneItems.SceneItemEnableStateChangedStream(
                                        capacity: 2,
                                        cancellationToken: cts.Token
                                    )
                                    .GetAsyncEnumerator(cts.Token);

                            try
                            {
                                ValueTask<bool> pending = enumerator.MoveNextAsync();

                                // Toggle more times than the buffer holds.
                                for (int i = 0; i < 4; i++)
                                {
                                    _ = await client
                                        .SceneItems.SetSceneItemEnabledAsync(
                                            sceneName,
                                            inputName,
                                            i % 2 == 0,
                                            cancellationToken
                                        )
                                        .ConfigureAwait(false);
                                }

                                bool first = await pending.ConfigureAwait(false);
                                return (
                                    first,
                                    first
                                        ? "buffered and delivered under capacity pressure"
                                        : "no event"
                                );
                            }
                            finally
                            {
                                await enumerator.DisposeAsync().ConfigureAwait(false);
                            }
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Single-request values (non-batch)",
                        async () =>
                        {
                            // The same response types that come back empty inside a batch, fetched singly.
                            GetSceneItemListResponseData? items = await client
                                .SceneItems.GetSceneItemListAsync(
                                    new GetSceneItemListRequestData(sceneName: sceneName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            GetStatsResponseData? st = await client
                                .General.GetStatsAsync(cancellationToken)
                                .ConfigureAwait(false);
                            GetVersionResponseData? ver = await client
                                .General.GetVersionAsync(cancellationToken)
                                .ConfigureAwait(false);

                            int itemCount = items?.SceneItems?.Count ?? -1;
                            double fps = st?.ActiveFps ?? 0;

                            return (
                                itemCount >= 0 && fps > 0 && ver?.ObsVersion is not null,
                                $"items={itemCount}, {fps:0} fps, v={ver?.ObsVersion}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Batch parallel execution",
                        async () =>
                        {
                            // OBS pairs each result with another request's response data under parallel
                            // execution, so reading by reference must refuse rather than return the wrong
                            // request's payload.
                            ObsBatchBuilder par = new();
                            BatchRef<GetVersionResponseData> v = par.General.GetVersion();
                            _ = par.SceneItems.GetSceneItemList(
                                new GetSceneItemListRequestData(sceneName: sceneName)
                            );

                            BatchResults r = await client
                                .CallBatchAsync(
                                    par,
                                    executionType: RequestBatchExecutionType.Parallel,
                                    haltOnFailure: false,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            string guarded;
                            try
                            {
                                _ = r.Get(v);
                                guarded = "returned data";
                            }
                            catch (ObsWebSocketException ex)
                            {
                                guarded = ex.Message.Contains("Parallel", StringComparison.Ordinal)
                                    ? "refused"
                                    : "threw: " + ex.Message;
                            }

                            return (
                                r.Count == 2
                                    && guarded == "refused"
                                    && !r.TryGet(v, out GetVersionResponseData? _),
                                $"{r.Count} raw result(s), reference {guarded}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Batch halt on failure",
                        async () =>
                        {
                            ObsBatchBuilder halt = new();
                            BatchRef<GetVersionResponseData> first = halt.General.GetVersion();
                            BatchRef<GetSceneItemListResponseData> bad =
                                halt.SceneItems.GetSceneItemList(
                                    new GetSceneItemListRequestData(sceneName: "__no_such_scene__")
                                );
                            BatchRef<GetStatsResponseData> never = halt.General.GetStats();

                            BatchResults r = await client
                                .CallBatchAsync(
                                    halt,
                                    executionType: RequestBatchExecutionType.SerialRealtime,
                                    haltOnFailure: true,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            bool firstOk = r.Get(first).ObsVersion is not null;
                            bool badRejected = !r.TryGet(bad, out GetSceneItemListResponseData? _);

                            // The third request never ran, so reading it explains itself rather than
                            // returning someone else's result.
                            string neverMsg;
                            try
                            {
                                _ = r.Get(never);
                                neverMsg = "returned a result";
                            }
                            catch (ObsWebSocketException ex)
                            {
                                neverMsg = ex.Message.Contains(
                                    "never ran",
                                    StringComparison.Ordinal
                                )
                                    ? "explained"
                                    : "threw: " + ex.Message;
                            }

                            return (
                                firstOk && badRejected && neverMsg == "explained",
                                $"{r.Count} result(s), unrun request {neverMsg}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "SetInputVolumeMulAsync",
                        async () =>
                        {
                            GetInputVolumeResponseData? before = await client
                                .Inputs.GetInputVolumeAsync(
                                    new GetInputVolumeRequestData(inputName: inputName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            double original = before!.InputVolumeMul;

                            await client
                                .Inputs.SetInputVolumeMulAsync(inputName, 0.5, cancellationToken)
                                .ConfigureAwait(false);
                            GetInputVolumeResponseData? after = await client
                                .Inputs.GetInputVolumeAsync(
                                    new GetInputVolumeRequestData(inputName: inputName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            double mul = after!.InputVolumeMul;

                            await client
                                .Inputs.SetInputVolumeMulAsync(
                                    inputName,
                                    original,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            return (Math.Abs(mul - 0.5) < 0.01, $"mul={mul:0.###}");
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "SwitchProgramSceneAsync",
                        async () =>
                        {
                            await client
                                .Scenes.SwitchProgramSceneAsync(
                                    sceneName,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);
                            GetSceneListResponseData? mid = await client
                                .Scenes.GetSceneListAsync(
                                    new GetSceneListRequestData(),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            bool switched = string.Equals(
                                mid?.CurrentProgramSceneName,
                                sceneName,
                                StringComparison.Ordinal
                            );

                            await client
                                .Scenes.SwitchProgramSceneAsync(
                                    originalScene,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);
                            GetSceneListResponseData? restored = await client
                                .Scenes.GetSceneListAsync(
                                    new GetSceneListRequestData(),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            return (
                                switched
                                    && string.Equals(
                                        restored?.CurrentProgramSceneName,
                                        originalScene,
                                        StringComparison.Ordinal
                                    ),
                                $"switched={switched}, restored to '{restored?.CurrentProgramSceneName}'"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "FindSceneItemIdAsync",
                        async () =>
                        {
                            int? id = await client
                                .SceneItems.FindSceneItemIdAsync(
                                    sceneName,
                                    inputName,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            int? miss = await client
                                .SceneItems.FindSceneItemIdAsync(
                                    sceneName,
                                    "__absent__",
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            return (
                                id is not null && miss is null,
                                $"id={id}, miss={(miss is null ? "null" : "unexpected")}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Screenshot helpers",
                        async () =>
                        {
                            byte[]? bytes = await client
                                .Sources.GetSourceScreenshotBytesAsync(
                                    sceneName,
                                    "png",
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            string path = Path.Combine(
                                Path.GetTempPath(),
                                $"obsws_{Guid.NewGuid():N}.png"
                            );
                            try
                            {
                                await client
                                    .Sources.SaveSourceScreenshotToFileAsync(
                                        sceneName,
                                        path,
                                        "png",
                                        cancellationToken: cancellationToken
                                    )
                                    .ConfigureAwait(false);

                                // A PNG starts with the eight byte signature, so this checks real image data
                                // rather than merely that the call returned.
                                byte[] written = await File.ReadAllBytesAsync(
                                        path,
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false);
                                bool pngOnDisk =
                                    written.Length > 8
                                    && written[0] == 0x89
                                    && written[1] == 0x50
                                    && written[2] == 0x4E
                                    && written[3] == 0x47;
                                bool pngInMemory =
                                    bytes is { Length: > 8 }
                                    && bytes[0] == 0x89
                                    && bytes[1] == 0x50
                                    && bytes[2] == 0x4E
                                    && bytes[3] == 0x47;

                                return (
                                    pngInMemory && pngOnDisk,
                                    $"{bytes?.Length ?? 0} bytes in memory, {written.Length} on disk"
                                );
                            }
                            finally
                            {
                                if (File.Exists(path))
                                {
                                    File.Delete(path);
                                }
                            }
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Ensure profile and scene collection",
                        async () =>
                        {
                            // Asking for the one already active proves the check without disrupting OBS,
                            // since switching either of these reloads the whole configuration.
                            GetProfileListResponseData? profiles = await client
                                .Config.GetProfileListAsync(cancellationToken)
                                .ConfigureAwait(false);
                            GetSceneCollectionListResponseData? collections = await client
                                .Config.GetSceneCollectionListAsync(cancellationToken)
                                .ConfigureAwait(false);

                            bool profileOk = await client
                                .Config.EnsureProfileActiveAsync(
                                    profiles!.CurrentProfileName!,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            bool collectionOk = await client
                                .Config.EnsureSceneCollectionActiveAsync(
                                    collections!.CurrentSceneCollectionName!,
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            bool absent = await client
                                .Config.EnsureProfileActiveAsync(
                                    "__no_such_profile__",
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            return (
                                profileOk && collectionOk && !absent,
                                $"profile={profiles.CurrentProfileName}, collection={collections.CurrentSceneCollectionName}, absent reported {absent}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Media transport shorthands",
                        async () =>
                        {
                            await client
                                .MediaInputs.PlayMediaAsync(inputName, cancellationToken)
                                .ConfigureAwait(false);
                            await client
                                .MediaInputs.PauseMediaAsync(inputName, cancellationToken)
                                .ConfigureAwait(false);
                            await client
                                .MediaInputs.RestartMediaAsync(inputName, cancellationToken)
                                .ConfigureAwait(false);
                            await client
                                .MediaInputs.StopMediaAsync(inputName, cancellationToken)
                                .ConfigureAwait(false);

                            GetMediaInputStatusResponseData? status = await client
                                .MediaInputs.GetMediaInputStatusAsync(
                                    new GetMediaInputStatusRequestData(inputName: inputName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            return (status is not null, $"state={status?.MediaState}");
                        }
                    )
                    .ConfigureAwait(false)
            );

            // Disabled: toggling the virtual camera takes down this OBS install. The fault is in
            // the Stream Deck plugin (streamdeckpluginobs32.dll appears at the fault address and
            // in every frame above it), not in OBS or in this library. Re-enable once that plugin
            // is removed.
            // results.Add(
            // await TrySettingsCheckAsync(
            // "Virtual camera toggle",
            // async () =>
            // {
            // bool before = await client
            // .Outputs.IsVirtualCamActiveAsync(cancellationToken)
            // .ConfigureAwait(false);

            // bool? turnedOn = await client
            // .Outputs.SetVirtualCamActiveAndWaitAsync(
            // !before,
            // cancellationToken: cancellationToken
            // )
            // .ConfigureAwait(false);
            // bool observed = await client
            // .Outputs.IsVirtualCamActiveAsync(cancellationToken)
            // .ConfigureAwait(false);

            // // Put it back the way it was found.
            // _ = await client
            // .Outputs.SetVirtualCamActiveAndWaitAsync(
            // before,
            // cancellationToken: cancellationToken
            // )
            // .ConfigureAwait(false);
            // bool restored = await client
            // .Outputs.IsVirtualCamActiveAsync(cancellationToken)
            // .ConfigureAwait(false);

            // return (
            // turnedOn == !before && observed == !before && restored == before,
            // $"{before} -> {observed} -> {restored}"
            // );
            // }
            // )
            // .ConfigureAwait(false)
            // );

            results.Add(
                await TrySettingsCheckAsync(
                        "Integer fields round trip",
                        async () =>
                        {
                            // The protocol calls every number "Number", so these fields used to
                            // arrive as double. Writing one and reading it back proves the
                            // retype survives the wire in both directions, which matters most
                            // for MessagePack, where an int and a float are different encodings.
                            int itemId =
                                await client
                                    .SceneItems.FindSceneItemIdAsync(
                                        sceneName,
                                        inputName,
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false)
                                ?? throw new InvalidOperationException(
                                    $"'{inputName}' is not in '{sceneName}'."
                                );

                            GetSceneItemIndexResponseData originalIndex = await client
                                .SceneItems.GetSceneItemIndexAsync(
                                    new GetSceneItemIndexRequestData(
                                        sceneItemId: itemId,
                                        sceneName: sceneName
                                    ),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            await client
                                .SceneItems.SetSceneItemIndexAsync(
                                    new SetSceneItemIndexRequestData(
                                        sceneItemId: itemId,
                                        sceneItemIndex: 0,
                                        sceneName: sceneName
                                    ),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            GetSceneItemIndexResponseData afterIndex = await client
                                .SceneItems.GetSceneItemIndexAsync(
                                    new GetSceneItemIndexRequestData(
                                        sceneItemId: itemId,
                                        sceneName: sceneName
                                    ),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            await client
                                .SceneItems.SetSceneItemIndexAsync(
                                    new SetSceneItemIndexRequestData(
                                        sceneItemId: itemId,
                                        sceneItemIndex: originalIndex.SceneItemIndex,
                                        sceneName: sceneName
                                    ),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            // A negative value, since OBS accepts negative sync offsets and a
                            // sign error would otherwise go unnoticed.
                            GetInputAudioSyncOffsetResponseData originalOffset = await client
                                .Inputs.GetInputAudioSyncOffsetAsync(
                                    new GetInputAudioSyncOffsetRequestData(inputName: inputName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            await client
                                .Inputs.SetInputAudioSyncOffsetAsync(
                                    new SetInputAudioSyncOffsetRequestData(
                                        inputAudioSyncOffset: -125,
                                        inputName: inputName
                                    ),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            GetInputAudioSyncOffsetResponseData afterOffset = await client
                                .Inputs.GetInputAudioSyncOffsetAsync(
                                    new GetInputAudioSyncOffsetRequestData(inputName: inputName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            await client
                                .Inputs.SetInputAudioSyncOffsetAsync(
                                    new SetInputAudioSyncOffsetRequestData(
                                        inputAudioSyncOffset: originalOffset.InputAudioSyncOffset,
                                        inputName: inputName
                                    ),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            return (
                                afterIndex.SceneItemIndex == 0
                                    && afterOffset.InputAudioSyncOffset == -125,
                                $"index {originalIndex.SceneItemIndex} -> {afterIndex.SceneItemIndex}, "
                                    + $"syncOffset {originalOffset.InputAudioSyncOffset} -> {afterOffset.InputAudioSyncOffset}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Preview scene helpers",
                        async () =>
                        {
                            // Preview only exists in Studio Mode, so turn it on for the check and
                            // put it back however it was found.
                            GetStudioModeEnabledResponseData studio = await client
                                .Ui.GetStudioModeEnabledAsync(cancellationToken)
                                .ConfigureAwait(false);
                            if (!studio.StudioModeEnabled)
                            {
                                await client
                                    .Ui.SetStudioModeEnabledAsync(new(true), cancellationToken)
                                    .ConfigureAwait(false);
                            }

                            try
                            {
                                // Every switch waits for the event confirming it. OBS points
                                // Preview at the Program scene while enabling Studio Mode, and
                                // that lands after StudioModeStateChanged, so a switch that does
                                // not wait for its own confirmation gets silently undone.
                                await client
                                    .Scenes.SwitchPreviewSceneAndWaitAsync(
                                        sceneName,
                                        cancellationToken: cancellationToken
                                    )
                                    .ConfigureAwait(false);
                                GetCurrentPreviewSceneResponseData preview = await client
                                    .Scenes.GetCurrentPreviewSceneAsync(cancellationToken)
                                    .ConfigureAwait(false);

                                // The plain overload, confirmed by waiting on the event directly.
                                Task<CurrentPreviewSceneChangedEventArgs> back =
                                    client.WaitForEventAsync<CurrentPreviewSceneChangedEventArgs>(
                                        e =>
                                            string.Equals(
                                                e.EventData.SceneName,
                                                originalScene,
                                                StringComparison.Ordinal
                                            ),
                                        TimeSpan.FromSeconds(5),
                                        cancellationToken
                                    );
                                await client
                                    .Scenes.SwitchPreviewSceneAsync(
                                        originalScene,
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false);
                                _ = await back.ConfigureAwait(false);

                                GetCurrentPreviewSceneResponseData restored = await client
                                    .Scenes.GetCurrentPreviewSceneAsync(cancellationToken)
                                    .ConfigureAwait(false);

                                bool ok =
                                    string.Equals(
                                        preview.SceneName,
                                        sceneName,
                                        StringComparison.Ordinal
                                    )
                                    && string.Equals(
                                        restored.SceneName,
                                        originalScene,
                                        StringComparison.Ordinal
                                    );

                                return (
                                    ok,
                                    $"wanted {sceneName} got {preview.SceneName}, "
                                        + $"then wanted {originalScene} got {restored.SceneName}"
                                );
                            }
                            finally
                            {
                                if (!studio.StudioModeEnabled)
                                {
                                    await client
                                        .Ui.SetStudioModeEnabledAsync(new(false), cancellationToken)
                                        .ConfigureAwait(false);
                                }
                            }
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "SourceExistsAsync",
                        async () =>
                        {
                            bool present = await client
                                .Sources.SourceExistsAsync(inputName, cancellationToken)
                                .ConfigureAwait(false);
                            bool absent = await client
                                .Sources.SourceExistsAsync("__absent__", cancellationToken)
                                .ConfigureAwait(false);

                            return (present && !absent, $"present={present}, absent={absent}");
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "SetInputMutesAsync",
                        async () =>
                        {
                            GetInputMuteResponseData before = await client
                                .Inputs.GetInputMuteAsync(
                                    new(inputName: inputName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            // One real input beside one that does not exist, so the returned
                            // results have to show a success next to a failure.
                            BatchResults muteResults = await client
                                .Inputs.SetInputMutesAsync(
                                    [(inputName, !before.InputMuted), ("__absent__", true)],
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            GetInputMuteResponseData after = await client
                                .Inputs.GetInputMuteAsync(
                                    new(inputName: inputName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            await client
                                .Inputs.SetInputMutesAsync(
                                    [(inputName, before.InputMuted)],
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            bool ok =
                                muteResults.Count == 2
                                && muteResults[0].RequestStatus.Result
                                && !muteResults[1].RequestStatus.Result
                                && after.InputMuted == !before.InputMuted;

                            return (
                                ok,
                                $"{muteResults.Count} results, muted {before.InputMuted} -> {after.InputMuted}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Transition settings read",
                        async () =>
                        {
                            GetCurrentSceneTransitionResponseData current = await client
                                .Transitions.GetCurrentSceneTransitionAsync(cancellationToken)
                                .ConfigureAwait(false);
                            // The typed Get*SettingsAsync helpers deserialize into a settings
                            // record; the generated request is the way to read the raw JSON.
                            // A transition with nothing to configure, such as Fade, legitimately
                            // reports no settings, so the name and kind are what is asserted.
                            JsonElement? settings = current.TransitionSettings;

                            return (
                                !string.IsNullOrEmpty(current.TransitionName)
                                    && !string.IsNullOrEmpty(current.TransitionKind),
                                $"{current.TransitionName} ({current.TransitionKind}), "
                                    + $"settings {(settings is null ? "none" : "present")}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Canvas screenshot helper",
                        async () =>
                        {
                            byte[]? bytes = await client
                                .Sources.GetSourceScreenshotOnCanvasBytesAsync(
                                    sceneName,
                                    "png",
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            bool png =
                                bytes is { Length: > 8 }
                                && bytes[0] == 0x89
                                && bytes[1] == 0x50
                                && bytes[2] == 0x4E
                                && bytes[3] == 0x47;

                            return (png, $"{bytes?.Length ?? 0} bytes at canvas size");
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Parallel batch, verdict without attribution",
                        async () =>
                        {
                            // A parallel batch of writes is the case Parallel is actually good
                            // for: OBS mispairs the rows, but a verdict over all of them does not
                            // depend on which row is which.
                            ObsBatchBuilder par = new();
                            _ = par.Inputs.SetInputMute(
                                new SetInputMuteRequestData(inputName: inputName, inputMuted: true)
                            );
                            _ = par.Inputs.SetInputMute(
                                new SetInputMuteRequestData(inputName: inputName, inputMuted: false)
                            );

                            BatchResults ok = await client
                                .CallBatchAsync(
                                    par,
                                    executionType: RequestBatchExecutionType.Parallel,
                                    haltOnFailure: false,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            // Same again with one request that cannot succeed, so the count of
                            // failures is checked as well as the all-succeeded verdict.
                            ObsBatchBuilder mixed = new();
                            _ = mixed.Inputs.SetInputMute(
                                new SetInputMuteRequestData(inputName: inputName, inputMuted: false)
                            );
                            _ = mixed.Inputs.SetInputMute(
                                new SetInputMuteRequestData(
                                    inputName: "__absent__",
                                    inputMuted: true
                                )
                            );

                            BatchResults partial = await client
                                .CallBatchAsync(
                                    mixed,
                                    executionType: RequestBatchExecutionType.Parallel,
                                    haltOnFailure: false,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            int failures = partial.GetFailures().Count();

                            return (
                                ok.AllSucceeded() && !partial.AllSucceeded() && failures == 1,
                                $"all-ok verdict {ok.AllSucceeded()}, mixed verdict "
                                    + $"{partial.AllSucceeded()} with {failures} failure(s)"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Concurrent requests keep their own results",
                        async () =>
                        {
                            // The answer to "how do I run things in parallel" once a parallel
                            // batch is ruled out for reads. The client multiplexes on request id.
                            Task<GetVersionResponseData> version = client.General.GetVersionAsync(
                                cancellationToken
                            );
                            Task<GetVideoSettingsResponseData> video =
                                client.Config.GetVideoSettingsAsync(cancellationToken);
                            Task<GetSceneItemListResponseData> itemsHere =
                                client.SceneItems.GetSceneItemListAsync(
                                    new GetSceneItemListRequestData(sceneName: sceneName),
                                    cancellationToken
                                );
                            Task<GetSceneItemListResponseData> itemsThere =
                                client.SceneItems.GetSceneItemListAsync(
                                    new GetSceneItemListRequestData(sceneName: originalScene),
                                    cancellationToken
                                );

                            await Task.WhenAll(version, video, itemsHere, itemsThere)
                                .ConfigureAwait(false);

                            // Each answer has to match what the same request returns on its own.
                            GetVersionResponseData serialVersion = await client
                                .General.GetVersionAsync(cancellationToken)
                                .ConfigureAwait(false);
                            GetSceneItemListResponseData serialHere = await client
                                .SceneItems.GetSceneItemListAsync(
                                    new GetSceneItemListRequestData(sceneName: sceneName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            bool ok =
                                string.Equals(
                                    version.Result.ObsVersion,
                                    serialVersion.ObsVersion,
                                    StringComparison.Ordinal
                                )
                                && video.Result.FpsNumerator > 0
                                && itemsHere.Result.SceneItems?.Count
                                    == serialHere.SceneItems?.Count;

                            return (
                                ok,
                                $"v={version.Result.ObsVersion}, fps={video.Result.FpsNumerator}, "
                                    + $"items {itemsHere.Result.SceneItems?.Count} here vs "
                                    + $"{itemsThere.Result.SceneItems?.Count} there"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Low level Add and raw results",
                        async () =>
                        {
                            // Hand rolled batch: Add covers anything the generated methods do not,
                            // and the raw payload helpers read it back.
                            ObsBatchBuilder raw = new();
                            _ = raw.Add("GetVersion");
                            _ = raw.Add("GetStats");

                            BatchResults rawResults = await client
                                .CallBatchAsync(
                                    raw,
                                    executionType: RequestBatchExecutionType.SerialRealtime,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            GetVersionResponseData? v = rawResults
                                .Raw[0]
                                .GetData<GetVersionResponseData>();

                            // And the same request without any batch at all.
                            GetVersionResponseData? direct = await client
                                .CallAsync<GetVersionResponseData>(
                                    "GetVersion",
                                    null,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            bool ok =
                                rawResults.Count == 2
                                && v is not null
                                && direct is not null
                                && string.Equals(
                                    v.ObsVersion,
                                    direct.ObsVersion,
                                    StringComparison.Ordinal
                                );

                            return (
                                ok,
                                $"raw batch {rawResults.Count}, CallAsync {direct?.ObsVersion}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Scene item list reindexed",
                        async () =>
                        {
                            // Reindexing asks OBS for the basic scene item list, a different shape
                            // from every other sceneItems array, so the event needs its own stub.
                            GetSceneItemListResponseData items = await client
                                .SceneItems.GetSceneItemListAsync(
                                    new GetSceneItemListRequestData(sceneName: sceneName),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);
                            if (items.SceneItems.Count == 0)
                            {
                                return (false, "no scene items to reindex");
                            }

                            int id = items.SceneItems[0].SceneItemId;
                            int index = items.SceneItems[0].SceneItemIndex;

                            Task<SceneItemListReindexedEventArgs> reindexed =
                                client.WaitForEventAsync<SceneItemListReindexedEventArgs>(
                                    timeout: TimeSpan.FromSeconds(5),
                                    cancellationToken: cancellationToken
                                );

                            await client
                                .SceneItems.SetSceneItemIndexAsync(
                                    new SetSceneItemIndexRequestData(
                                        sceneItemId: id,
                                        sceneItemIndex: index,
                                        sceneName: sceneName
                                    ),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            SceneItemListReindexedEventArgs args = await reindexed.ConfigureAwait(
                                false
                            );

                            SceneItemOrderStub? moved = args.EventData.SceneItems.Find(i =>
                                i.SceneItemId == id
                            );

                            return (
                                moved is not null
                                    && string.Equals(
                                        args.EventData.SceneName,
                                        sceneName,
                                        StringComparison.Ordinal
                                    ),
                                $"{args.EventData.SceneItems.Count} item(s) reindexed, "
                                    + $"item {id} at index {moved?.SceneItemIndex}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Input volume meters",
                        async () =>
                        {
                            // High rate event with its own stub. It was read as an InputStub and
                            // failed on the kind fields it never sends, so it never fired at all.
                            EventSubscription? before = client.CurrentEventSubscriptions;
                            await client
                                .ReidentifyAsync(
                                    (uint)(
                                        (before ?? EventSubscription.All)
                                        | EventSubscription.InputVolumeMeters
                                    ),
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            try
                            {
                                InputVolumeMetersEventArgs meters = await client
                                    .WaitForEventAsync<InputVolumeMetersEventArgs>(
                                        timeout: TimeSpan.FromSeconds(5),
                                        cancellationToken: cancellationToken
                                    )
                                    .ConfigureAwait(false);

                                InputVolumeMeterStub? first =
                                    meters.EventData.Inputs.Count > 0
                                        ? meters.EventData.Inputs[0]
                                        : null;

                                // An input with no audio channels reports an empty level list, so
                                // the levels are checked where they exist rather than required.
                                bool levelsWellFormed = meters.EventData.Inputs.TrueForAll(i =>
                                    i.InputLevelsMul.TrueForAll(channel => channel.Count == 3)
                                );
                                int channels = meters.EventData.Inputs.Sum(i =>
                                    i.InputLevelsMul.Count
                                );

                                bool ok =
                                    first is not null
                                    && !string.IsNullOrEmpty(first.InputName)
                                    && Guid.TryParse(first.InputUuid, out _)
                                    && levelsWellFormed;

                                return (
                                    ok,
                                    $"{meters.EventData.Inputs.Count} input(s), first '{first?.InputName}', "
                                        + $"{channels} channel(s) total, three levels each = "
                                        + $"{levelsWellFormed}"
                                );
                            }
                            finally
                            {
                                if (before is not null)
                                {
                                    await client
                                        .ReidentifyAsync(
                                            (uint)before.Value,
                                            cancellationToken: CancellationToken.None
                                        )
                                        .ConfigureAwait(false);
                                }
                            }
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Canvases category",
                        async () =>
                        {
                            // The only request in its category. Its array carries no item type in
                            // the protocol definition, so the stub is taken from the request
                            // handler and has to be checked against a real OBS on both transports.
                            GetCanvasListResponseData canvases = await client
                                .Canvases.GetCanvasListAsync(cancellationToken)
                                .ConfigureAwait(false);

                            CanvasStub? main = canvases.Canvases.Find(c => c.CanvasFlags.Main);
                            bool ok =
                                canvases.Canvases.Count > 0
                                && main is not null
                                && !string.IsNullOrEmpty(main.CanvasName)
                                && Guid.TryParse(main.CanvasUuid, out _)
                                && main.CanvasVideoSettings.BaseWidth > 0
                                && main.CanvasVideoSettings.BaseHeight > 0
                                && main.CanvasVideoSettings.FpsNumerator > 0;

                            return (
                                ok,
                                $"{canvases.Canvases.Count} canvas(es), main '{main?.CanvasName}' "
                                    + $"{main?.CanvasVideoSettings.BaseWidth}x{main?.CanvasVideoSettings.BaseHeight} "
                                    + $"@ {main?.CanvasVideoSettings.FpsNumerator}/{main?.CanvasVideoSettings.FpsDenominator}, "
                                    + $"flags MAIN={main?.CanvasFlags.Main} MIX_AUDIO={main?.CanvasFlags.MixAudio}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Request with neither payload",
                        async () =>
                        {
                            // The one generated shape nothing else exercises: no request data and
                            // no response. Pointing Preview at the scene already in Program makes
                            // the transition a no-op, so the check is safe to run.
                            GetStudioModeEnabledResponseData studio = await client
                                .Ui.GetStudioModeEnabledAsync(cancellationToken)
                                .ConfigureAwait(false);
                            if (!studio.StudioModeEnabled)
                            {
                                await client
                                    .Ui.SetStudioModeEnabledAsync(new(true), cancellationToken)
                                    .ConfigureAwait(false);
                            }

                            try
                            {
                                GetSceneListResponseData before = await client
                                    .Scenes.GetSceneListAsync(new(), cancellationToken)
                                    .ConfigureAwait(false);
                                string program = before.CurrentProgramSceneName!;

                                // Plain switch, not the waiting variant: OBS raises no
                                // CurrentPreviewSceneChanged when the preview is already that
                                // scene, which enabling Studio Mode has just made it.
                                await client
                                    .Scenes.SwitchPreviewSceneAsync(program, cancellationToken)
                                    .ConfigureAwait(false);

                                await client
                                    .Transitions.TriggerStudioModeTransitionAsync(cancellationToken)
                                    .ConfigureAwait(false);

                                GetSceneListResponseData after = await client
                                    .Scenes.GetSceneListAsync(new(), cancellationToken)
                                    .ConfigureAwait(false);

                                return (
                                    string.Equals(
                                        after.CurrentProgramSceneName,
                                        program,
                                        StringComparison.Ordinal
                                    ),
                                    $"transitioned, program still '{after.CurrentProgramSceneName}'"
                                );
                            }
                            finally
                            {
                                if (!studio.StudioModeEnabled)
                                {
                                    await client
                                        .Ui.SetStudioModeEnabledAsync(new(false), cancellationToken)
                                        .ConfigureAwait(false);
                                }
                            }
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Nested request object",
                        async () =>
                        {
                            // keyModifiers is the one generated nested record, a distinct shape
                            // from the flat request payloads. F13 is not bound by default, so the
                            // press does nothing.
                            await client
                                .General.TriggerHotkeyByKeySequenceAsync(
                                    new TriggerHotkeyByKeySequenceRequestData(
                                        keyId: "OBS_KEY_F13",
                                        keyModifiers: new TriggerHotkeyByKeySequenceRequestData_KeyModifiers(
                                            shift: false,
                                            control: true,
                                            alt: false,
                                            command: false
                                        )
                                    ),
                                    cancellationToken
                                )
                                .ConfigureAwait(false);

                            return (true, "nested keyModifiers accepted");
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Remaining stub types",
                        async () =>
                        {
                            // Monitor, output and transition stubs are generated the same way as
                            // the scene and input ones, so one read each covers the shape.
                            GetMonitorListResponseData monitors = await client
                                .Ui.GetMonitorListAsync(cancellationToken)
                                .ConfigureAwait(false);
                            GetSceneTransitionListResponseData transitions = await client
                                .Transitions.GetSceneTransitionListAsync(cancellationToken)
                                .ConfigureAwait(false);
                            GetOutputListResponseData outputs = await client
                                .Outputs.GetOutputListAsync(cancellationToken)
                                .ConfigureAwait(false);

                            bool ok =
                                monitors.Monitors.Count > 0
                                && transitions.Transitions.Count > 0
                                && outputs.Outputs.Count > 0
                                && monitors.Monitors[0].MonitorWidth > 0
                                && !string.IsNullOrEmpty(transitions.Transitions[0].TransitionName)
                                && !string.IsNullOrEmpty(outputs.Outputs[0].OutputName);

                            return (
                                ok,
                                $"{monitors.Monitors.Count} monitor(s) first {monitors.Monitors[0].MonitorWidth}px, "
                                    + $"{transitions.Transitions.Count} transition(s), {outputs.Outputs.Count} output(s)"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Classic handler and subscriptions",
                        async () =>
                        {
                            // The stream path is covered above; this is the += path, plus the
                            // negotiated subscription flags the client reports.
                            TaskCompletionSource seen = new(
                                TaskCreationOptions.RunContinuationsAsynchronously
                            );
                            string? observed = null;
                            void Handler(object? sender, SceneCreatedEventArgs e)
                            {
                                observed = e.EventData.SceneName;
                                _ = seen.TrySetResult();
                            }

                            client.Scenes.SceneCreated += Handler;
                            string probe = $"__obsws_handler_{Guid.NewGuid():N}"[..24];
                            try
                            {
                                await client
                                    .Scenes.CreateSceneAsync(
                                        new CreateSceneRequestData(sceneName: probe),
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false);
                                await seen
                                    .Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            finally
                            {
                                client.Scenes.SceneCreated -= Handler;
                                await client
                                    .Scenes.RemoveSceneAsync(
                                        new RemoveSceneRequestData(sceneName: probe),
                                        CancellationToken.None
                                    )
                                    .ConfigureAwait(false);
                            }

                            EventSubscription? subs = client.CurrentEventSubscriptions;

                            return (
                                string.Equals(observed, probe, StringComparison.Ordinal)
                                    && subs is not null
                                    && subs.Value.HasFlag(EventSubscription.Scenes),
                                $"handler saw '{observed}', subscriptions {subs}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Every way of sending a request",
                        async () =>
                        {
                            // One request, reached six ways, so no path is left unexercised.
                            // 1. The generated request on its category group.
                            GetVersionResponseData viaGroup = await client
                                .General.GetVersionAsync(cancellationToken)
                                .ConfigureAwait(false);

                            // 2. The low level typed call, for a reference type response.
                            GetVersionResponseData? viaCall = await client
                                .CallAsync<GetVersionResponseData>(
                                    "GetVersion",
                                    null,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);

                            // 3. The low level untyped call, for a value type response.
                            JsonElement? viaValue = await client
                                .CallAsyncValue<JsonElement>(
                                    "GetVersion",
                                    null,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);
                            string? viaRawJson = viaValue?.GetProperty("obsVersion").GetString();

                            // 4. A hand built JsonElement as the request payload.
                            using JsonDocument requestBody = JsonDocument.Parse(
                                $$"""{"sceneName":"{{sceneName}}"}"""
                            );
                            JsonElement? viaJsonBody = await client
                                .CallAsyncValue<JsonElement>(
                                    "GetSceneItemList",
                                    requestBody.RootElement,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);
                            int itemsViaJsonBody =
                                viaJsonBody?.GetProperty("sceneItems").GetArrayLength() ?? -1;

                            // 5. The typed batch builder.
                            ObsBatchBuilder builder = new();
                            BatchRef<GetVersionResponseData> batched = builder.General.GetVersion();
                            BatchResults built = await client
                                .CallBatchAsync(
                                    builder,
                                    executionType: RequestBatchExecutionType.SerialRealtime,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);
                            string viaBatch = built.Get(batched).ObsVersion;

                            // 6. A hand rolled batch item, with a JsonElement payload.
                            using JsonDocument batchBody = JsonDocument.Parse(
                                $$"""{"sceneName":"{{sceneName}}"}"""
                            );
                            List<RequestResponsePayload<object>> viaRawBatch = await client
                                .CallBatchAsync(
                                    [
                                        new BatchRequestItem("GetVersion", null),
                                        new BatchRequestItem(
                                            "GetSceneItemList",
                                            batchBody.RootElement
                                        ),
                                    ],
                                    executionType: RequestBatchExecutionType.SerialRealtime,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);
                            string? viaRawBatchVersion = viaRawBatch[0]
                                .GetData<GetVersionResponseData>()
                                ?.ObsVersion;

                            // 7. A consumer's own JsonSerializerContext, so a type this library
                            // has never heard of is sent without hand building a JsonElement.
                            JsonElement? viaConsumerContext = await client
                                .CallAsyncValue<JsonElement>(
                                    "GetSceneItemList",
                                    new ConsumerSceneRequest(sceneName),
                                    ExampleRequestContext.Default.ConsumerSceneRequest,
                                    cancellationToken: cancellationToken
                                )
                                .ConfigureAwait(false);
                            int itemsViaContext =
                                viaConsumerContext?.GetProperty("sceneItems").GetArrayLength()
                                ?? -1;

                            // And the one shape that is not supported, asserted as unsupported:
                            // an anonymous object has no metadata in the serializer context.
                            string anonymous;
                            try
                            {
                                _ = await client
                                    .CallAsyncValue<JsonElement>(
                                        "GetSceneItemList",
                                        new { sceneName },
                                        cancellationToken: cancellationToken
                                    )
                                    .ConfigureAwait(false);
                                anonymous = "unexpectedly accepted";
                            }
                            catch (ObsWebSocketSerializationException)
                            {
                                anonymous = "refused";
                            }

                            string expected = viaGroup.ObsVersion;
                            bool allAgree =
                                viaCall?.ObsVersion == expected
                                && viaRawJson == expected
                                && viaBatch == expected
                                && viaRawBatchVersion == expected
                                && itemsViaJsonBody >= 0
                                && itemsViaContext == itemsViaJsonBody
                                && anonymous == "refused";

                            return (
                                allAgree,
                                $"seven paths agree on {expected}, JsonElement body and consumer "
                                    + $"context both read {itemsViaJsonBody} item(s), anonymous "
                                    + $"object {anonymous}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Typed exception on a rejected request",
                        async () =>
                        {
                            try
                            {
                                _ = await client
                                    .SceneItems.GetSceneItemListAsync(
                                        new GetSceneItemListRequestData(
                                            sceneName: "__no_such_scene__"
                                        ),
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false);
                                return (false, "no exception");
                            }
                            catch (ObsWebSocketRequestException ex)
                            {
                                return (
                                    ex.StatusCode == RequestStatusCode.ResourceNotFound
                                        && ex.RequestType == "GetSceneItemList",
                                    $"{ex.RequestType} code {(int?)ex.StatusCode}"
                                );
                            }
                        }
                    )
                    .ConfigureAwait(false)
            );

            results.Add(
                await TrySettingsCheckAsync(
                        "Output state helpers",
                        async () =>
                        {
                            bool recording = await client
                                .Record.IsRecordActiveAsync(cancellationToken)
                                .ConfigureAwait(false);
                            bool streaming = await client
                                .Stream.IsStreamActiveAsync(cancellationToken)
                                .ConfigureAwait(false);
                            bool virtualCam = await client
                                .Outputs.IsVirtualCamActiveAsync(cancellationToken)
                                .ConfigureAwait(false);

                            // Each helper has to agree with the request it wraps.
                            GetRecordStatusResponseData? recordStatus = await client
                                .Record.GetRecordStatusAsync(cancellationToken)
                                .ConfigureAwait(false);
                            GetStreamStatusResponseData? streamStatus = await client
                                .Stream.GetStreamStatusAsync(cancellationToken)
                                .ConfigureAwait(false);
                            GetVirtualCamStatusResponseData? camStatus = await client
                                .Outputs.GetVirtualCamStatusAsync(cancellationToken)
                                .ConfigureAwait(false);

                            bool agrees =
                                recording == recordStatus?.OutputActive
                                && streaming == streamStatus?.OutputActive
                                && virtualCam == camStatus?.OutputActive;

                            return (
                                agrees,
                                $"record={recording}, stream={streaming}, virtualCam={virtualCam}, agrees={agrees}"
                            );
                        }
                    )
                    .ConfigureAwait(false)
            );
        }
        finally
        {
            // Always put OBS back the way it was found.
            try
            {
                if (!string.IsNullOrEmpty(originalScene))
                {
                    await client
                        .Scenes.SwitchProgramSceneAsync(
                            originalScene,
                            cancellationToken: CancellationToken.None
                        )
                        .ConfigureAwait(false);
                }

                if (inputCreated)
                {
                    await client
                        .Inputs.RemoveInputAsync(
                            new RemoveInputRequestData(inputName: inputName),
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
                }

                if (sceneCreated)
                {
                    await client
                        .Scenes.RemoveSceneAsync(
                            new RemoveSceneRequestData(sceneName: sceneName),
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
                }
            }
            catch (ObsWebSocketException ex)
            {
                results.Add(("Cleanup", false, ex.Message));
            }
        }

        return results;
    }

    private static async Task<(string Label, bool Pass, string Detail)> TrySettingsCheckAsync(
        string label,
        Func<Task<(bool Pass, string Detail)>> action
    )
    {
        try
        {
            (bool pass, string detail) = await action().ConfigureAwait(false);
            return (label, pass, detail);
        }
        catch (Exception ex)
        {
            string msg = ex.Message.Length > 100 ? ex.Message[..100] : ex.Message;
            return (label, false, msg);
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
    private async Task<int> GetSceneItemIdAsync(
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

        // Step 4: Prompt — create new source or update an existing browser source
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

        int sceneItemId;

        // Step 8: Create new input or update existing source settings
        if (isNewSource)
        {
            UiInfo($"Creating browser source '{sourceName}' in scene '{selectedScene}'...");

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

            sceneItemId = createResult.SceneItemId;
            UiSuccess($"Created '{sourceName}' (scene item ID: {sceneItemId}).");
        }
        else
        {
            UiInfo($"Updating browser source '{sourceName}' settings...");

            // overlay: false — reset to defaults then apply all new settings cleanly
            await _obsClient.Inputs.SetInputSettingsAsync(
                inputName: sourceName,
                settings: browserSettings,
                overlay: false,
                cancellationToken: cancellationToken
            );

            sceneItemId = await GetSceneItemIdAsync(selectedScene, sourceName, cancellationToken);
            UiSuccess($"Updated '{sourceName}' (scene item ID: {sceneItemId}).");
        }

        // Step 9: Set Blend Mode to Normal (explicit, even though it is the default)
        await _obsClient.SceneItems.SetSceneItemBlendModeAsync(
            new SetSceneItemBlendModeRequestData(
                sceneItemId: sceneItemId,
                sceneItemBlendMode: "OBS_BLEND_NORMAL",
                sceneName: selectedScene
            ),
            cancellationToken: cancellationToken
        );

        // The obs-websocket v5 protocol does not expose SetSceneItemPrivateSettings,
        // so Blending Method (SRGB Off) cannot be set programmatically via this API.
        AnsiConsole.MarkupLine(
            "[yellow]Action required:[/] Set [bold]Blending Method[/] to [bold]sRGB Off[/] manually in OBS."
        );
        AnsiConsole.MarkupLine(
            "[grey]  Right-click the scene item → Blending → Method → sRGB Off[/]"
        );

        RenderKeyValueTable(
            $"Browser Source — {(isNewSource ? "Created" : "Updated")}",
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
                ("Blending Method", "sRGB Off — set manually in OBS (not exposed by WebSocket v5)"),
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
            Markup.Escape("mute [input name]"),
            Markup.Escape("Toggle mute for audio input")
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
            Markup.Escape("List filters for source")
        );
        _ = commandTable.AddRow(
            Markup.Escape("toggle-filter [source] [filter]"),
            Markup.Escape("Toggle filter enabled state")
        );
        _ = commandTable.AddRow(
            Markup.Escape("media [input] [action]"),
            Markup.Escape("Media transport via the typed MediaInputAction enum")
        );
        _ = commandTable.AddRow(
            Markup.Escape("watch [seconds]"),
            Markup.Escape("Stream scene changes with await foreach (default 15s)")
        );
        _ = commandTable.AddRow(
            Markup.Escape("batch-example"),
            Markup.Escape("Run sample batch request sequence via the typed builder")
        );
        _ = commandTable.AddRow(
            Markup.Escape("run-transport-tests"),
            Markup.Escape(
                "Run validation cycle for the configured transport (version, scenes, inputs, filters, custom event, batch, settings modes 1/2/3)"
            )
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

/// <summary>
/// A request payload this library has no metadata for, sent with the consumer's own context.
/// </summary>
/// <param name="SceneName">The scene to list items for.</param>
internal sealed record ConsumerSceneRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("sceneName")] string SceneName
);

/// <summary>
/// The consumer side serializer context, the AOT safe way to describe a payload the library does
/// not model.
/// </summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(ConsumerSceneRequest))]
internal sealed partial class ExampleRequestContext
    : System.Text.Json.Serialization.JsonSerializerContext;
