using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Events;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Protocol;
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
    ILoggerFactory loggerFactory,
    IWebSocketConnectionFactory connectionFactory,
    IHostApplicationLifetime lifetime
) : BackgroundService
{
    private readonly ILogger<Worker> _logger = logger;
    private readonly ObsWebSocketClient _obsClient = obsClient;
    private readonly ObsWebSocketClientOptions _baseOptions = obsOptions.Value;
    private readonly ExampleValidationOptions _validationOptions = validationOptions.Value;
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

                    // Construct the settings object for Text (GDI+) source
                    // IMPORTANT: The exact property name ('text') depends on the input kind.
                    // Assume 'text' for 'text_gdiplus_v3'. Inspect with 'get-input-settings' if unsure.
                    ArrayBufferWriter<byte> textPayloadBuffer = new();
                    using (Utf8JsonWriter writer = new(textPayloadBuffer))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("text", newText);
                        writer.WriteEndObject();
                        writer.Flush();
                    }

                    using JsonDocument textPayloadDocument = JsonDocument.Parse(
                        textPayloadBuffer.WrittenMemory
                    );
                    JsonElement newSettings = textPayloadDocument.RootElement.Clone();

                    // Send the request
                    await _obsClient.SetInputSettingsAsync(
                        new SetInputSettingsRequestData(
                            newSettings,
                            inputName: inputForSetText, // Identify input by name
                            overlay: true // Only update the 'text' field
                        ),
                        cancellationToken: cancellationToken
                    );

                    UiSuccess(
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
                    UiWarn("Usage: list-filters [source name]");
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

                List<BatchRequestItem> batchItems =
                [
                    new("GetVersion", null),
                    new("GetCurrentProgramScene", null),
                    new("GetInputList", new GetInputListRequestData("text_gdiplus_v3")),
                    new("Sleep", new SleepRequestData(sleepMillis: 100)),
                    new(
                        "SetInputSettings", // Example: Set specific text source's text (requires knowing name)
                        new SetInputSettingsRequestData(
                            batchSettingsPayload,
                            inputName: "MyTextSource", // REPLACE WITH YOUR ACTUAL TEXT SOURCE NAME
                            overlay: true
                        )
                    ),
                ];

                List<RequestResponsePayload<object>> batchResults = await _obsClient.CallBatchAsync(
                    batchItems,
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

            case "get-all-filter-settings": // New Command
                await GetAllFilterSettingsForSource(cancellationToken);
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
                .GetSceneListAsync(cancellationToken)
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
                        new GetSourceFilterListRequestData(inputName),
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

            Task<CustomEventEventArgs?> waitForCustomEvent = cycleClient.WaitForEventAsync<
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

            CustomEventEventArgs? customEvent = await waitForCustomEvent.ConfigureAwait(false);
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

            Table summary = new() { Title = new TableTitle($"{format} Validation Summary") };
            _ = summary.AddColumn("Check");
            _ = summary.AddColumn("Result");
            _ = summary.AddRow("OBS Version", Markup.Escape(validatedVersion.ObsVersion ?? "N/A"));
            _ = summary.AddRow("RPC", validatedVersion.RpcVersion.ToString());
            _ = summary.AddRow("Scenes", sceneCount.ToString());
            _ = summary.AddRow("Inputs", inputCount.ToString());
            _ = summary.AddRow("Filters (first input)", filterCount.ToString());
            _ = summary.AddRow("Filter Kinds", filterKindCount.ToString());
            _ = summary.AddRow("CustomEvent", customEventVerified ? "[green]Pass[/]" : "[yellow]Unverified[/]");
            _ = summary.AddRow("Batch", $"{batch.Count} result(s)");
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

    private IWebSocketMessageSerializer CreateSerializer(SerializationFormat format) =>
        format switch
        {
            SerializationFormat.MsgPack => new MsgPackMessageSerializer(
                _loggerFactory.CreateLogger<MsgPackMessageSerializer>()
            ),
            _ => new JsonMessageSerializer(_loggerFactory.CreateLogger<JsonMessageSerializer>()),
        };

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
        Rule settingsStartRule = new("[yellow]Filter Default Settings[/]")
        {
            Justification = Justify.Left
        };
        AnsiConsole.Write(settingsStartRule);
        try
        {
            ArrayBufferWriter<byte> outputBuffer = new();
            using (Utf8JsonWriter writer = new(outputBuffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach ((string filterKind, JsonElement? value) in filterDefaultSettings)
                {
                    writer.WritePropertyName(filterKind);
                    if (value is JsonElement element)
                    {
                        element.WriteTo(writer);
                    }
                    else
                    {
                        writer.WriteNullValue();
                    }
                }

                writer.WriteEndObject();
                writer.Flush();
            }

            string jsonOutput = System.Text.Encoding.UTF8.GetString(outputBuffer.WrittenSpan);
            RenderJsonPanel("Filter Kind Defaults", jsonOutput);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to serialize filter default settings results to JSON.");
            UiError("{\"error\": \"Failed to serialize results.\"}");
        }

        _logger.LogInformation("Default filter settings retrieval finished.");
        // No cleanup needed as we didn't add filters to the source
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
            Markup.Escape("batch-example"),
            Markup.Escape("Run sample batch request sequence")
        );
        _ = commandTable.AddRow(
            Markup.Escape("run-transport-tests"),
            Markup.Escape("Run JSON + MsgPack validation cycles")
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
            Markup.Escape("get-all-filter-settings"),
            Markup.Escape("Dump default settings for all filter kinds")
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


