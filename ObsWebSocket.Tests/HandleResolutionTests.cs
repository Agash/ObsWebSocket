using ObsWebSocket.Core;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Tests.Fakes;

namespace ObsWebSocket.Tests;

/// <summary>
/// Turning names into uuids. A wrong answer here addresses the wrong scene or source, so each
/// kind is checked for the found case, the missing case and its listing of what does exist.
/// </summary>
[TestClass]
public sealed class HandleResolutionTests
{
    private const int TestTimeout = 30_000;

    private const string SceneUuid = "11111111-1111-1111-1111-111111111111";
    private const string InputUuid = "22222222-2222-2222-2222-222222222222";
    private const string CanvasUuid = "33333333-3333-3333-3333-333333333333";

    private static FakeObsServer Server()
    {
        FakeObsServer server = new();
        _ = server.Returns(
            "GetSceneList",
            $$"""{"currentProgramSceneName":"Live","currentProgramSceneUuid":"{{SceneUuid}}","scenes":[{"sceneName":"Live","sceneUuid":"{{SceneUuid}}","sceneIndex":0}]}"""
        );
        _ = server.Returns(
            "GetInputList",
            $$"""{"inputs":[{"inputName":"Mic","inputUuid":"{{InputUuid}}","inputKind":"wasapi_input_capture","unversionedInputKind":"wasapi_input_capture"}]}"""
        );
        _ = server.Returns(
            "GetCanvasList",
            $$$"""{"canvases":[{"canvasName":"Vertical","canvasUuid":"{{{CanvasUuid}}}","canvasFlags":{"MAIN":false,"ACTIVATE":true,"MIX_AUDIO":false,"SCENE_REF":false,"EPHEMERAL":false},"canvasVideoSettings":{"fpsNumerator":30,"fpsDenominator":1,"baseWidth":1080,"baseHeight":1920,"outputWidth":1080,"outputHeight":1920}}]}"""
        );
        return server;
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ResolveAsync_KnownName_ReturnsUuid()
    {
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(Server());
        ObsWebSocketClient client = fake.Client;

        Assert.AreEqual(
            SceneUuid,
            (await client.Scenes.ResolveAsync(SceneHandle.FromName("Live"))).Uuid
        );
        Assert.AreEqual(
            InputUuid,
            (await client.Inputs.ResolveAsync(InputHandle.FromName("Mic"))).Uuid
        );
        Assert.AreEqual(
            CanvasUuid,
            (await client.Canvases.ResolveAsync(CanvasHandle.FromName("Vertical"))).Uuid
        );
        Assert.AreEqual(
            InputUuid,
            (await client.Sources.ResolveAsync(SourceHandle.FromName("Mic"))).Uuid
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ResolveAsync_SourceIsScene_ChecksInputsThenScenes()
    {
        FakeObsServer server = Server();
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        SourceHandle resolved = await fake.Client.Sources.ResolveAsync(
            SourceHandle.FromName("Live")
        );

        Assert.AreEqual(SceneUuid, resolved.Uuid);
        CollectionAssert.AreEqual(
            new[] { "GetInputList", "GetSceneList" },
            server.Requests.ToList()
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ResolveAsync_AlreadyResolved_SendsNothing()
    {
        FakeObsServer server = Server();
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);
        ObsWebSocketClient client = fake.Client;

        SceneHandle scene = SceneHandle.FromUuid(SceneUuid);
        InputHandle input = InputHandle.FromUuid(InputUuid);
        SourceHandle source = SourceHandle.FromUuid(InputUuid);
        CanvasHandle canvas = CanvasHandle.FromUuid(CanvasUuid);

        Assert.AreSame(scene, await client.Scenes.ResolveAsync(scene));
        Assert.AreSame(input, await client.Inputs.ResolveAsync(input));
        Assert.AreSame(source, await client.Sources.ResolveAsync(source));
        Assert.AreSame(canvas, await client.Canvases.ResolveAsync(canvas));
        Assert.AreSame(CanvasHandle.Main, await client.Canvases.ResolveAsync(CanvasHandle.Main));

        Assert.IsEmpty(server.Requests, "nothing needed looking up");
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ResolveAsync_UnknownName_ThrowsListingAvailable()
    {
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(Server());
        ObsWebSocketClient client = fake.Client;

        ObsWebSocketResourceNotFoundException scene =
            await Assert.ThrowsExactlyAsync<ObsWebSocketResourceNotFoundException>(async () =>
                await client.Scenes.ResolveAsync(SceneHandle.FromName("Absent"))
            );
        Assert.AreEqual("scene", scene.Kind);
        Assert.AreEqual("Absent", scene.RequestedName);
        CollectionAssert.AreEqual(new[] { "Live" }, scene.Available.ToList());

        ObsWebSocketResourceNotFoundException input =
            await Assert.ThrowsExactlyAsync<ObsWebSocketResourceNotFoundException>(async () =>
                await client.Inputs.ResolveAsync(InputHandle.FromName("Absent"))
            );
        CollectionAssert.AreEqual(new[] { "Mic" }, input.Available.ToList());

        ObsWebSocketResourceNotFoundException canvas =
            await Assert.ThrowsExactlyAsync<ObsWebSocketResourceNotFoundException>(async () =>
                await client.Canvases.ResolveAsync(CanvasHandle.FromName("Absent"))
            );
        CollectionAssert.AreEqual(new[] { "Vertical" }, canvas.Available.ToList());

        ObsWebSocketResourceNotFoundException source =
            await Assert.ThrowsExactlyAsync<ObsWebSocketResourceNotFoundException>(async () =>
                await client.Sources.ResolveAsync(SourceHandle.FromName("Absent"))
            );
        CollectionAssert.AreEquivalent(new[] { "Mic", "Live" }, source.Available.ToList());
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ResolveAsync_KnownSceneItem_ReturnsId()
    {
        FakeObsServer server = Server();
        _ = server.Returns("GetSceneItemId", """{"sceneItemId":7}""");
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        SceneItemHandle item = await fake.Client.SceneItems.ResolveAsync(
            SceneHandle.FromName("Live").Item("Mic")
        );

        Assert.AreEqual(7, item.SceneItemId);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ResolveAsync_UnknownSceneItem_ThrowsListingSceneSources()
    {
        FakeObsServer server = Server();
        _ = server.Fails("GetSceneItemId", (int)RequestStatusCode.ResourceNotFound);
        _ = server.Returns(
            "GetSceneItemList",
            """{"sceneItems":[{"sourceName":"Mic","sceneItemId":1,"sceneItemIndex":0,"sourceType":"OBS_SOURCE_TYPE_INPUT","sourceUuid":"u","sceneItemEnabled":true,"sceneItemLocked":false,"sceneItemBlendMode":"OBS_BLEND_NORMAL","sceneItemTransform":{"positionX":0,"positionY":0,"rotation":0,"scaleX":1,"scaleY":1,"width":0,"height":0,"sourceWidth":0,"sourceHeight":0,"alignment":5,"boundsType":"OBS_BOUNDS_NONE","boundsAlignment":0,"boundsWidth":0,"boundsHeight":0,"cropLeft":0,"cropTop":0,"cropRight":0,"cropBottom":0},"isGroup":null,"inputKind":null}]}"""
        );
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        ObsWebSocketResourceNotFoundException error =
            await Assert.ThrowsExactlyAsync<ObsWebSocketResourceNotFoundException>(async () =>
                await fake.Client.SceneItems.ResolveAsync(
                    SceneHandle.FromName("Live").Item("Camera")
                )
            );

        Assert.AreEqual("Camera", error.RequestedName);
        CollectionAssert.AreEqual(new[] { "Mic" }, error.Available.ToList());
        Assert.IsInstanceOfType<ObsWebSocketRequestException>(error.InnerException);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ResolveAsync_UnknownSceneItemAndScene_ThrowsForItem()
    {
        FakeObsServer server = Server();
        _ = server.Fails("GetSceneItemId", (int)RequestStatusCode.ResourceNotFound);
        _ = server.Fails("GetSceneItemList", (int)RequestStatusCode.ResourceNotFound);
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        ObsWebSocketResourceNotFoundException error =
            await Assert.ThrowsExactlyAsync<ObsWebSocketResourceNotFoundException>(async () =>
                await fake.Client.SceneItems.ResolveAsync(
                    SceneHandle.FromName("Gone").Item("Camera")
                )
            );

        Assert.AreEqual("Camera", error.RequestedName);
        Assert.IsEmpty(error.Available);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ResolveAsync_SceneItemOtherFailure_ThrowsRequestException()
    {
        FakeObsServer server = Server();
        _ = server.Fails("GetSceneItemId", (int)RequestStatusCode.InvalidResourceState);
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        ObsWebSocketRequestException error =
            await Assert.ThrowsExactlyAsync<ObsWebSocketRequestException>(async () =>
                await fake.Client.SceneItems.ResolveAsync(SceneHandle.FromName("Live").Item("Mic"))
            );

        Assert.AreEqual(RequestStatusCode.InvalidResourceState, error.StatusCode);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ResolveAsync_Null_Throws()
    {
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(Server());
        ObsWebSocketClient client = fake.Client;

        _ = await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () =>
            await client.Scenes.ResolveAsync(null!)
        );
        _ = await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () =>
            await client.Inputs.ResolveAsync(null!)
        );
        _ = await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () =>
            await client.Canvases.ResolveAsync(null!)
        );
        _ = await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () =>
            await client.Sources.ResolveAsync(null!)
        );
        _ = await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () =>
            await client.SceneItems.ResolveAsync(null!)
        );
    }
}
