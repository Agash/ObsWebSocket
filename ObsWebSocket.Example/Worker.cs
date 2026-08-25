using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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
                _logger.LogInformation(
                    "Running startup command: {Command}",
                    startupCommand
                );
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
                    _logger.LogInformation(
                        "Running startup command: {Command}",
                        startupCommand
                    );
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
                GetVersionResponseData? version = await _obsClient.GetVersionAsync(
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
                    await _obsClient.GetCurrentProgramSceneAsync(
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
                ToggleInputMuteResponseData? muteState = await _obsClient.ToggleInputMuteAsync(
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
                    UiWarn(
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

                    // Uses SetInputTextAsync helper which serializes TextGdiPlusInputSettings internally.
                    await _obsClient.SetInputTextAsync(inputForSetText, newText, cancellationToken);
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
                    await _obsClient.GetSourceFilterListAsync(
                        new GetSourceFilterListRequestData(sourceName: sourceForFilters),
                        cancellationToken: cancellationToken
                    );
                if (filterList?.Filters is not null && filterList.Filters.Count > 0)
                {
                    Table table = new() { Title = new TableTitle($"Filters for '{sourceForFilters}'") };
                    _ = table.AddColumn("Index");
                    _ = table.AddColumn("Name");
                    _ = table.AddColumn("Kind");
                    _ = table.AddColumn("Enabled");
                    foreach (Core.Protocol.Common.FilterStub filterElement in filterList.Filters)
                    {
                        string filterIndex = filterElement.FilterIndex?.ToString() ?? "N/A";
                        string filterName = Markup.Escape(filterElement.FilterName ?? "N/A") ?? "N/A";
                        string filterKind = Markup.Escape(filterElement.FilterKind ?? "N/A") ?? "N/A";
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
                    UiWarn(
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

                UiSuccess(
                    $"Filter '{filterToToggle}' on '{sourceForToggle}' toggled to {(newState ? "ENABLED" : "DISABLED")}"
                );
                return false;

            case "watch":
            {
                // Streams are the ergonomic way to observe events: subscribe for the lifetime
                // of the loop, no handler bookkeeping, and cancellation ends it cleanly. The
                // classic events on the client are untouched and still work alongside this.
                int seconds = args.Length > 0 && int.TryParse(args[0], out int parsed) ? parsed : 15;
                UiInfo($"Watching scene changes for {seconds}s. Switch scenes in OBS.");

                using CancellationTokenSource watchCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );
                watchCts.CancelAfter(TimeSpan.FromSeconds(seconds));

                try
                {
                    await foreach (
                        CurrentProgramSceneChangedEventArgs sceneEvent in _obsClient.CurrentProgramSceneChangedStream(
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
                List<RequestResponsePayload<object>> batchResults = await _obsClient.CallBatchAsync(
                    batch =>
                        batch
                            .GetVersion()
                            .GetCurrentProgramScene()
                            .GetInputList(new GetInputListRequestData("text_gdiplus_v3"))
                            .Sleep(new SleepRequestData(sleepMillis: 100))
                            .SetInputSettings(
                                new SetInputSettingsRequestData(
                                    batchSettingsPayload,
                                    inputName: "MyTextSource", // REPLACE WITH YOUR ACTUAL TEXT SOURCE NAME
                                    overlay: true
                                )
                            )
                            // Still available for anything the generated methods do not cover.
                            .Add("GetStats"),
                    executionType: RequestBatchExecutionType.SerialRealtime,
                    haltOnFailure: false, // Continue even if one fails
                    cancellationToken: cancellationToken
                );

                Table batchTable = new() { Title = new TableTitle($"Batch Results ({batchResults.Count} items)") };
                _ = batchTable.AddColumn("Request");
                _ = batchTable.AddColumn("Status");
                _ = batchTable.AddColumn("Code");
                _ = batchTable.AddColumn("Details");
                foreach (RequestResponsePayload<object> result in batchResults)
                {
                    string shortId = result.RequestId[(result.RequestId.LastIndexOf('_') + 1)..];
                    string status = result.RequestStatus.Result ? "[green]Success[/]" : "[red]Failed[/]";
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
                            responseJson =
                                result.ResponseData is JsonElement jsonElement
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
                        (
                            "Note",
                            "Reflects last requested flags, not server-acknowledged state."
                        ),
                    ]
                );
                return false;

            case "set-subs":
                if (args.Length == 0 || !uint.TryParse(args[0], out uint newFlags))
                {
                    UiWarn("Usage: set-subs <numeric_flags>");
                    UiInfo(
                        "Example: set-subs 65 (General | Scenes | Inputs, 1 | 4 | 8 = 13)"
                    );
                    UiInfo(
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
                    newFlags,
                    cancellationToken: cancellationToken
                );
                _currentSubscriptionFlags = newFlags; // Update our stored value *after* successful re-identify
                UiSuccess(
                    $"Re-identified successfully. Intended subscriptions set to: {_currentSubscriptionFlags} ({(EventSubscription)_currentSubscriptionFlags})"
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
        Rule rule = new("[cyan]Transport Validation[/]")
        {
            Justification = Justify.Left
        };
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
                .GetVersionAsync(cancellationToken: cancellationToken)
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
                .GetSceneListAsync(new(), cancellationToken)
                .ConfigureAwait(false);
            if (scenes?.Scenes is null || scenes.Scenes.Count == 0)
            {
                throw new InvalidOperationException(
                    $"[{format}] GetSceneList returned no scenes."
                );
            }

            _logger.LogInformation(
                "[{Format}] Scene stubs deserialized: {SceneCount}",
                format,
                scenes?.Scenes?.Count ?? 0
            );
            int sceneCount = scenes?.Scenes?.Count ?? 0;

            GetInputListResponseData? inputs = await cycleClient
                .GetInputListAsync(new GetInputListRequestData(), cancellationToken)
                .ConfigureAwait(false);
            if (inputs?.Inputs is null || inputs.Inputs.Count == 0)
            {
                throw new InvalidOperationException(
                    $"[{format}] GetInputList returned no inputs."
                );
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
                    .GetSourceFilterListAsync(
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
                .GetSourceFilterKindListAsync(cancellationToken)
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

            Task<CustomEventEventArgs> waitForCustomEvent = cycleClient.WaitForEventAsync<
                CustomEventEventArgs
            >(
                predicate: _ => true,
                timeout: TimeSpan.FromSeconds(2),
                cancellationToken: cancellationToken
            );

            await cycleClient
                .BroadcastCustomEventAsync(
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
                && actualCustomData.GetProperty("nested").GetProperty("levels").GetArrayLength() == 3
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

            _logger.LogInformation("[{Format}] Batch call results: {ResultCount}", format, batch.Count);

            List<(string Label, bool Pass, string Detail)> settingsResults =
                await ValidateSettingsModesAsync(cycleClient, inputs, cancellationToken)
                    .ConfigureAwait(false);

            List<(string Label, bool Pass, string Detail)> modernResults =
                await ValidateModernApisAsync(cycleClient, cancellationToken).ConfigureAwait(false);

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
            _ = summary.AddRow("CustomEvent", customEventVerified ? "[green]Pass[/]" : "[yellow]Unverified[/]");
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
                await cycleClient.DisconnectAsync(cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Validates all three settings API modes for both InputSettings and FilterSettings.
    /// All operations are read-then-write-back (overlay:true) so they are non-destructive.
    /// Requires at least one browser_source and one input with a gain_filter in OBS.
    /// </summary>
    private static async Task<List<(string Label, bool Pass, string Detail)>> ValidateSettingsModesAsync(
        ObsWebSocketClient client,
        GetInputListResponseData? inputs,
        CancellationToken cancellationToken)
    {
        List<(string Label, bool Pass, string Detail)> results = [];
        if (inputs is null)
        {
            results.Add(("Settings [all modes]", false, "GetInputList returned null"));
            return results;
        }

        // ── InputSettings ─────────────────────────────────────────────────────
        string? browserInputName = inputs.Inputs
            ?.FirstOrDefault(i => string.Equals(i.InputKind, "browser_source", StringComparison.OrdinalIgnoreCase))
            ?.InputName;

        if (string.IsNullOrEmpty(browserInputName))
        {
            results.Add(("InputSettings [all modes]", false, "No browser_source in OBS — add one to test"));
        }
        else
        {
            // Mode 1: raw JsonElement via protocol-level call
            results.Add(await TrySettingsCheckAsync("InputSettings Mode1 (raw JsonElement)", async () =>
            {
                GetInputSettingsResponseData? r = await client.GetInputSettingsAsync(
                    new GetInputSettingsRequestData(browserInputName), cancellationToken);
                if (r?.InputSettings is not JsonElement el)
                {
                    return (false, "null InputSettings in response");
                }

                await client.SetInputSettingsAsync(
                    new SetInputSettingsRequestData(el, inputName: browserInputName, overlay: true),
                    cancellationToken);
                string url = el.TryGetProperty("url", out JsonElement p) ? p.GetString() ?? "(no url)" : "(no url key)";
                return (true, $"'{browserInputName}' url={url}");
            }));

            // Mode 2: library-registered type via implicit GetTypeInfo lookup
            results.Add(await TrySettingsCheckAsync("InputSettings Mode2 (BrowserSourceSettings)", async () =>
            {
                BrowserSourceSettings? s = await client.GetInputSettingsAsync<BrowserSourceSettings>(
                    browserInputName, cancellationToken);
                if (s is null)
                {
                    return (false, "null result");
                }

                await client.SetInputSettingsAsync(browserInputName, s, overlay: true, cancellationToken: cancellationToken);
                return (true, $"'{browserInputName}' url={s.Url ?? "(null)"}");
            }));

            // Mode 3: consumer-defined type with explicit JsonTypeInfo<T>
            results.Add(await TrySettingsCheckAsync("InputSettings Mode3 (consumer JsonTypeInfo)", async () =>
            {
                JsonTypeInfo<WorkerBrowserUrlSettings> typeInfo = WorkerSettingsJsonContext.Default.WorkerBrowserUrlSettings;
                WorkerBrowserUrlSettings? s = await client.GetInputSettingsAsync(
                    browserInputName, typeInfo, cancellationToken);
                if (s is null)
                {
                    return (false, "null result");
                }

                await client.SetInputSettingsAsync(browserInputName, s, typeInfo, overlay: true, cancellationToken: cancellationToken);
                return (true, $"'{browserInputName}' url={s.Url ?? "(null)"}");
            }));
        }

        // ── FilterSettings ────────────────────────────────────────────────────
        // Find first gain_filter across the first 5 inputs.
        string? filterSourceName = null;
        string? gainFilterName = null;
        foreach (Core.Protocol.Common.InputStub input in inputs.Inputs?.Where(i => !string.IsNullOrEmpty(i.InputName)).Take(5) ?? [])
        {
            try
            {
                GetSourceFilterListResponseData? fl = await client.GetSourceFilterListAsync(
                    new GetSourceFilterListRequestData(sourceName: input.InputName!), cancellationToken);
                Core.Protocol.Common.FilterStub? gain = fl?.Filters?.FirstOrDefault(f =>
                    string.Equals(f.FilterKind, "gain_filter", StringComparison.OrdinalIgnoreCase));
                if (gain?.FilterName is not null)
                {
                    filterSourceName = input.InputName;
                    gainFilterName = gain.FilterName;
                    break;
                }
            }
            catch { /* skip inputs we can't query */ }
        }

        if (string.IsNullOrEmpty(filterSourceName) || string.IsNullOrEmpty(gainFilterName))
        {
            results.Add(("FilterSettings [all modes]", false, "No gain_filter found — add one to an input in OBS"));
        }
        else
        {
            // Mode 1: raw JsonElement via protocol-level call
            results.Add(await TrySettingsCheckAsync("FilterSettings Mode1 (raw JsonElement)", async () =>
            {
                GetSourceFilterResponseData? r = await client.GetSourceFilterAsync(
                    new GetSourceFilterRequestData { SourceName = filterSourceName, FilterName = gainFilterName },
                    cancellationToken);
                if (r?.FilterSettings is not JsonElement el)
                {
                    return (false, "null FilterSettings in response");
                }

                await client.SetSourceFilterSettingsAsync(
                    new SetSourceFilterSettingsRequestData(gainFilterName, el, sourceName: filterSourceName, overlay: true),
                    cancellationToken);
                string db = el.TryGetProperty("db", out JsonElement p) ? p.GetDouble().ToString("F1") : "(no db key)";
                return (true, $"'{filterSourceName}/{gainFilterName}' db={db}");
            }));

            // Mode 2: library-registered type via implicit GetTypeInfo lookup
            results.Add(await TrySettingsCheckAsync("FilterSettings Mode2 (GainFilterSettings)", async () =>
            {
                GainFilterSettings? s = await client.GetSourceFilterSettingsAsync<GainFilterSettings>(
                    filterSourceName, gainFilterName, cancellationToken);
                if (s is null)
                {
                    return (false, "null result");
                }

                await client.SetSourceFilterSettingsAsync(filterSourceName, gainFilterName, s, overlay: true, cancellationToken: cancellationToken);
                return (true, $"'{filterSourceName}/{gainFilterName}' db={s.Db?.ToString("F1") ?? "(null)"}");
            }));

            // Mode 3: consumer-defined type with explicit JsonTypeInfo<T>
            results.Add(await TrySettingsCheckAsync("FilterSettings Mode3 (consumer JsonTypeInfo)", async () =>
            {
                JsonTypeInfo<WorkerGainDbSettings> typeInfo = WorkerSettingsJsonContext.Default.WorkerGainDbSettings;
                WorkerGainDbSettings? s = await client.GetSourceFilterSettingsAsync(
                    filterSourceName, gainFilterName, typeInfo, cancellationToken);
                if (s is null)
                {
                    return (false, "null result");
                }

                await client.SetSourceFilterSettingsAsync(filterSourceName, gainFilterName, s, typeInfo, overlay: true, cancellationToken: cancellationToken);
                return (true, $"'{filterSourceName}/{gainFilterName}' db={s.Db?.ToString("F1") ?? "(null)"}");
            }));
        }

        return results;
    }

    /// <summary>
    /// Exercises the modern conveniences against a scene and input this method creates itself,
    /// so the run does not depend on any particular OBS layout. Everything it makes is removed
    /// again, whether the checks pass or not.
    /// </summary>
    private static async Task<List<(string Label, bool Pass, string Detail)>> ValidateModernApisAsync(
        ObsWebSocketClient client,
        CancellationToken cancellationToken)
    {
        List<(string Label, bool Pass, string Detail)> results = [];

        string suffix = Guid.NewGuid().ToString("N")[..8];
        string sceneName = $"__obsws_validation_{suffix}";
        string inputName = $"__obsws_input_{suffix}";

        GetSceneListResponseData? sceneList = await client
            .GetSceneListAsync(new GetSceneListRequestData(), cancellationToken)
            .ConfigureAwait(false);
        string originalScene = sceneList?.CurrentProgramSceneName ?? string.Empty;

        bool sceneCreated = false;
        bool inputCreated = false;

        try
        {
            await client
                .CreateSceneAsync(new CreateSceneRequestData(sceneName), cancellationToken)
                .ConfigureAwait(false);
            sceneCreated = true;

            results.Add(await TrySettingsCheckAsync("SceneExistsAsync", async () =>
            {
                bool present = await client.SceneExistsAsync(sceneName, cancellationToken).ConfigureAwait(false);
                bool absent = await client.SceneExistsAsync(sceneName + "__nope", cancellationToken).ConfigureAwait(false);
                return (present && !absent, $"present={present}, absent={!absent}");
            }).ConfigureAwait(false));

            // A media source carries audio, so the volume and media transport helpers apply.
            _ = await client
                .CreateInputAsync(
                    "ffmpeg_source",
                    inputName,
                    new MediaSourceSettings(IsLocalFile: true),
                    sceneName: sceneName,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            inputCreated = true;

            results.Add(await TrySettingsCheckAsync("FindSceneItemIdAsync", async () =>
            {
                double? hit = await client.FindSceneItemIdAsync(sceneName, inputName, cancellationToken).ConfigureAwait(false);
                double? miss = await client.FindSceneItemIdAsync(sceneName, "__not_here__", cancellationToken).ConfigureAwait(false);
                return (hit is not null && miss is null, $"hit={hit}, miss={(miss is null ? "null" : "unexpected")}");
            }).ConfigureAwait(false));

            results.Add(await TrySettingsCheckAsync("SetSceneItemEnabledAsync (toggle)", async () =>
            {
                bool off = await client.SetSceneItemEnabledAsync(sceneName, inputName, false, cancellationToken).ConfigureAwait(false);
                bool toggled = await client.SetSceneItemEnabledAsync(sceneName, inputName, null, cancellationToken).ConfigureAwait(false);
                return (!off && toggled, $"set false -> {off}, toggled -> {toggled}");
            }).ConfigureAwait(false));

            results.Add(await TrySettingsCheckAsync("SetInputVolumeDbAsync", async () =>
            {
                await client.SetInputVolumeDbAsync(inputName, -6, cancellationToken).ConfigureAwait(false);
                GetInputVolumeResponseData? volume = await client
                    .GetInputVolumeAsync(new GetInputVolumeRequestData(inputName: inputName), cancellationToken)
                    .ConfigureAwait(false);
                double db = volume?.InputVolumeDb ?? double.NaN;
                return (Math.Abs(db + 6) < 0.5, $"db={db:0.##}");
            }).ConfigureAwait(false));

            results.Add(await TrySettingsCheckAsync("Media transport (typed enum)", async () =>
            {
                await client.TriggerMediaActionAsync(inputName, MediaInputAction.Stop, cancellationToken).ConfigureAwait(false);
                return (true, "sent " + MediaInputAction.Stop.ToWireValue());
            }).ConfigureAwait(false));

            results.Add(await TrySettingsCheckAsync("Event stream (await foreach)", async () =>
            {
                using CancellationTokenSource streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                streamCts.CancelAfter(TimeSpan.FromSeconds(10));

                List<string> observed = [];
                Task consume = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (CurrentProgramSceneChangedEventArgs sceneEvent
                            in client.CurrentProgramSceneChangedStream(cancellationToken: streamCts.Token)
                            .ConfigureAwait(false))
                        {
                            observed.Add(sceneEvent.EventData.SceneName ?? string.Empty);
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
                }, CancellationToken.None);

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                await client.SwitchSceneAsync(sceneName, cancellationToken: cancellationToken).ConfigureAwait(false);
                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(originalScene))
                {
                    await client.SwitchSceneAsync(originalScene, cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                await consume.ConfigureAwait(false);
                return (observed.Count >= 2, $"observed {observed.Count}: {string.Join(" -> ", observed)}");
            }).ConfigureAwait(false));

            results.Add(await TrySettingsCheckAsync("WaitForEventAsync (timeout overload)", async () =>
            {
                Task<SceneItemEnableStateChangedEventArgs> wait = client
                    .WaitForEventAsync<SceneItemEnableStateChangedEventArgs>(TimeSpan.FromSeconds(5), cancellationToken);
                _ = await client.SetSceneItemEnabledAsync(sceneName, inputName, false, cancellationToken).ConfigureAwait(false);
                try
                {
                    SceneItemEnableStateChangedEventArgs observed = await wait.ConfigureAwait(false);
                    return (true, $"enabled={observed.EventData.SceneItemEnabled}");
                }
                catch (TimeoutException)
                {
                    return (false, "timed out");
                }
            }).ConfigureAwait(false));

            results.Add(await TrySettingsCheckAsync("Typed batch builder", async () =>
            {
                List<RequestResponsePayload<object>> typedBatch = await client
                    .CallBatchAsync(
                        batchBuilder => batchBuilder
                            .GetVersion()
                            .Sleep(new SleepRequestData(sleepMillis: 25))
                            .GetSceneList(new GetSceneListRequestData())
                            .Add("GetStats"),
                        executionType: RequestBatchExecutionType.SerialRealtime,
                        haltOnFailure: false,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                bool allOk = typedBatch.Count == 4 && typedBatch.All(r => r.RequestStatus.Result);
                return (allOk, $"{typedBatch.Count} result(s), all ok = {allOk}");
            }).ConfigureAwait(false));

            results.Add(await TrySettingsCheckAsync("Output state helpers", async () =>
            {
                bool recording = await client.IsRecordActiveAsync(cancellationToken).ConfigureAwait(false);
                bool streaming = await client.IsStreamActiveAsync(cancellationToken).ConfigureAwait(false);
                bool virtualCam = await client.IsVirtualCamActiveAsync(cancellationToken).ConfigureAwait(false);
                return (true, $"record={recording}, stream={streaming}, virtualCam={virtualCam}");
            }).ConfigureAwait(false));
        }
        finally
        {
            // Always put OBS back the way it was found.
            try
            {
                if (!string.IsNullOrEmpty(originalScene))
                {
                    await client.SwitchSceneAsync(originalScene, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }

                if (inputCreated)
                {
                    await client
                        .RemoveInputAsync(new RemoveInputRequestData(inputName: inputName), CancellationToken.None)
                        .ConfigureAwait(false);
                }

                if (sceneCreated)
                {
                    await client
                        .RemoveSceneAsync(new RemoveSceneRequestData(sceneName: sceneName), CancellationToken.None)
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
        Func<Task<(bool Pass, string Detail)>> action)
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
            ..(scenes?.Scenes ?? []).Select(scene => scene.ExtensionData),
            ..(inputs?.Inputs ?? []).Select(input => input.ExtensionData),
        ];

        int extensionBagCount = extensionBags.Count(bag => bag is { Count: > 0 });
        int extensionEntryCount = extensionBags
            .Where(bag => bag is { Count: > 0 })
            .Sum(bag => bag!.Count);

        bool valid = true;
        foreach (Dictionary<string, JsonElement>? bag in extensionBags.Where(bag => bag is { Count: > 0 }))
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
                        TryFindCustomEventPayloadByTestIdCore(element, testId, depth + 1, out payload)
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

    private async Task GetAllSettingsTypesAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Fetching all settings type schemas from OBS...");

        await DumpKindDefaultSettingsAsync(
            "Filter Kind Defaults",
            async ct =>
            {
                GetSourceFilterKindListResponseData? r = await _obsClient.GetSourceFilterKindListAsync(cancellationToken: ct);
                return r?.SourceFilterKinds ?? [];
            },
            async (kind, ct) =>
            {
                GetSourceFilterDefaultSettingsResponseData? r = await _obsClient.GetSourceFilterDefaultSettingsAsync(
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
                GetInputKindListResponseData? r = await _obsClient.GetInputKindListAsync(
                    new GetInputKindListRequestData(unversioned: false),
                    cancellationToken: ct
                );
                return r?.InputKinds ?? [];
            },
            async (kind, ct) =>
            {
                GetInputDefaultSettingsResponseData? r = await _obsClient.GetInputDefaultSettingsAsync(
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

        _logger.LogInformation("Found {Count} kinds for '{Panel}'. Fetching defaults...", kinds.Count, panelTitle);

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
            GetOutputListResponseData? response = await _obsClient.GetOutputListAsync(
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
                GetOutputSettingsResponseData? r = await _obsClient.GetOutputSettingsAsync(
                    new GetOutputSettingsRequestData(outputName: name),
                    cancellationToken: cancellationToken
                );
                results[key] = r?.OutputSettings;
            }
            catch (ObsWebSocketException ex)
            {
                _logger.LogWarning("Could not get settings for output '{Name}': {Msg}", name, ex.Message);
                results[key] = null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error getting settings for output '{Name}'.", name);
                results[key] = null;
            }
        }

        RenderJsonPanel("Output Settings (current instances)", SerializeKindDefaults(results));
    }

    private async Task DumpStreamServiceSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            GetStreamServiceSettingsResponseData? response = await _obsClient.GetStreamServiceSettingsAsync(
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

            RenderJsonPanel("Stream Service Settings", System.Text.Encoding.UTF8.GetString(buf.WrittenSpan));
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
        GetSceneListResponseData? sceneList = await _obsClient.GetSceneListAsync(
            new(),
            cancellationToken: cancellationToken
        );

        if (sceneList?.Scenes is null || sceneList.Scenes.Count == 0)
        {
            UiWarn("Could not retrieve scene list from OBS.");
            return;
        }

        string currentProgramScene = sceneList.CurrentProgramSceneName ?? string.Empty;

        List<string> sceneNames = [
            ..sceneList.Scenes
                .Select(s => s.SceneName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!),
        ];

        // Place current program scene first, then alphabetically
        List<string> orderedSceneNames = !string.IsNullOrEmpty(currentProgramScene)
            ? [
                ..sceneNames.Where(n => n == currentProgramScene),
                ..sceneNames.Where(n => n != currentProgramScene).OrderBy(n => n),
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
        Task<GetSceneItemListResponseData> sceneItemsTask = _obsClient.GetSceneItemListAsync(
            new GetSceneItemListRequestData(sceneName: selectedScene),
            cancellationToken: cancellationToken
        );
        Task<GetInputListResponseData> browserInputsTask = _obsClient.GetInputListAsync(
            new GetInputListRequestData("browser_source"),
            cancellationToken: cancellationToken
        );

        await Task.WhenAll(sceneItemsTask, browserInputsTask).ConfigureAwait(false);

        GetSceneItemListResponseData? sceneItemList = await sceneItemsTask;
        GetInputListResponseData? browserInputList = await browserInputsTask;

        // Find browser sources that already exist in the selected scene
        HashSet<string> sceneSourceNames = sceneItemList?.SceneItems?
            .Select(si => si.SourceName ?? string.Empty)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        List<string> existingBrowserSourcesInScene = browserInputList?.Inputs?
            .Where(i => sceneSourceNames.Contains(i.InputName ?? string.Empty))
            .Select(i => i.InputName!)
            .Where(n => !string.IsNullOrEmpty(n))
            .OrderBy(n => n)
            .ToList() ?? [];

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
                new TextPrompt<string>("New browser source [cyan]name[/]:")
                    .Validate(s =>
                        !string.IsNullOrWhiteSpace(s)
                            ? ValidationResult.Success()
                            : ValidationResult.Error("[red]Name cannot be empty.[/]")
                    )
            )
            : selectedSourceChoice;

        // Step 5: Get canvas dimensions from video settings
        GetVideoSettingsResponseData? videoSettings = await _obsClient.GetVideoSettingsAsync(
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
            new TextPrompt<string>("Browser source [cyan]URL[/]:")
                .Validate(s =>
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

        double sceneItemId;

        // Step 8: Create new input or update existing source settings
        if (isNewSource)
        {
            UiInfo($"Creating browser source '{sourceName}' in scene '{selectedScene}'...");

            CreateInputResponseData? createResult = await _obsClient.CreateInputAsync(
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
            await _obsClient.SetInputSettingsAsync(
                inputName: sourceName,
                settings: browserSettings,
                overlay: false,
                cancellationToken: cancellationToken
            );

            sceneItemId = await GetSceneItemIdAsync(selectedScene, sourceName, cancellationToken);
            UiSuccess($"Updated '{sourceName}' (scene item ID: {sceneItemId}).");
        }

        // Step 9: Set Blend Mode to Normal (explicit, even though it is the default)
        await _obsClient.SetSceneItemBlendModeAsync(
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
        _ = commandTable.AddRow(
            Markup.Escape("scene"),
            Markup.Escape("Get current program scene")
        );
        _ = commandTable.AddRow(
            Markup.Escape("mute [input name]"),
            Markup.Escape("Toggle mute for audio input")
        );
        _ = commandTable.AddRow(Markup.Escape("unmute [input name]"), Markup.Escape("Alias for mute"));
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
            Markup.Escape("watch [seconds]"),
            Markup.Escape("Stream scene changes with await foreach (default 15s)")
        );
        _ = commandTable.AddRow(
            Markup.Escape("batch-example"),
            Markup.Escape("Run sample batch request sequence via the typed builder")
        );
        _ = commandTable.AddRow(
            Markup.Escape("run-transport-tests"),
            Markup.Escape("Run validation cycle for the configured transport (version, scenes, inputs, filters, custom event, batch, settings modes 1/2/3)")
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
            Markup.Escape("Dump default settings for all filter kinds, input kinds, and current stream service")
        );
        _ = commandTable.AddRow(
            Markup.Escape("add-browser-source"),
            Markup.Escape("Create or update a fullscreen browser source overlay in a scene")
        );
        AnsiConsole.Write(commandTable);
    }

    private static void RenderKeyValueTable(string title, IReadOnlyList<(string Key, string Value)> rows)
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
        // The wire value is a string; OutputStateExtensions.FromWireValue turns it into the
        // typed enum so it can be matched instead of compared against protocol constants.
        string description = OutputStateExtensions.FromWireValue(e.EventData.OutputState) switch
        {
            OutputState.Starting => "starting up",
            OutputState.Started => "live",
            OutputState.Stopping => "shutting down",
            OutputState.Stopped => "offline",
            OutputState.Reconnecting => "reconnecting",
            OutputState.Reconnected => "reconnected",
            OutputState.Paused => "paused",
            OutputState.Unknown => "in an unknown state",
            null => $"reporting an unrecognised state ({e.EventData.OutputState})",
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

internal sealed record WorkerGainDbSettings(
    [property: JsonPropertyName("db")] double? Db = null
);

[JsonSerializable(typeof(WorkerBrowserUrlSettings))]
[JsonSerializable(typeof(WorkerGainDbSettings))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
internal sealed partial class WorkerSettingsJsonContext : JsonSerializerContext { }


