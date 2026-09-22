using System.Text.Json;
using System.Text.Json.Serialization;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Protocol.Common.FilterSettings;
using ObsWebSocket.Core.Protocol.Common.InputSettings;
using ObsWebSocket.Core.Protocol.Common.StreamServiceSettings;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol.Responses;
using ObsWebSocket.Tests.Fakes;

namespace ObsWebSocket.Tests;

/// <summary>
/// The hand written group helpers, driven over the in-memory OBS. These wrap one or more requests
/// and sometimes wait for an event, which is the part that silently stops working.
/// </summary>
[TestClass]
public sealed class GroupHelperTests
{
    private const int TestTimeout = 30_000;

    private const string SceneList =
        """{"currentProgramSceneName":"Live","currentProgramSceneUuid":"p","currentPreviewSceneName":"Next","currentPreviewSceneUuid":"n","scenes":[{"sceneName":"Live","sceneUuid":"p","sceneIndex":0},{"sceneName":"Standby","sceneUuid":"s","sceneIndex":1}]}""";

    private static FakeObsServer ServerWithScenes()
    {
        FakeObsServer server = new();
        _ = server.Returns("GetSceneList", SceneList);
        return server;
    }

    #region Scenes

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SceneExistsAsync_Name_AnswersFromSceneList()
    {
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(ServerWithScenes());

        Assert.IsTrue(await fake.Client.Scenes.SceneExistsAsync("Standby"));
        Assert.IsFalse(await fake.Client.Scenes.SceneExistsAsync("Absent"));
        _ = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            fake.Client.Scenes.SceneExistsAsync("")
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SwitchProgramSceneAsync_WithTransition_SetsTransitionFirst()
    {
        FakeObsServer server = ServerWithScenes();
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        await fake.Client.Scenes.SwitchProgramSceneAsync("Standby", "Fade", 300);

        CollectionAssert.AreEqual(
            new[]
            {
                "SetCurrentSceneTransition",
                "SetCurrentSceneTransitionDuration",
                "SetCurrentProgramScene",
            },
            server.Requests.ToList()
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SwitchPreviewSceneAsync_Scene_SendsPreviewRequest()
    {
        FakeObsServer server = ServerWithScenes();
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        await fake.Client.Scenes.SwitchPreviewSceneAsync("Standby");

        CollectionAssert.Contains(server.Requests.ToList(), "SetCurrentPreviewScene");
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SwitchProgramSceneAndWaitAsync_Confirmed_Returns()
    {
        FakeObsServer server = ServerWithScenes();
        _ = server.OnRequest(
            "SetCurrentProgramScene",
            _ =>
            {
                server.RaiseEvent(
                    "CurrentProgramSceneChanged",
                    """{"sceneName":"Standby","sceneUuid":"s"}"""
                );
                return FakeObsServer.RequestOutcome.Success();
            }
        );

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        await fake.Client.Scenes.SwitchProgramSceneAndWaitAsync("Standby");
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SwitchPreviewSceneAndWaitAsync_Confirmed_Returns()
    {
        FakeObsServer server = ServerWithScenes();
        _ = server.OnRequest(
            "SetCurrentPreviewScene",
            _ =>
            {
                server.RaiseEvent(
                    "CurrentPreviewSceneChanged",
                    """{"sceneName":"Standby","sceneUuid":"s"}"""
                );
                return FakeObsServer.RequestOutcome.Success();
            }
        );

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        await fake.Client.Scenes.SwitchPreviewSceneAndWaitAsync("Standby");
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SwitchProgramSceneAndWaitAsync_AlreadyActive_SendsNothing()
    {
        FakeObsServer server = ServerWithScenes();
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        // OBS raises no event for a switch that changes nothing, so the wait would hang.
        await fake.Client.Scenes.SwitchProgramSceneAndWaitAsync("Live");

        Assert.DoesNotContain("SetCurrentProgramScene", server.Requests.ToList());
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SwitchProgramSceneAndWaitAsync_NoConfirmation_ThrowsTimeout()
    {
        FakeObsServer server = ServerWithScenes();
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        _ = await Assert.ThrowsExactlyAsync<ObsWebSocketTimeoutException>(() =>
            fake.Client.Scenes.SwitchProgramSceneAndWaitAsync(
                "Standby",
                TimeSpan.FromMilliseconds(200)
            )
        );
    }

    #endregion

    #region Outputs, record and stream

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SetVirtualCamActiveAndWaitAsync_Confirmed_ReturnsEventState()
    {
        FakeObsServer server = new();
        _ = server.OnRequest(
            "StartVirtualCam",
            _ =>
            {
                server.RaiseEvent("VirtualcamStateChanged", """{"outputActive":true}""");
                return FakeObsServer.RequestOutcome.Success();
            }
        );

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        Assert.IsTrue(await fake.Client.Outputs.SetVirtualCamActiveAndWaitAsync(activate: true));
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SetVirtualCamActiveAndWaitAsync_NoConfirmation_ReturnsNull()
    {
        FakeObsServer server = new();
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        Assert.IsNull(
            await fake.Client.Outputs.SetVirtualCamActiveAndWaitAsync(
                activate: false,
                TimeSpan.FromMilliseconds(200)
            )
        );
        CollectionAssert.Contains(server.Requests.ToList(), "StopVirtualCam");
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task IsVirtualCamActiveAsync_Active_ReturnsTrue()
    {
        FakeObsServer server = new();
        _ = server.Returns("GetVirtualCamStatus", """{"outputActive":true}""");

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        Assert.IsTrue(await fake.Client.Outputs.IsVirtualCamActiveAsync());
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SetRecordActiveAndWaitAsync_Confirmed_ReturnsEventState()
    {
        FakeObsServer server = new();
        _ = server.OnRequest(
            "StartRecord",
            _ =>
            {
                server.RaiseEvent(
                    "RecordStateChanged",
                    """{"outputActive":true,"outputState":"OBS_WEBSOCKET_OUTPUT_STARTED"}"""
                );
                return FakeObsServer.RequestOutcome.Success();
            }
        );
        _ = server.Returns(
            "GetRecordStatus",
            """{"outputActive":true,"outputPaused":false,"outputTimecode":"00:00:01.000","outputDuration":1000,"outputBytes":1}"""
        );

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        Assert.AreEqual(
            OutputState.Started,
            await fake.Client.Record.SetRecordActiveAndWaitAsync(activate: true)
        );
        Assert.IsTrue(await fake.Client.Record.IsRecordActiveAsync());
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SetRecordActiveAndWaitAsync_NoConfirmation_ReturnsNull()
    {
        FakeObsServer server = new();
        _ = server.Returns("StopRecord", """{"outputPath":"C:/clips/one.mkv"}""");

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        Assert.IsNull(
            await fake.Client.Record.SetRecordActiveAndWaitAsync(
                activate: false,
                TimeSpan.FromMilliseconds(200)
            )
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SetStreamActiveAndWaitAsync_Confirmed_ReturnsEventState()
    {
        FakeObsServer server = new();
        _ = server.OnRequest(
            "StartStream",
            _ =>
            {
                server.RaiseEvent(
                    "StreamStateChanged",
                    """{"outputActive":true,"outputState":"OBS_WEBSOCKET_OUTPUT_STARTED"}"""
                );
                return FakeObsServer.RequestOutcome.Success();
            }
        );
        _ = server.Returns(
            "GetStreamStatus",
            """{"outputActive":true,"outputReconnecting":false,"outputTimecode":"00:00:01.000","outputDuration":1000,"outputCongestion":0,"outputBytes":1,"outputSkippedFrames":0,"outputTotalFrames":1}"""
        );

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        Assert.AreEqual(
            OutputState.Started,
            await fake.Client.Stream.SetStreamActiveAndWaitAsync(activate: true)
        );
        Assert.IsTrue(await fake.Client.Stream.IsStreamActiveAsync());
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SetStreamActiveAndWaitAsync_NoConfirmation_ReturnsNull()
    {
        FakeObsServer server = new();
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        Assert.IsNull(
            await fake.Client.Stream.SetStreamActiveAndWaitAsync(
                activate: false,
                TimeSpan.FromMilliseconds(200)
            )
        );
        CollectionAssert.Contains(server.Requests.ToList(), "StopStream");
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task OutputSettingsAsync_TypedHelpers_RoundTrip()
    {
        FakeObsServer server = new();
        _ = server.Returns(
            "GetOutputSettings",
            """{"outputSettings":{"path":"C:/clips","format_name":"mkv"}}"""
        );
        JsonElement? sent = null;
        _ = server.OnRequest(
            "SetOutputSettings",
            data =>
            {
                sent = data?.GetProperty("outputSettings");
                return FakeObsServer.RequestOutcome.Success();
            }
        );

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        RecordOutputSettings? read = await fake.Client.Outputs.GetOutputSettingsAsync(
            "adv_file_output",
            HelperSettingsContext.Default.RecordOutputSettings
        );
        Assert.AreEqual("C:/clips", read?.Path);

        await fake.Client.Outputs.SetOutputSettingsAsync(
            "adv_file_output",
            new RecordOutputSettings("D:/clips"),
            HelperSettingsContext.Default.RecordOutputSettings
        );
        Assert.AreEqual("D:/clips", sent?.GetProperty("path").GetString());
    }

    #endregion

    #region Inputs and filters

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SetInputTextAsync_Text_SendsTextSettings()
    {
        FakeObsServer server = new();
        JsonElement? sent = null;
        _ = server.OnRequest(
            "SetInputSettings",
            data =>
            {
                sent = data?.GetProperty("inputSettings");
                return FakeObsServer.RequestOutcome.Success();
            }
        );

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        await fake.Client.Inputs.SetInputTextAsync("Caption", "on air");

        Assert.AreEqual("on air", sent?.GetProperty("text").GetString());
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SetInputMutesAsync_Several_ReturnsOneResultEach()
    {
        FakeObsServer server = new();
        int call = 0;
        _ = server.OnRequest(
            "SetInputMute",
            _ =>
                ++call == 2
                    ? FakeObsServer.RequestOutcome.Failure((int)RequestStatusCode.ResourceNotFound)
                    : FakeObsServer.RequestOutcome.Success()
        );

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        BatchResults results = await fake.Client.Inputs.SetInputMutesAsync([
            ("Mic", true),
            ("Desktop", false),
            ("Music", true),
        ]);

        Assert.HasCount(3, results);
        Assert.IsTrue(results[0].RequestStatus.Result);
        Assert.IsFalse(results[1].RequestStatus.Result);
        Assert.IsTrue(results[2].RequestStatus.Result);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SetInputMutesAsync_Empty_SendsNothing()
    {
        FakeObsServer server = new();
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        BatchResults results = await fake.Client.Inputs.SetInputMutesAsync([]);

        Assert.IsEmpty(results);
        Assert.IsEmpty(server.Requests);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task InputSettingsAsync_TypedHelpers_RoundTrip()
    {
        FakeObsServer server = new();
        _ = server.Returns(
            "GetInputSettings",
            """{"inputSettings":{"url":"https://example.com","width":1920},"inputKind":"browser_source"}"""
        );
        _ = server.Returns(
            "GetInputDefaultSettings",
            """{"defaultInputSettings":{"url":"https://obsproject.com","width":800}}"""
        );
        JsonElement? sent = null;
        _ = server.OnRequest(
            "SetInputSettings",
            data =>
            {
                sent = data?.GetProperty("inputSettings");
                return FakeObsServer.RequestOutcome.Success();
            }
        );
        _ = server.Returns(
            "CreateInput",
            """{"inputUuid":"00000000-0000-0000-0000-0000000000ff","sceneItemId":3}"""
        );

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        BrowserSourceSettings? read =
            await fake.Client.Inputs.GetInputSettingsAsync<BrowserSourceSettings>("Web");
        Assert.AreEqual("https://example.com", read?.Url);

        BrowserSourceSettings? defaults =
            await fake.Client.Inputs.GetInputDefaultSettingsAsync<BrowserSourceSettings>(
                "browser_source"
            );
        Assert.AreEqual("https://obsproject.com", defaults?.Url);

        await fake.Client.Inputs.SetInputSettingsAsync(
            "Web",
            new BrowserSourceSettings(Url: "https://example.net")
        );
        Assert.AreEqual("https://example.net", sent?.GetProperty("url").GetString());

        CreateInputResponseData? created = await fake.Client.Inputs.CreateInputAsync(
            "browser_source",
            "Web 2",
            new BrowserSourceSettings(Url: "https://example.org"),
            sceneName: "Live"
        );
        Assert.AreEqual(3, created?.SceneItemId);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task FilterSettingsAsync_TypedHelpers_RoundTrip()
    {
        FakeObsServer server = new();
        _ = server.Returns(
            "GetSourceFilterDefaultSettings",
            """{"defaultFilterSettings":{"opacity":1.0}}"""
        );
        _ = server.Returns(
            "GetSourceFilter",
            """{"filterEnabled":true,"filterIndex":0,"filterKind":"color_filter_v2","filterSettings":{"opacity":0.5}}"""
        );
        JsonElement? sentSettings = null;
        _ = server.OnRequest(
            "SetSourceFilterSettings",
            data =>
            {
                sentSettings = data?.GetProperty("filterSettings");
                return FakeObsServer.RequestOutcome.Success();
            }
        );
        JsonElement? createdSettings = null;
        _ = server.OnRequest(
            "CreateSourceFilter",
            data =>
            {
                createdSettings = data?.GetProperty("filterSettings");
                return FakeObsServer.RequestOutcome.Success();
            }
        );

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        ColorCorrectionFilterSettings? read =
            await fake.Client.Filters.GetSourceFilterSettingsAsync<ColorCorrectionFilterSettings>(
                "Cam",
                "Grade"
            );
        Assert.AreEqual(0.5, read?.Opacity);

        ColorCorrectionFilterSettings? defaults =
            await fake.Client.Filters.GetSourceFilterDefaultSettingsAsync<ColorCorrectionFilterSettings>(
                "color_filter_v2"
            );
        Assert.AreEqual(1.0, defaults?.Opacity);

        await fake.Client.Filters.SetSourceFilterSettingsAsync(
            "Cam",
            "Grade",
            new ColorCorrectionFilterSettings(Opacity: 0.25)
        );
        Assert.AreEqual(0.25, sentSettings?.GetProperty("opacity").GetDouble());

        await fake.Client.Filters.CreateSourceFilterAsync(
            "Cam",
            "Grade 2",
            "color_filter_v2",
            new ColorCorrectionFilterSettings(Opacity: 0.75)
        );
        Assert.AreEqual(0.75, createdSettings?.GetProperty("opacity").GetDouble());
    }

    #endregion

    #region Config, transitions, general and sources

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task EnsureActiveAsync_AlreadyActive_SwitchesOnlyWhenNeeded()
    {
        FakeObsServer server = new();
        _ = server.Returns(
            "GetSceneCollectionList",
            """{"currentSceneCollectionName":"Show","sceneCollections":["Show","Other"]}"""
        );
        _ = server.Returns(
            "GetProfileList",
            """{"currentProfileName":"Stream","profiles":["Stream","Local"]}"""
        );

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        Assert.IsTrue(await fake.Client.Config.EnsureSceneCollectionActiveAsync("Show"));
        Assert.IsTrue(await fake.Client.Config.EnsureProfileActiveAsync("Stream"));
        Assert.DoesNotContain("SetCurrentSceneCollection", server.Requests.ToList());
        Assert.DoesNotContain("SetCurrentProfile", server.Requests.ToList());

        Assert.IsTrue(await fake.Client.Config.EnsureSceneCollectionActiveAsync("Other"));
        Assert.IsTrue(await fake.Client.Config.EnsureProfileActiveAsync("Local"));
        CollectionAssert.Contains(server.Requests.ToList(), "SetCurrentSceneCollection");
        CollectionAssert.Contains(server.Requests.ToList(), "SetCurrentProfile");
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    [DataRow((int)RequestStatusCode.ResourceNotFound)]
    [DataRow((int)RequestStatusCode.InvalidRequestField)]
    public async Task EnsureActiveAsync_UnknownName_ReturnsFalse(int code)
    {
        FakeObsServer server = new();
        _ = server.Returns(
            "GetSceneCollectionList",
            """{"currentSceneCollectionName":"Show","sceneCollections":["Show"]}"""
        );
        _ = server.Returns(
            "GetProfileList",
            """{"currentProfileName":"Stream","profiles":["Stream"]}"""
        );
        _ = server.Fails("SetCurrentSceneCollection", code);
        _ = server.Fails("SetCurrentProfile", code);

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        Assert.IsFalse(await fake.Client.Config.EnsureSceneCollectionActiveAsync("Absent"));
        Assert.IsFalse(await fake.Client.Config.EnsureProfileActiveAsync("Absent"));
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task StreamServiceSettingsAsync_TypedHelpers_RoundTrip()
    {
        FakeObsServer server = new();
        _ = server.Returns(
            "GetStreamServiceSettings",
            """{"streamServiceType":"rtmp_common","streamServiceSettings":{"server":"auto","key":"live_1"}}"""
        );
        JsonElement? sent = null;
        _ = server.OnRequest(
            "SetStreamServiceSettings",
            data =>
            {
                sent = data?.GetProperty("streamServiceSettings");
                return FakeObsServer.RequestOutcome.Success();
            }
        );

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        RtmpCommonStreamServiceSettings? read =
            await fake.Client.Config.GetStreamServiceSettingsAsync<RtmpCommonStreamServiceSettings>();
        Assert.AreEqual("live_1", read?.Key);

        await fake.Client.Config.SetStreamServiceSettingsAsync(
            "rtmp_common",
            new RtmpCommonStreamServiceSettings(Server: "auto", Key: "live_2")
        );
        Assert.AreEqual("live_2", sent?.GetProperty("key").GetString());
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task TransitionSettingsAsync_TypedHelpers_RoundTrip()
    {
        FakeObsServer server = new();
        _ = server.Returns(
            "GetCurrentSceneTransition",
            """{"transitionName":"Swipe","transitionUuid":"t","transitionKind":"swipe_transition","transitionFixed":false,"transitionConfigurable":true,"transitionSettings":{"direction":"left"}}"""
        );
        JsonElement? sent = null;
        _ = server.OnRequest(
            "SetCurrentSceneTransitionSettings",
            data =>
            {
                sent = data?.GetProperty("transitionSettings");
                return FakeObsServer.RequestOutcome.Success();
            }
        );

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        SwipeTransitionSettings? read =
            await fake.Client.Transitions.GetCurrentSceneTransitionSettingsAsync(
                HelperSettingsContext.Default.SwipeTransitionSettings
            );
        Assert.AreEqual("left", read?.Direction);

        await fake.Client.Transitions.SetCurrentSceneTransitionSettingsAsync(
            new SwipeTransitionSettings("right"),
            HelperSettingsContext.Default.SwipeTransitionSettings
        );
        Assert.AreEqual("right", sent?.GetProperty("direction").GetString());
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task TriggerHotkeyAsync_Name_SendsCanonicalName()
    {
        FakeObsServer server = new();
        string? name = null;
        _ = server.OnRequest(
            "TriggerHotkeyByName",
            data =>
            {
                name = data?.GetProperty("hotkeyName").GetString();
                return FakeObsServer.RequestOutcome.Success();
            }
        );

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        await fake.Client.General.TriggerHotkeyAsync("OBSBasic.StartStreaming");

        Assert.AreEqual("OBSBasic.StartStreaming", name);
        _ = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            fake.Client.General.TriggerHotkeyAsync("")
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SourceExistsAsync_Name_ChecksInputsThenScenes()
    {
        FakeObsServer server = ServerWithScenes();
        _ = server.Returns(
            "GetInputList",
            """{"inputs":[{"inputName":"Mic","inputUuid":"i","inputKind":"wasapi_input_capture","unversionedInputKind":"wasapi_input_capture"}]}"""
        );

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        Assert.IsTrue(await fake.Client.Sources.SourceExistsAsync("Mic"), "an input counts");
        Assert.IsTrue(await fake.Client.Sources.SourceExistsAsync("Live"), "a scene counts too");
        Assert.IsFalse(await fake.Client.Sources.SourceExistsAsync("Absent"));
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetSourceScreenshotBytesAsync_DataUri_ReturnsDecodedBytes()
    {
        byte[] image = [1, 2, 3, 4];
        string encoded = "data:image/png;base64," + Convert.ToBase64String(image);
        FakeObsServer server = new();
        _ = server.Returns("GetSourceScreenshot", $$"""{"imageData":"{{encoded}}"}""");

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        CollectionAssert.AreEqual(
            image,
            await fake.Client.Sources.GetSourceScreenshotBytesAsync("Cam")
        );
        CollectionAssert.AreEqual(
            image,
            await fake.Client.Sources.GetSourceScreenshotOnCanvasBytesAsync("Cam")
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetSourceScreenshotBytesAsync_UnknownSource_ReturnsNull()
    {
        FakeObsServer server = new();
        _ = server.Fails("GetSourceScreenshot", (int)RequestStatusCode.ResourceNotFound);

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        Assert.IsNull(await fake.Client.Sources.GetSourceScreenshotBytesAsync("Absent"));
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SaveSourceScreenshotToFileAsync_Path_SendsPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"obsws-{Guid.NewGuid():N}.png");
        FakeObsServer server = new();
        string? requestedPath = null;
        _ = server.OnRequest(
            "SaveSourceScreenshot",
            data =>
            {
                requestedPath = data?.GetProperty("imageFilePath").GetString();
                return FakeObsServer.RequestOutcome.Success();
            }
        );

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        await fake.Client.Sources.SaveSourceScreenshotToFileAsync("Cam", path);

        Assert.AreEqual(path, requestedPath);
    }

    #endregion

    #region Not connected

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task Helpers_NotConnected_Throw()
    {
        await using FakeObsClient fake = FakeObsClient.Build(new FakeObsServer());
        ObsWebSocketClient client = fake.Client;

        await AssertRefusedAsync(() => client.Scenes.SceneExistsAsync("Live"));
        await AssertRefusedAsync(() => client.Scenes.SwitchProgramSceneAsync("Live"));
        await AssertRefusedAsync(() => client.Sources.SourceExistsAsync("Cam"));
        await AssertRefusedAsync(() => client.Inputs.SetInputTextAsync("Caption", "x"));
        await AssertRefusedAsync(() => client.Inputs.SetInputMutesAsync([("Mic", true)]));
        await AssertRefusedAsync(() =>
            client.General.TriggerHotkeyAsync("OBSBasic.StartStreaming")
        );
        await AssertRefusedAsync(() => client.Record.IsRecordActiveAsync());
        await AssertRefusedAsync(() => client.Stream.IsStreamActiveAsync());
        await AssertRefusedAsync(() => client.Outputs.IsVirtualCamActiveAsync());
        await AssertRefusedAsync(() => client.Config.EnsureProfileActiveAsync("Stream"));
        await AssertRefusedAsync(() => client.Config.EnsureSceneCollectionActiveAsync("Show"));
    }

    private static async Task AssertRefusedAsync(Func<Task> call) =>
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(call);

    #endregion
}

/// <summary>An output settings shape a consumer would define, with no library registration.</summary>
/// <param name="Path">Where the output writes.</param>
internal sealed record RecordOutputSettings(
    [property: JsonPropertyName("path")] string? Path = null
);

/// <summary>A transition settings shape a consumer would define.</summary>
/// <param name="Direction">The direction the swipe runs in.</param>
internal sealed record SwipeTransitionSettings(
    [property: JsonPropertyName("direction")] string? Direction = null
);

[JsonSerializable(typeof(RecordOutputSettings))]
[JsonSerializable(typeof(SwipeTransitionSettings))]
internal sealed partial class HelperSettingsContext : JsonSerializerContext { }
