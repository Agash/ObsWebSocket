using ObsWebSocket.Core;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol.Responses;
using ObsWebSocket.Tests.Fakes;

namespace ObsWebSocket.Tests;

/// <summary>
/// The handshake and request path driven end to end over the in-memory OBS: the same code a live
/// connection runs, without a live connection.
/// </summary>
[TestClass]
public sealed class FakeServerHandshakeTests
{
    private const int TestTimeout = 30_000;

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ConnectAsync_NoAuthentication_ConnectsAndServesRequests()
    {
        FakeObsServer server = new();
        _ = server.Returns(
            "GetVersion",
            """{"obsVersion":"32.2.2","obsWebSocketVersion":"5.7.0","rpcVersion":1,"availableRequests":[],"supportedImageFormats":[],"platform":"windows","platformDescription":"Windows 11"}"""
        );

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        Assert.IsTrue(fake.Client.IsConnected);
        Assert.AreEqual(1, fake.Client.NegotiatedRpcVersion);

        Assert.AreEqual("32.2.2", (await fake.Client.General.GetVersionAsync()).ObsVersion);
        CollectionAssert.Contains(server.Requests.ToList(), "GetVersion");
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ConnectAsync_WithPassword_AnswersChallenge()
    {
        FakeObsServer server = new() { Password = "secret" };

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(
            server,
            options => options.Password = "secret"
        );

        Assert.IsTrue(fake.Client.IsConnected);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ConnectAsync_PasswordRequiredButMissing_ThrowsAuthenticationFailure()
    {
        FakeObsServer server = new() { Password = "secret" };
        await using FakeObsClient fake = FakeObsClient.Build(server);

        _ = await Assert.ThrowsExactlyAsync<AuthenticationFailureException>(() =>
            fake.Client.ConnectAsync()
        );
        Assert.IsFalse(fake.Client.IsConnected);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ConnectAsync_IdentifyRejected_ThrowsAuthenticationFailureWithoutRetry()
    {
        FakeObsServer server = new()
        {
            RejectIdentifyWith = (int)WebSocketCloseCode.AuthenticationFailed,
        };
        await using FakeObsClient fake = FakeObsClient.Build(
            server,
            options =>
            {
                options.Password = "wrong";
                options.AutoReconnectEnabled = true;
                options.MaxReconnectAttempts = 5;
                options.InitialReconnectDelayMs = 1;
                options.MaxReconnectDelayMs = 1;
            }
        );

        _ = await Assert.ThrowsExactlyAsync<AuthenticationFailureException>(() =>
            fake.Client.ConnectAsync()
        );

        Assert.IsFalse(fake.Client.IsConnected);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task CallAsync_ObsReportsFailure_ThrowsWithStatus()
    {
        FakeObsServer server = new();
        _ = server.Fails("GetVersion", (int)RequestStatusCode.ResourceNotFound);

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        ObsWebSocketRequestException error =
            await Assert.ThrowsExactlyAsync<ObsWebSocketRequestException>(() =>
                fake.Client.General.GetVersionAsync()
            );

        Assert.AreEqual(RequestStatusCode.ResourceNotFound, error.StatusCode);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task EventDispatch_KnownEvent_RaisesGeneratedEvent()
    {
        FakeObsServer server = new();
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        TaskCompletionSource<string> sceneName = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        fake.Client.CurrentProgramSceneChanged += (_, e) =>
            sceneName.TrySetResult(e.EventData.SceneName);

        server.RaiseEvent(
            "CurrentProgramSceneChanged",
            """{"sceneName":"Live","sceneUuid":"00000000-0000-0000-0000-000000000001"}"""
        );

        Assert.AreEqual("Live", await sceneName.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ReceiveLoop_ServerCloses_Disconnects()
    {
        FakeObsServer server = new();
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Client.Disconnected += (_, _) => disconnected.TrySetResult();

        server.CloseFromServer((int)WebSocketCloseCode.SessionInvalidated);

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(fake.Client.IsConnected);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task CallBatchAsync_MixedResults_ReturnsEveryItemInOrder()
    {
        FakeObsServer server = new();
        _ = server.Returns(
            "GetVersion",
            """{"obsVersion":"32.2.2","obsWebSocketVersion":"5.7.0","rpcVersion":1,"availableRequests":[],"supportedImageFormats":[],"platform":"windows","platformDescription":"Windows 11"}"""
        );
        _ = server.Fails("GetSceneList", (int)RequestStatusCode.ResourceNotFound);

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        List<RequestResponsePayload<object>> results = await fake.Client.CallBatchAsync(
            [new("GetVersion"), new("GetSceneList")],
            haltOnFailure: false
        );

        Assert.HasCount(2, results);
        Assert.AreEqual("GetVersion", results[0].RequestType);
        Assert.IsTrue(results[0].RequestStatus.Result);
        Assert.IsFalse(results[1].RequestStatus.Result);
        Assert.IsFalse(results.AllSucceeded());
        Assert.HasCount(1, results.GetFailures().ToList());
        Assert.AreEqual("32.2.2", results[0].GetRequiredData<GetVersionResponseData>().ObsVersion);
    }
}
