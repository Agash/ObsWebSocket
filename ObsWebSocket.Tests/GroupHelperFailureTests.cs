using System.Text.Json;
using System.Text.Json.Serialization;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Protocol.Common.FilterSettings;
using ObsWebSocket.Core.Protocol.Common.InputSettings;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Tests.Fakes;

namespace ObsWebSocket.Tests;

/// <summary>
/// How the helpers behave when OBS or the caller's types do not cooperate: what they turn into
/// "nothing", what they let through, and what they refuse.
/// </summary>
[TestClass]
public sealed class GroupHelperFailureTests
{
    private const int TestTimeout = 30_000;

    #region Typed settings

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetSettingsAsync_UnknownToObs_ReturnsNull()
    {
        FakeObsServer server = new();
        _ = server.Fails("GetInputSettings", (int)RequestStatusCode.ResourceNotFound);
        _ = server.Fails("GetSourceFilter", (int)RequestStatusCode.ResourceNotFound);
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        Assert.IsNull(
            await fake.Client.Inputs.GetInputSettingsAsync<BrowserSourceSettings>("Absent")
        );
        Assert.IsNull(
            await fake.Client.Filters.GetSourceFilterSettingsAsync<ColorCorrectionFilterSettings>(
                "Cam",
                "Absent"
            )
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetSettingsAsync_WrongShape_ReturnsNull()
    {
        FakeObsServer server = new();
        _ = server.Returns(
            "GetInputSettings",
            """{"inputSettings":{"url":42},"inputKind":"browser_source"}"""
        );
        _ = server.Returns(
            "GetSourceFilter",
            """{"filterEnabled":true,"filterIndex":0,"filterKind":"color_filter_v2","filterSettings":{"opacity":"half"}}"""
        );
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        Assert.IsNull(await fake.Client.Inputs.GetInputSettingsAsync<BrowserSourceSettings>("Web"));
        Assert.IsNull(
            await fake.Client.Filters.GetSourceFilterSettingsAsync<ColorCorrectionFilterSettings>(
                "Cam",
                "Grade"
            )
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetInputSettingsAsync_OtherRefusal_Throws()
    {
        FakeObsServer server = new();
        _ = server.Fails("GetInputSettings", (int)RequestStatusCode.InvalidResourceState);
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        _ = await Assert.ThrowsExactlyAsync<ObsWebSocketRequestException>(() =>
            fake.Client.Inputs.GetInputSettingsAsync<BrowserSourceSettings>("Web")
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SetSettingsAsync_Unserializable_ThrowsBeforeSending()
    {
        FakeObsServer server = new();
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);
        ObsWebSocketClient client = fake.Client;
        BrokenSettings broken = new(new Unwritable());
        var typeInfo = BrokenSettingsContext.Default.BrokenSettings;

        _ = await Assert.ThrowsExactlyAsync<ObsWebSocketException>(() =>
            client.Inputs.SetInputSettingsAsync("Web", broken, typeInfo)
        );
        _ = await Assert.ThrowsExactlyAsync<ObsWebSocketException>(() =>
            client.Inputs.CreateInputAsync(
                "browser_source",
                "Web",
                broken,
                typeInfo,
                sceneName: "Live"
            )
        );
        _ = await Assert.ThrowsExactlyAsync<ObsWebSocketException>(() =>
            client.Filters.SetSourceFilterSettingsAsync("Cam", "Grade", broken, typeInfo)
        );
        _ = await Assert.ThrowsExactlyAsync<ObsWebSocketException>(() =>
            client.Filters.CreateSourceFilterAsync(
                "Cam",
                "Grade",
                "color_filter_v2",
                broken,
                typeInfo
            )
        );

        Assert.IsEmpty(server.Requests);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task TransitionSettingsAsync_UnregisteredType_Throws()
    {
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(new FakeObsServer());

        _ = await Assert.ThrowsExactlyAsync<ObsWebSocketException>(() =>
            fake.Client.Transitions.GetCurrentSceneTransitionSettingsAsync<SwipeTransitionSettings>()
        );
        _ = await Assert.ThrowsExactlyAsync<ObsWebSocketException>(() =>
            fake.Client.Transitions.SetCurrentSceneTransitionSettingsAsync(
                new SwipeTransitionSettings("left")
            )
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetCurrentSceneTransitionSettingsAsync_NoSettings_ReturnsNull()
    {
        FakeObsServer server = new();
        _ = server.Returns(
            "GetCurrentSceneTransition",
            """{"transitionName":"Cut","transitionUuid":"t","transitionKind":"cut_transition","transitionFixed":true,"transitionConfigurable":false,"transitionSettings":null}"""
        );
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        Assert.IsNull(
            await fake.Client.Transitions.GetCurrentSceneTransitionSettingsAsync(
                HelperSettingsContext.Default.SwipeTransitionSettings
            )
        );
    }

    #endregion

    #region Sources

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SourceExistsAsync_ObsRefuses_ReturnsFalse()
    {
        FakeObsServer server = new();
        _ = server.Fails("GetInputList", (int)RequestStatusCode.InvalidResourceState);
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        Assert.IsFalse(await fake.Client.Sources.SourceExistsAsync("Mic"));
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SourceExistsAsync_TimesOut_Throws()
    {
        FakeObsServer server = new();
        _ = server.OnRequest("GetInputList", _ => FakeObsServer.RequestOutcome.NoReply);
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(
            server,
            options => options.RequestTimeoutMs = 200
        );

        // Absent would invite the caller to create it again.
        _ = await Assert.ThrowsExactlyAsync<ObsWebSocketTimeoutException>(() =>
            fake.Client.Sources.SourceExistsAsync("Mic")
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    [DataRow("""{"imageData":""}""")]
    [DataRow("""{"imageData":"data:image/png;base64,***not base64***"}""")]
    public async Task GetSourceScreenshotBytesAsync_NoUsableImage_ReturnsNull(string response)
    {
        FakeObsServer server = new();
        _ = server.Returns("GetSourceScreenshot", response);
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        Assert.IsNull(await fake.Client.Sources.GetSourceScreenshotBytesAsync("Cam"));
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetSourceScreenshotOnCanvasBytesAsync_NoImage_ReturnsEmpty()
    {
        FakeObsServer server = new();
        _ = server.Returns("GetSourceScreenshot", """{"imageData":""}""");
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        Assert.IsEmpty(await fake.Client.Sources.GetSourceScreenshotOnCanvasBytesAsync("Cam"));
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetSourceScreenshotOnCanvasBytesAsync_NoDataUriPrefix_Decodes()
    {
        byte[] image = [9, 8, 7];
        FakeObsServer server = new();
        _ = server.Returns(
            "GetSourceScreenshot",
            $$"""{"imageData":"{{Convert.ToBase64String(image)}}"}"""
        );
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        CollectionAssert.AreEqual(
            image,
            await fake.Client.Sources.GetSourceScreenshotOnCanvasBytesAsync("Cam")
        );
    }

    #endregion

    #region Scenes

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SwitchProgramSceneAndWaitAsync_ObsRefuses_Throws()
    {
        FakeObsServer server = new();
        _ = server.Returns(
            "GetSceneList",
            """{"currentProgramSceneName":"Live","currentProgramSceneUuid":"p","scenes":[]}"""
        );
        _ = server.Fails("SetCurrentProgramScene", (int)RequestStatusCode.ResourceNotFound);
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        ObsWebSocketRequestException error =
            await Assert.ThrowsExactlyAsync<ObsWebSocketRequestException>(() =>
                fake.Client.Scenes.SwitchProgramSceneAndWaitAsync("Absent")
            );
        Assert.AreEqual(RequestStatusCode.ResourceNotFound, error.StatusCode);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SwitchProgramSceneAndWaitAsync_Cancelled_ThrowsOperationCanceled()
    {
        FakeObsServer server = new();
        _ = server.Returns(
            "GetSceneList",
            """{"currentProgramSceneName":"Live","currentProgramSceneUuid":"p","scenes":[]}"""
        );
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(200));

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fake.Client.Scenes.SwitchProgramSceneAndWaitAsync(
                "Standby",
                cancellationToken: cancel.Token
            )
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SwitchSceneAsync_ObsoleteForwarders_StillSwitch()
    {
        FakeObsServer server = new();
        _ = server.Returns(
            "GetSceneList",
            """{"currentProgramSceneName":"Standby","currentProgramSceneUuid":"s","scenes":[]}"""
        );
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

#pragma warning disable CS0618 // The forwarders under test are the obsolete ones.
        await fake.Client.Scenes.SwitchSceneAsync("Live");
        await fake.Client.Scenes.SwitchSceneAndWaitAsync("Standby");
#pragma warning restore CS0618

        CollectionAssert.Contains(server.Requests.ToList(), "SetCurrentProgramScene");
    }

    #endregion
}

/// <summary>A value no serializer can write, standing in for a caller's broken type.</summary>
internal sealed class Unwritable;

/// <summary>Refuses to read or write, as a faulty caller converter would.</summary>
internal sealed class RefusingConverter : JsonConverter<Unwritable>
{
    public override Unwritable Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) => throw new JsonException("cannot read");

    public override void Write(
        Utf8JsonWriter writer,
        Unwritable value,
        JsonSerializerOptions options
    ) => throw new JsonException("cannot write");
}

/// <summary>Settings whose only property cannot be serialized.</summary>
/// <param name="Value">The property that refuses.</param>
internal sealed record BrokenSettings(
    [property: JsonConverter(typeof(RefusingConverter))] Unwritable Value
);

[JsonSerializable(typeof(BrokenSettings))]
internal sealed partial class BrokenSettingsContext : JsonSerializerContext { }
