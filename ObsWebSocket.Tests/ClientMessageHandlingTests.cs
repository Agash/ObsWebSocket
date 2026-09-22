using ObsWebSocket.Core;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol.Responses;
using ObsWebSocket.Tests.Fakes;

namespace ObsWebSocket.Tests;

/// <summary>
/// What the client does with frames it did not ask for or cannot read. A newer OBS, a stray
/// response or a truncated frame must not take the connection down, and a lost connection must
/// come back on its own when that is configured.
/// </summary>
[TestClass]
public sealed class ClientMessageHandlingTests
{
    private const int TestTimeout = 30_000;

    private const string Version =
        """{"obsVersion":"32.2.2","obsWebSocketVersion":"5.7.0","rpcVersion":1,"availableRequests":[],"supportedImageFormats":[],"platform":"windows","platformDescription":"Windows 11"}""";

    private static FakeObsServer ServerWithVersion()
    {
        FakeObsServer server = new();
        _ = server.Returns("GetVersion", Version);
        return server;
    }

    /// <summary>Proves the connection still works after whatever the test pushed.</summary>
    private static async Task AssertStillServingAsync(FakeObsClient fake)
    {
        Assert.IsTrue(fake.Client.IsConnected);
        GetVersionResponseData version = await fake.Client.General.GetVersionAsync();
        Assert.AreEqual("32.2.2", version.ObsVersion);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    [DataRow("not json at all")]
    [DataRow("""{"op":99,"d":{}}""")]
    [DataRow("""{"op":5,"d":null}""")]
    [DataRow("""{"op":5,"d":{"eventIntent":0}}""")]
    [DataRow(
        """{"op":5,"d":{"eventType":"SomethingOnlyANewerObsSends","eventIntent":0,"eventData":{}}}"""
    )]
    [DataRow(
        """{"op":5,"d":{"eventType":"CurrentProgramSceneChanged","eventIntent":0,"eventData":{"sceneName":42}}}"""
    )]
    [DataRow("""{"op":5,"d":{"eventType":"CurrentProgramSceneChanged","eventIntent":0}}""")]
    [DataRow(
        """{"op":7,"d":{"requestType":"GetVersion","requestId":"nobody-asked","requestStatus":{"result":true,"code":100}}}"""
    )]
    [DataRow("""{"op":7,"d":null}""")]
    [DataRow("""{"op":9,"d":{"requestId":"nobody-asked","results":[]}}""")]
    [DataRow("""{"op":9,"d":null}""")]
    [DataRow("""{"op":2,"d":{"negotiatedRpcVersion":1}}""")]
    [DataRow("""{"op":0,"d":{"obsWebSocketVersion":"5.7.0","rpcVersion":1}}""")]
    public async Task ReceiveLoop_UnusableFrame_DropsItAndKeepsServing(string frame)
    {
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(ServerWithVersion());

        fake.Server.PushRaw(frame);

        await AssertStillServingAsync(fake);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task EventDispatch_HandlerThrows_NextEventStillDelivered()
    {
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(ServerWithVersion());

        int delivered = 0;
        TaskCompletionSource second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Client.CurrentProgramSceneChanged += (_, _) =>
        {
            if (Interlocked.Increment(ref delivered) == 1)
            {
                throw new InvalidOperationException("a subscriber bug");
            }

            second.TrySetResult();
        };

        const string payload = """{"sceneName":"Live","sceneUuid":"p"}""";
        fake.Server.RaiseEvent("CurrentProgramSceneChanged", payload);
        fake.Server.RaiseEvent("CurrentProgramSceneChanged", payload);

        await second.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await AssertStillServingAsync(fake);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task CustomEvent_WithBroadcastData_RaisesItsPayload()
    {
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(ServerWithVersion());

        TaskCompletionSource<string> received = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        fake.Client.CustomEvent += (_, e) =>
            received.TrySetResult(e.EventData.EventData?.GetProperty("cue").GetString() ?? "");

        fake.Server.RaiseEvent("CustomEvent", """{"cue":"lights"}""");

        Assert.AreEqual("lights", await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ReceiveLoop_MessageLargerThanBuffer_ReassemblesIt()
    {
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(ServerWithVersion());

        string longName = new('x', 256 * 1024);
        TaskCompletionSource<string> received = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        fake.Client.CurrentProgramSceneChanged += (_, e) =>
            received.TrySetResult(e.EventData.SceneName);

        fake.Server.RaiseEvent(
            "CurrentProgramSceneChanged",
            $$"""{"sceneName":"{{longName}}","sceneUuid":"p"}"""
        );

        Assert.AreEqual(longName, await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ReceiveLoop_MessageOverCeiling_EndsConnection()
    {
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(
            ServerWithVersion(),
            options => options.MaxIncomingMessageBytes = ObsWebSocketClient.ReceiveBufferSize
        );

        TaskCompletionSource<Exception?> disconnected = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        fake.Client.Disconnected += (_, e) => disconnected.TrySetResult(e.ReasonException);

        fake.Server.RaiseEvent(
            "CurrentProgramSceneChanged",
            $$"""{"sceneName":"{{new string('x', 64 * 1024)}}","sceneUuid":"p"}"""
        );

        Exception? reason = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsInstanceOfType<ObsWebSocketMessageTooLargeException>(reason);
        Assert.IsFalse(fake.Client.IsConnected);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task CallAsync_ServerClosesWhilePending_Fails()
    {
        FakeObsServer server = new();
        TaskCompletionSource requestSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = server.OnRequest(
            "GetVersion",
            _ =>
            {
                requestSeen.TrySetResult();
                server.CloseFromServer((int)WebSocketCloseCode.SessionInvalidated);
                return FakeObsServer.RequestOutcome.NoReply;
            }
        );

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        Task<GetVersionResponseData> pending = fake.Client.General.GetVersionAsync();
        await requestSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _ = await Assert.ThrowsAsync<ObsWebSocketException>(() => pending);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ReidentifyAsync_Acknowledged_RecordsNewSubscriptions()
    {
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(
            ServerWithVersion(),
            options => options.EventSubscriptions = EventSubscription.None
        );

        Assert.AreEqual(EventSubscription.None, fake.Client.CurrentEventSubscriptions);

        await fake.Client.ReidentifyAsync(
            (uint)(EventSubscription.Scenes | EventSubscription.Inputs)
        );

        Assert.AreEqual(
            EventSubscription.Scenes | EventSubscription.Inputs,
            fake.Client.CurrentEventSubscriptions
        );
        await AssertStillServingAsync(fake);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ReidentifyAsync_NeverAnswered_FailsAndKeepsOldSubscriptions()
    {
        FakeObsServer server = ServerWithVersion();
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(
            server,
            options => options.EventSubscriptions = EventSubscription.Scenes
        );
        server.IgnoreReidentify = true;

        _ = await Assert.ThrowsAsync<ObsWebSocketException>(() =>
            fake.Client.ReidentifyAsync((uint)EventSubscription.Inputs, timeoutMs: 200)
        );

        Assert.AreEqual(EventSubscription.Scenes, fake.Client.CurrentEventSubscriptions);

        // The failed attempt must not wedge the next one.
        server.IgnoreReidentify = false;
        await fake.Client.ReidentifyAsync((uint)EventSubscription.Inputs);
        Assert.AreEqual(EventSubscription.Inputs, fake.Client.CurrentEventSubscriptions);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ReidentifyAsync_Cancelled_ThrowsOperationCanceled()
    {
        FakeObsServer server = ServerWithVersion();
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);
        server.IgnoreReidentify = true;
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(100));

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fake.Client.ReidentifyAsync(
                (uint)EventSubscription.Inputs,
                timeoutMs: 10_000,
                cancellationToken: cancel.Token
            )
        );
        await AssertStillServingAsync(fake);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task EventDispatch_OversizedUnreadablePayload_DropsItAndKeepsServing()
    {
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(ServerWithVersion());

        // Wrong shape and well past the length the log keeps.
        fake.Server.RaiseEvent(
            "CurrentProgramSceneChanged",
            $$"""{"sceneName":[{{string.Join(",", Enumerable.Repeat("1", 2000))}}]}"""
        );

        await AssertStillServingAsync(fake);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ReidentifyAsync_Concurrent_BothComplete()
    {
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(ServerWithVersion());

        await Task.WhenAll(
            fake.Client.ReidentifyAsync((uint)EventSubscription.Scenes),
            fake.Client.ReidentifyAsync((uint)EventSubscription.Inputs)
        );

        Assert.IsNotNull(fake.Client.CurrentEventSubscriptions);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ConnectionLoop_LostWithAutoReconnectOn_Reconnects()
    {
        FakeObsServer server = ServerWithVersion();
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(
            server,
            options =>
            {
                options.AutoReconnectEnabled = true;
                options.MaxReconnectAttempts = 5;
                options.InitialReconnectDelayMs = 10;
                options.MaxReconnectDelayMs = 20;
            }
        );

        int connects = 0;
        TaskCompletionSource reconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Client.Connected += (_, _) =>
        {
            if (Interlocked.Increment(ref connects) == 1)
            {
                reconnected.TrySetResult();
            }
        };

        server.CloseFromServer((int)WebSocketCloseCode.SessionInvalidated);

        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await AssertStillServingAsync(fake);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ConnectionLoop_LostWithAutoReconnectOff_StaysDisconnected()
    {
        FakeObsServer server = ServerWithVersion();
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(server);

        int attempts = 0;
        fake.Client.Connecting += (_, _) => Interlocked.Increment(ref attempts);
        TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Client.Disconnected += (_, _) => disconnected.TrySetResult();

        server.CloseFromServer((int)WebSocketCloseCode.SessionInvalidated);

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(
            0,
            attempts,
            "the server would have accepted a reconnect, so none may be tried"
        );
        Assert.IsFalse(fake.Client.IsConnected);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task DisconnectAsync_ThenRequest_RequestRefused()
    {
        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(ServerWithVersion());

        await fake.Client.DisconnectAsync();

        Assert.IsFalse(fake.Client.IsConnected);
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fake.Client.General.GetVersionAsync()
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task CallAsync_NoReplyWithinTimeout_ThrowsTimeout()
    {
        FakeObsServer server = new();

        _ = server.OnRequest("GetVersion", _ => FakeObsServer.RequestOutcome.NoReply);

        await using FakeObsClient fake = await FakeObsClient.ConnectAsync(
            server,
            options => options.RequestTimeoutMs = 200
        );

        _ = await Assert.ThrowsExactlyAsync<ObsWebSocketTimeoutException>(() =>
            fake.Client.General.GetVersionAsync()
        );
        await AssertStillServingAfterTimeoutAsync(fake);
    }

    private static async Task AssertStillServingAfterTimeoutAsync(FakeObsClient fake)
    {
        // A timed out request is the caller's failure, not the connection's.
        _ = fake.Server.Returns("GetVersion", Version);
        await AssertStillServingAsync(fake);
    }
}
