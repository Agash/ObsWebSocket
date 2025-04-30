using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Protocol.Common;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Tests;

/// <summary>
/// Integration tests for ObsWebSocketClient connecting to a live OBS instance.
/// Requires OBS Studio running with the WebSocket server enabled.
/// Connection details are read from 'testsettings.local.json' (ensure this file exists and is configured).
/// </summary>
[TestClass]
[DoNotParallelize] // Ensure tests run sequentially against the OBS instance
[TestCategory("Integration")]
public class ObsWebSocketClientIntegrationTests
{
    private static IServiceProvider s_serviceProvider = null!;
    private static ObsIntegrationTestOptions s_testOptions = null!;
    private const int DefaultTimeoutMs = 10000; // Default for most tests
    private const int EventTimeoutMs = 15000; // Allow more time for manual interaction tests

    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        bool enableTrace = Environment.GetEnvironmentVariable("CI_TRACE") == "1";
        LogLevel minLogLevel = enableTrace ? LogLevel.Trace : LogLevel.Information;

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("testsettings.json", optional: true) // Base settings (optional)
            .AddJsonFile("testsettings.local.json", optional: false, reloadOnChange: false) // Local overrides (required)
            .AddEnvironmentVariables()
            .Build();

        ServiceCollection services = new();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(minLogLevel));

        // Bind the "ObsIntegration" section from configuration to the options class
        services.Configure<ObsIntegrationTestOptions>(configuration.GetSection("ObsIntegration"));

        // Configure ObsWebSocketClientOptions based on ObsIntegrationTestOptions
        services
            .AddOptions<ObsWebSocketClientOptions>()
            .Configure<IOptions<ObsIntegrationTestOptions>>(
                (coreOptions, testOpts) =>
                {
                    coreOptions.ServerUri =
                        testOpts.Value.ServerUri != null ? new Uri(testOpts.Value.ServerUri) : null;
                    coreOptions.Password = testOpts.Value.Password;
                    coreOptions.AutoReconnectEnabled = false; // Disable auto-reconnect for tests
                }
            );

        // Add the OBS WebSocket client services
        services.AddObsWebSocketClient();

        s_serviceProvider = services.BuildServiceProvider();
        s_testOptions = s_serviceProvider
            .GetRequiredService<IOptions<ObsIntegrationTestOptions>>()
            .Value;

        // --- Configuration Validation ---
        static void ValidateOption(string? value, string name)
        {
            Assert.IsFalse(
                string.IsNullOrWhiteSpace(value),
                $"{name} must be configured in testsettings.local.json ('ObsIntegration' section)"
            );
            Trace.WriteLine($"Using {name}: '{value}'");
        }

        ValidateOption(s_testOptions.ServerUri, nameof(s_testOptions.ServerUri));
        context.WriteLine(
            s_testOptions.Password is { Length: > 0 }
                ? "Using password authentication."
                : "Using no authentication."
        );
        ValidateOption(s_testOptions.TestSceneName, nameof(s_testOptions.TestSceneName));
        ValidateOption(s_testOptions.TestInputName, nameof(s_testOptions.TestInputName));
        ValidateOption(s_testOptions.TestFilterName, nameof(s_testOptions.TestFilterName));
        ValidateOption(s_testOptions.TestAudioInputName, nameof(s_testOptions.TestAudioInputName));
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static async Task ClassCleanup()
    {
        if (s_serviceProvider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (s_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static ObsWebSocketClient CreateClient() =>
        s_serviceProvider.GetRequiredService<ObsWebSocketClient>();

    // --- Test Cases ---

    [TestMethod, TestCategory("Integration")]
    [Timeout(DefaultTimeoutMs)]
    public async Task ConnectDisconnect_ValidUri_Succeeds()
    {
        /* OBS Setup: Requires OBS running with WebSocket enabled at the configured URI. */
        ObsWebSocketClient client = CreateClient();
        bool connectedEventFired = false;
        bool disconnectedEventFired = false;
        Exception? disconnectReason = new("Placeholder"); // Start non-null to check for graceful disconnect

        client.Connected += (_, _) => connectedEventFired = true;
        client.Disconnected += (_, e) =>
        {
            disconnectedEventFired = true;
            disconnectReason = e.ReasonException;
        };

        try
        {
            await client.ConnectAsync();

            Assert.IsTrue(client.IsConnected, "Client should be connected after ConnectAsync.");
            Assert.IsTrue(connectedEventFired, "Connected event should have fired.");
            Assert.IsNotNull(client.NegotiatedRpcVersion, "NegotiatedRpcVersion should be set.");
            Assert.IsTrue(client.NegotiatedRpcVersion >= 1, "NegotiatedRpcVersion should be >= 1.");
            Assert.IsNotNull(
                client.CurrentEventSubscriptions,
                "CurrentEventSubscriptions should be set."
            ); // Check if event subs are received
        }
        finally
        {
            if (client.IsConnected) // Only disconnect if connected
            {
                await client.DisconnectAsync();
                Assert.IsFalse(
                    client.IsConnected,
                    "Client should be disconnected after DisconnectAsync."
                );
                Assert.IsTrue(disconnectedEventFired, "Disconnected event should have fired.");
                Assert.IsNull(
                    disconnectReason,
                    "Disconnect reason should be null for graceful disconnect."
                );
            }

            await client.DisposeAsync(); // Ensure disposal
        }
    }

    [TestMethod, TestCategory("Integration")]
    [Timeout(DefaultTimeoutMs)]
    public async Task GetVersion_ReturnsValidData()
    {
        /* OBS Setup: Requires OBS running with WebSocket enabled. */
        await using ObsWebSocketClient client = CreateClient(); // Use await using for disposal
        await client.ConnectAsync();
        Assert.IsTrue(client.IsConnected);

        GetVersionResponseData? version = await client.GetVersionAsync();

        Assert.IsNotNull(version);
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(version.ObsVersion),
            "ObsVersion should not be empty."
        );
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(version.ObsWebSocketVersion),
            "ObsWebSocketVersion should not be empty."
        );
        Assert.IsTrue(version.RpcVersion >= 1, "RpcVersion should be >= 1.");
        Assert.IsNotNull(version.AvailableRequests, "AvailableRequests should not be null.");
        Assert.IsTrue(
            version.AvailableRequests.Count > 0,
            "AvailableRequests should not be empty."
        );
    }

    [TestMethod, TestCategory("Integration")]
    [Timeout(DefaultTimeoutMs)]
    public async Task SetCurrentProgramScene_ChangesScene_WhenSceneExists()
    {
        string sceneName =
            s_testOptions.TestSceneName
            ?? throw new AssertInconclusiveException("TestSceneName not configured");

        await using ObsWebSocketClient client = CreateClient();
        await client.ConnectAsync();
        Assert.IsTrue(client.IsConnected);

        // Get original scene (optional, but good practice)
        GetCurrentProgramSceneResponseData? originalScene = null;
        try
        {
            originalScene = await client.GetCurrentProgramSceneAsync();
            Trace.WriteLine($"Original scene: {originalScene?.SceneName}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Could not get original scene: {ex.Message}");
        }

        try
        {
            // Act: Set the scene
            await client.SetCurrentProgramSceneAsync(
                new SetCurrentProgramSceneRequestData(sceneName)
            );

            // Assert: Check if the scene was actually changed
            GetCurrentProgramSceneResponseData? newScene =
                await client.GetCurrentProgramSceneAsync();
            Assert.IsNotNull(newScene);
            Assert.AreEqual(
                sceneName,
                newScene.SceneName,
                $"Scene should have been changed to '{sceneName}'."
            );
        }
        catch (ObsWebSocketException ex)
        {
            // If the scene doesn't exist, OBS returns an error
            if (
                ex.Message.Contains("NotFound", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("600")
            ) // 600 is ResourceNotFound code
            {
                Assert.Inconclusive(
                    $"Required scene '{sceneName}' not found in OBS. Please create it. OBS Error: {ex.Message}"
                );
            }

            throw; // Re-throw other OBS errors
        }
        finally
        {
            // Attempt to restore original scene (best effort)
            if (originalScene?.SceneName != null && originalScene.SceneName != sceneName)
            {
                try
                {
                    Trace.WriteLine($"Attempting to restore scene to: {originalScene.SceneName}");
                    await client.SetCurrentProgramSceneAsync(
                        new SetCurrentProgramSceneRequestData(originalScene.SceneName)
                    );
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Could not restore original scene: {ex.Message}");
                }
            }
        }
    }

    [TestMethod, TestCategory("Integration")]
    [Timeout(EventTimeoutMs)] // Allow more time for manual interaction + connection
    public async Task Event_CurrentProgramSceneChanged_WhenTriggeredManually()
    {
        /* OBS Setup: Requires OBS running, WebSocket enabled. */
        /* Test Action: Manually change the Program scene in OBS UI *after* the test connects. */
        await using ObsWebSocketClient client = CreateClient();
        TaskCompletionSource<CurrentProgramSceneChangedEventArgs> tcs = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        string? changedToSceneName = null;

        client.CurrentProgramSceneChanged += (sender, args) =>
        {
            Trace.WriteLine(
                $"--> Integration Test: Received CurrentProgramSceneChanged event for scene '{args.EventData.SceneName}'"
            );
            changedToSceneName = args.EventData.SceneName;
            tcs.TrySetResult(args); // Signal that the event was received
        };

        await client.ConnectAsync();
        Assert.IsTrue(client.IsConnected, "Client failed to connect.");

        // Get the initial scene to know what *not* to expect initially
        GetCurrentProgramSceneResponseData? initialScene = null;
        try
        {
            initialScene = await client.GetCurrentProgramSceneAsync();
        }
        catch
        { /* Ignore if fails */
        }

        Trace.WriteLine(
            $"Initial scene is: {initialScene?.SceneName ?? "Unknown"}. Waiting for scene change event..."
        );

        Console.WriteLine(
            $"\n>>> INTERACTION REQUIRED: Please manually change the Program scene in OBS now (within 10 seconds)!"
        );

        // Wait for the event to be fired
        bool eventReceived = await Task.WhenAny(tcs.Task, Task.Delay(10000)) == tcs.Task; // 10 second timeout for manual change

        // Assert
        Assert.IsTrue(
            eventReceived,
            "Did not receive the CurrentProgramSceneChanged event within the timeout. Did you change the scene in OBS?"
        );
        Assert.IsNotNull(changedToSceneName, "Scene name in event args was null.");

        // Optional: Assert the scene name changed if you know the initial scene
        if (initialScene?.SceneName != null)
        {
            Assert.AreNotEqual(
                initialScene.SceneName,
                changedToSceneName,
                "Scene name should have changed from the initial scene."
            );
        }

        Console.WriteLine($"<<< Event received for scene: {changedToSceneName}. Test successful.");
    }

    [TestMethod, TestCategory("Integration")]
    [Timeout(DefaultTimeoutMs)]
    public async Task GetSceneList_ReturnsSceneStubs()
    {
        /* OBS Setup: Requires OBS running with WebSocket enabled and at least one scene existing (the TestSceneName). */
        await using ObsWebSocketClient client = CreateClient();
        await client.ConnectAsync();
        Assert.IsTrue(client.IsConnected);

        GetSceneListResponseData? response = await client.GetSceneListAsync();

        Assert.IsNotNull(response, "GetSceneList response was null.");
        Assert.IsNotNull(response.Scenes, "Scenes list was null.");
        Assert.IsTrue(response.Scenes.Count > 0, "Expected at least one scene in the list.");

        // Find the test scene using the SceneStub
        SceneStub? testSceneStub = response.Scenes.FirstOrDefault(s =>
            s.SceneName == s_testOptions.TestSceneName
        );
        Assert.IsNotNull(
            testSceneStub,
            $"Test scene '{s_testOptions.TestSceneName}' not found in the list."
        );
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(testSceneStub.SceneUuid),
            "Scene UUID should not be empty."
        );
        Assert.IsTrue(testSceneStub.SceneIndex >= 0, "Scene index should be non-negative.");
        Trace.WriteLine(
            $"Found Test Scene Stub: Name={testSceneStub.SceneName}, UUID={testSceneStub.SceneUuid}, Index={testSceneStub.SceneIndex}"
        );
        // Check for extra data (unlikely for this basic type, but good pattern)
        if (testSceneStub.ExtensionData != null && testSceneStub.ExtensionData.Count > 0)
        {
            Trace.WriteLine(
                $"  ExtensionData found: {JsonSerializer.Serialize(testSceneStub.ExtensionData)}"
            );
        }
    }

    [TestMethod, TestCategory("Integration")]
    [Timeout(DefaultTimeoutMs)]
    public async Task GetInputList_ReturnsInputStubs()
    {
        /* OBS Setup: Requires OBS running with WebSocket enabled and at least one input (the TestInputName). */
        await using ObsWebSocketClient client = CreateClient();
        await client.ConnectAsync();
        Assert.IsTrue(client.IsConnected);

        GetInputListResponseData? response = await client.GetInputListAsync(
            new GetInputListRequestData()
        ); // Empty request data

        Assert.IsNotNull(response, "GetInputList response was null.");
        Assert.IsNotNull(response.Inputs, "Inputs list was null.");
        Assert.IsTrue(response.Inputs.Count > 0, "Expected at least one input in the list.");

        // Find the test input
        InputStub? testInputStub = response.Inputs.FirstOrDefault(i =>
            i.InputName == s_testOptions.TestInputName
        );
        Assert.IsNotNull(
            testInputStub,
            $"Test input '{s_testOptions.TestInputName}' not found in the list."
        );
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(testInputStub.InputUuid),
            "Input UUID should not be empty."
        );
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(testInputStub.InputKind),
            "Input Kind should not be empty."
        );
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(testInputStub.UnversionedInputKind),
            "Unversioned Input Kind should not be empty."
        );
        Trace.WriteLine(
            $"Found Test Input Stub: Name={testInputStub.InputName}, UUID={testInputStub.InputUuid}, Kind={testInputStub.InputKind}"
        );
    }

    [TestMethod, TestCategory("Integration")]
    [Timeout(DefaultTimeoutMs)]
    public async Task GetSceneItemTransform_ReturnsTransformStub()
    {
        /* OBS Setup: Requires OBS running with TestSceneName containing TestInputName. */
        await using ObsWebSocketClient client = CreateClient();
        await client.ConnectAsync();
        Assert.IsTrue(client.IsConnected);

        // Get the scene item ID first
        GetSceneItemIdResponseData? idResponse = await client.GetSceneItemIdAsync(
            new GetSceneItemIdRequestData(
                s_testOptions.TestInputName!,
                s_testOptions.TestSceneName!
            )
        );
        Assert.IsNotNull(
            idResponse,
            $"Could not get SceneItemId for '{s_testOptions.TestInputName}' in scene '{s_testOptions.TestSceneName}'. Ensure it exists."
        );
        double sceneItemId = idResponse.SceneItemId;

        // Get the transform
        GetSceneItemTransformResponseData? transformResponse =
            await client.GetSceneItemTransformAsync(
                new GetSceneItemTransformRequestData(sceneItemId, s_testOptions.TestSceneName!)
            );

        Assert.IsNotNull(transformResponse, "GetSceneItemTransform response was null.");
        Assert.IsNotNull(transformResponse.SceneItemTransform, "SceneItemTransform data was null.");

        // Validate some core transform properties
        SceneItemTransformStub transform = transformResponse.SceneItemTransform;
        Assert.IsNotNull(transform.PositionX, "PositionX should have a value.");
        Assert.IsNotNull(transform.PositionY, "PositionY should have a value.");
        Assert.IsNotNull(transform.ScaleX, "ScaleX should have a value.");
        Assert.IsNotNull(transform.ScaleY, "ScaleY should have a value.");
        Assert.IsNotNull(transform.Width, "Width should have a value.");
        Assert.IsNotNull(transform.Height, "Height should have a value.");
        Trace.WriteLine(
            $"Transform for Item {sceneItemId}: Pos=({transform.PositionX},{transform.PositionY}), Scale=({transform.ScaleX},{transform.ScaleY}), Size=({transform.Width}x{transform.Height})"
        );
    }

    [TestMethod, TestCategory("Integration")]
    [Timeout(DefaultTimeoutMs)]
    public async Task GetInputAudioTracks_ReturnsDictionary()
    {
        /* OBS Setup: Requires OBS running with an audio input named like 'Mic/Aux' (configure in TestAudioInputName). */
        await using ObsWebSocketClient client = CreateClient();
        await client.ConnectAsync();
        Assert.IsTrue(client.IsConnected);

        GetInputAudioTracksResponseData? response;
        try
        {
            response = await client.GetInputAudioTracksAsync(
                new GetInputAudioTracksRequestData(s_testOptions.TestAudioInputName!)
            );
        }
        catch (ObsWebSocketException ex) when (ex.Message.Contains("ResourceNotFound"))
        {
            Assert.Inconclusive(
                $"Required audio input '{s_testOptions.TestAudioInputName}' not found in OBS. Error: {ex.Message}"
            );
            return;
        }

        Assert.IsNotNull(response, "GetInputAudioTracks response was null.");
        Assert.IsNotNull(response.InputAudioTracks, "InputAudioTracks dictionary was null.");
        Assert.IsTrue(response.InputAudioTracks.Count > 0, "Expected at least one audio track.");

        // Check if common tracks exist (OBS usually has 6)
        for (int i = 1; i <= 6; i++)
        {
            Assert.IsTrue(
                response.InputAudioTracks.ContainsKey(i.ToString()),
                $"Track '{i}' not found in dictionary."
            );
            Assert.IsInstanceOfType<bool>(
                response.InputAudioTracks[i.ToString()],
                $"Track '{i}' value is not a boolean."
            );
            Trace.WriteLine(
                $"Audio Track {i}: {(response.InputAudioTracks[i.ToString()] ? "Enabled" : "Disabled")}"
            );
        }
    }

    [TestMethod, TestCategory("Integration")]
    [Timeout(DefaultTimeoutMs)]
    public async Task GetInputSettings_TextGDI_ReturnsJsonElement()
    {
        /* OBS Setup: Requires OBS running with TestSceneName containing TestInputName (which should be a Text GDI+ source). */
        await using ObsWebSocketClient client = CreateClient();
        await client.ConnectAsync();
        Assert.IsTrue(client.IsConnected);

        GetInputSettingsResponseData? response;
        try
        {
            response = await client.GetInputSettingsAsync(
                new GetInputSettingsRequestData(s_testOptions.TestInputName!)
            );
        }
        catch (ObsWebSocketException ex) when (ex.Message.Contains("ResourceNotFound"))
        {
            Assert.Inconclusive(
                $"Required input '{s_testOptions.TestInputName}' not found in OBS. Error: {ex.Message}"
            );
            return;
        }
        catch (ObsWebSocketException ex) when (ex.Message.Contains("InvalidInputKind")) // Might happen if the input isn't what we expect
        {
            Assert.Inconclusive(
                $"Input '{s_testOptions.TestInputName}' is not the expected kind for this test. Error: {ex.Message}"
            );
            return;
        }

        Assert.IsNotNull(response, "GetInputSettings response was null.");
        Assert.IsNotNull(response.InputSettings, "InputSettings (JsonElement?) was null.");
        Assert.AreEqual(
            "text_gdiplus_v3",
            response.InputKind,
            "Expected input kind 'text_gdiplus_v3'."
        ); // Verify kind

        // Demonstrate deserializing the JsonElement
        JsonElement settingsElement = response.InputSettings.Value;
        Assert.AreEqual(
            JsonValueKind.Object,
            settingsElement.ValueKind,
            "Expected settings to be a JSON object."
        );

        try
        {
            // Deserialize into a known structure for Text GDI+ v2
            TextGdiPlusSettings? textSettings = settingsElement.Deserialize<TextGdiPlusSettings>(
                TestUtils.s_jsonSerializerOptions
            );
            Assert.IsNotNull(textSettings, "Failed to deserialize settings element.");
            Assert.IsNotNull(textSettings.Text, "Expected 'text' property in settings.");
            Trace.WriteLine($"Text GDI+ Settings 'text' property: {textSettings.Text}");
            Assert.IsNotNull(textSettings.Font, "Expected 'font' property in settings.");
            Trace.WriteLine($"Text GDI+ Settings 'font.face': {textSettings.Font.Face}");
        }
        catch (JsonException ex)
        {
            Assert.Fail(
                $"Failed to deserialize JsonElement: {ex.Message}. Raw JSON: {settingsElement.GetRawText()}"
            );
        }
    }

    [TestMethod, TestCategory("Integration")]
    [Timeout(DefaultTimeoutMs)]
    public async Task GetSourceFilterList_ReturnsFilterStubs()
    {
        /* OBS Setup: Requires TestInputName source with a filter named TestFilterName. */
        await using ObsWebSocketClient client = CreateClient();
        await client.ConnectAsync();
        Assert.IsTrue(client.IsConnected);

        GetSourceFilterListResponseData? response;
        try
        {
            response = await client.GetSourceFilterListAsync(
                new GetSourceFilterListRequestData(s_testOptions.TestInputName!)
            );
        }
        catch (ObsWebSocketException ex) when (ex.Message.Contains("ResourceNotFound"))
        {
            Assert.Inconclusive(
                $"Required input '{s_testOptions.TestInputName}' not found in OBS. Error: {ex.Message}"
            );
            return;
        }

        Assert.IsNotNull(response, "GetSourceFilterList response was null.");
        Assert.IsNotNull(response.Filters, "Filters list was null.");

        FilterStub? testFilter = response.Filters.FirstOrDefault(f =>
            f.FilterName == s_testOptions.TestFilterName
        );
        Assert.IsNotNull(
            testFilter,
            $"Test filter '{s_testOptions.TestFilterName}' not found on source '{s_testOptions.TestInputName}'. Ensure it exists."
        );
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(testFilter.FilterKind),
            "Filter kind should not be empty."
        );
        Assert.IsNotNull(testFilter.FilterIndex, "Filter index should have a value.");
        Assert.IsNotNull(testFilter.FilterEnabled, "Filter enabled should have a value.");
        Trace.WriteLine(
            $"Found Test Filter Stub: Name={testFilter.FilterName}, Kind={testFilter.FilterKind}, Index={testFilter.FilterIndex}, Enabled={testFilter.FilterEnabled}"
        );
        if (testFilter.FilterSettings.HasValue)
        {
            Trace.WriteLine($"  Filter Settings: {testFilter.FilterSettings.Value.GetRawText()}");
        }

        if (testFilter.ExtensionData != null && testFilter.ExtensionData.Count > 0)
        {
            Trace.WriteLine(
                $"  ExtensionData: {JsonSerializer.Serialize(testFilter.ExtensionData)}"
            );
        }
    }

    [TestMethod, TestCategory("Integration")]
    [Timeout(DefaultTimeoutMs)]
    public async Task GetTransitionList_ReturnsTransitionStubs()
    {
        /* OBS Setup: Requires OBS running with WebSocket enabled. */
        await using ObsWebSocketClient client = CreateClient();
        await client.ConnectAsync();
        Assert.IsTrue(client.IsConnected);

        GetSceneTransitionListResponseData? response = await client.GetSceneTransitionListAsync();

        Assert.IsNotNull(response, "GetSceneTransitionList response was null.");
        Assert.IsNotNull(response.Transitions, "Transitions list was null.");
        Assert.IsTrue(response.Transitions.Count > 0, "Expected at least one transition.");
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(response.CurrentSceneTransitionName),
            "Current transition name should not be empty."
        );

        TransitionStub? firstTransition = response.Transitions.FirstOrDefault();
        Assert.IsNotNull(firstTransition, "First transition stub was null.");
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(firstTransition.TransitionName),
            "Transition name should not be empty."
        );
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(firstTransition.TransitionKind),
            "Transition kind should not be empty."
        );
        Assert.IsNotNull(
            firstTransition.TransitionConfigurable,
            "Transition configurable flag should exist."
        );
        Assert.IsNotNull(firstTransition.TransitionFixed, "Transition fixed flag should exist.");
        Trace.WriteLine($"Current Transition: {response.CurrentSceneTransitionName}");
        Trace.WriteLine(
            $"First Transition Stub: Name={firstTransition.TransitionName}, Kind={firstTransition.TransitionKind}"
        );
    }

    [TestMethod, TestCategory("Integration")]
    [Timeout(DefaultTimeoutMs)]
    public async Task GetOutputList_ReturnsOutputStubs()
    {
        /* OBS Setup: Requires OBS running with WebSocket enabled. */
        await using ObsWebSocketClient client = CreateClient();
        await client.ConnectAsync();
        Assert.IsTrue(client.IsConnected);

        GetOutputListResponseData? response = await client.GetOutputListAsync();

        Assert.IsNotNull(response, "GetOutputList response was null.");
        Assert.IsNotNull(response.Outputs, "Outputs list was null.");
        Assert.IsTrue(
            response.Outputs.Count > 0,
            "Expected at least one output (e.g., Simple Output or Advanced)."
        );

        OutputStub? firstOutput = response.Outputs.FirstOrDefault();
        Assert.IsNotNull(firstOutput, "First output stub was null.");
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(firstOutput.OutputName),
            "Output name should not be empty."
        );
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(firstOutput.OutputKind),
            "Output kind should not be empty."
        );
        Assert.IsNotNull(firstOutput.OutputActive, "Output active flag should exist.");
        // Note: Width/Height/Settings might be null depending on the output type and state
        Trace.WriteLine(
            $"First Output Stub: Name={firstOutput.OutputName}, Kind={firstOutput.OutputKind}, Active={firstOutput.OutputActive}"
        );
    }

    [TestMethod, TestCategory("Integration")]
    [Timeout(DefaultTimeoutMs)]
    public async Task GetMonitorList_ReturnsMonitorStubs()
    {
        /* OBS Setup: Requires OBS running with WebSocket enabled. Result depends on connected monitors. */
        await using ObsWebSocketClient client = CreateClient();
        await client.ConnectAsync();
        Assert.IsTrue(client.IsConnected);

        GetMonitorListResponseData? response = await client.GetMonitorListAsync();

        Assert.IsNotNull(response, "GetMonitorList response was null.");
        Assert.IsNotNull(response.Monitors, "Monitors list was null.");

        // Cannot assert count > 0 as user might have no monitors, but list should exist.
        if (response.Monitors.Count > 0)
        {
            MonitorStub? firstMonitor = response.Monitors.FirstOrDefault();
            Assert.IsNotNull(firstMonitor, "First monitor stub was null if list is not empty.");
            Assert.IsFalse(
                string.IsNullOrWhiteSpace(firstMonitor.MonitorName),
                "Monitor name should not be empty."
            );
            Assert.IsNotNull(firstMonitor.MonitorIndex, "Monitor index should exist.");
            Assert.IsNotNull(firstMonitor.MonitorWidth, "Monitor width should exist.");
            Assert.IsNotNull(firstMonitor.MonitorHeight, "Monitor height should exist.");
            Assert.IsNotNull(firstMonitor.MonitorPositionX, "Monitor position X should exist.");
            Assert.IsNotNull(firstMonitor.MonitorPositionY, "Monitor position Y should exist.");
            Trace.WriteLine(
                $"First Monitor Stub: Name={firstMonitor.MonitorName}, Index={firstMonitor.MonitorIndex}, Res=({firstMonitor.MonitorWidth}x{firstMonitor.MonitorHeight})"
            );
        }
        else
        {
            Trace.WriteLine("No monitors detected.");
        }
    }

    [TestMethod, TestCategory("Integration")]
    [Timeout(EventTimeoutMs)] // Allow more time for manual scene change
    public async Task Event_SceneListChanged_ReceivesStubList()
    {
        /* OBS Setup: Requires OBS running with WebSocket enabled. */
        /* Test Action: Manually create OR delete a scene in OBS UI *after* the test connects. */
        await using ObsWebSocketClient client = CreateClient();
        TaskCompletionSource<SceneListChangedEventArgs> tcs = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        SceneListChangedEventArgs? receivedArgs = null;

        client.SceneListChanged += (sender, args) =>
        {
            Trace.WriteLine($"--> Integration Test: Received SceneListChanged event.");
            receivedArgs = args;
            tcs.TrySetResult(args); // Signal that the event was received
        };

        await client.ConnectAsync();
        Assert.IsTrue(client.IsConnected, "Client failed to connect.");

        Console.WriteLine(
            $"\n>>> INTERACTION REQUIRED: Please manually CREATE or DELETE a scene in OBS now (within 10 seconds)!"
        );

        // Wait for the event to be fired
        bool eventReceived = await Task.WhenAny(tcs.Task, Task.Delay(10000)) == tcs.Task; // 10 second timeout

        // Assert
        Assert.IsTrue(
            eventReceived,
            "Did not receive the SceneListChanged event within the timeout. Did you create/delete a scene in OBS?"
        );
        Assert.IsNotNull(receivedArgs, "Received event arguments were null.");
        Assert.IsNotNull(receivedArgs.EventData.Scenes, "EventData.Scenes list was null.");
        Assert.IsTrue(
            receivedArgs.EventData.Scenes.Count >= 0,
            "Scene list should exist (can be empty after deleting last scene)."
        );

        if (receivedArgs.EventData.Scenes.Count > 0)
        {
            SceneStub firstStub = receivedArgs.EventData.Scenes[0];
            Assert.IsFalse(
                string.IsNullOrWhiteSpace(firstStub.SceneName),
                "First scene stub's name should not be empty."
            );
            Assert.IsFalse(
                string.IsNullOrWhiteSpace(firstStub.SceneUuid),
                "First scene stub's UUID should not be empty."
            );
            Assert.IsTrue(
                firstStub.SceneIndex >= 0,
                "First scene stub's index should be non-negative."
            );
            Trace.WriteLine(
                $"SceneListChanged event verified. First scene: Name='{firstStub.SceneName}', Index={firstStub.SceneIndex}"
            );
        }
        else
        {
            Trace.WriteLine("SceneListChanged event verified with an empty scene list.");
        }
    }

    [TestMethod, TestCategory("Integration")]
    [Timeout(DefaultTimeoutMs)]
    public async Task GetInputDefaultSettings_ReturnsJsonElement_CanDeserialize()
    {
        /* OBS Setup: Requires OBS running with WebSocket enabled. */
        string inputKind = "text_gdiplus_v3"; // Use a known input kind
        await using ObsWebSocketClient client = CreateClient();
        await client.ConnectAsync();
        Assert.IsTrue(client.IsConnected);

        GetInputDefaultSettingsResponseData? response;
        try
        {
            response = await client.GetInputDefaultSettingsAsync(
                new GetInputDefaultSettingsRequestData(inputKind)
            );
        }
        catch (ObsWebSocketException ex) when (ex.Message.Contains("InvalidInputKind"))
        {
            Assert.Inconclusive(
                $"Input kind '{inputKind}' not recognized by OBS. Error: {ex.Message}"
            );
            return;
        }
        catch (ObsWebSocketException ex) when (ex.Message.Contains("ResourceNotFound")) // Might happen if kind doesn't exist?
        {
            Assert.Inconclusive($"Input kind '{inputKind}' not found by OBS. Error: {ex.Message}");
            return;
        }

        Assert.IsNotNull(response, "GetInputDefaultSettings response was null.");
        Assert.IsNotNull(
            response.DefaultInputSettings,
            "DefaultInputSettings (JsonElement?) was null."
        );

        JsonElement settingsElement = response.DefaultInputSettings.Value;
        Assert.AreEqual(
            JsonValueKind.Object,
            settingsElement.ValueKind,
            "Expected default settings to be a JSON object."
        );

        // Demonstrate deserializing the JsonElement into a Dictionary
        try
        {
            Dictionary<string, JsonElement>? defaultSettingsDict = settingsElement.Deserialize<
                Dictionary<string, JsonElement>
            >(TestUtils.s_jsonSerializerOptions);
            Assert.IsNotNull(
                defaultSettingsDict,
                "Failed to deserialize default settings JsonElement."
            );

            // Assert some known default properties for text_gdiplus_v3 exist
            Assert.IsTrue(
                defaultSettingsDict.ContainsKey("font"),
                "Expected 'font' default setting."
            );
            Assert.AreEqual(
                JsonValueKind.Object,
                defaultSettingsDict["font"].ValueKind,
                "'font' setting should be an object."
            );
            Trace.WriteLine(
                $"Successfully deserialized default settings for '{inputKind}'. Found 'text' and 'font'."
            );
            Trace.WriteLine($"Raw Default Settings JSON: {settingsElement.GetRawText()}");
        }
        catch (JsonException ex)
        {
            Assert.Fail(
                $"Failed to deserialize default settings JsonElement: {ex.Message}. Raw JSON: {settingsElement.GetRawText()}"
            );
        }
    }

    // --- Helper Record for Text GDI+ settings deserialization ---
    private record TextGdiPlusSettings(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("font")] FontStub? Font
    );

    private record FontStub(
        [property: JsonPropertyName("face")] string? Face,
        [property: JsonPropertyName("size")] int? Size,
        [property: JsonPropertyName("style")] string? Style,
        [property: JsonPropertyName("flags")] int? Flags
    );
}
