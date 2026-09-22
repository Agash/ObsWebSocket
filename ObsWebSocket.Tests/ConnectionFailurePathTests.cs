using System.Net.WebSockets;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Tests;

/// <summary>
/// The paths a connection takes when something goes wrong: a handshake that never completes, a
/// refused connect, a send that throws. These fail quietly in production and are reachable
/// through the mocked transport.
/// </summary>
[TestClass]
public sealed class ConnectionFailurePathTests
{
    private const int TestTimeout = 30_000;
    private static readonly Uri s_uri = new("ws://testhost:4455");

    /// <summary>Drives the client until <paramref name="pending"/> settles.</summary>
    private static async Task<Exception?> PumpForFailureAsync(Task pending, FakeTimeProvider time)
    {
        for (int i = 0; i < 4000 && !pending.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(1);
        }

        try
        {
            await pending;
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    /// <summary>A transport that opens, sends nothing, and blocks on receive.</summary>
    private static void SetupSilentTransport(Mock<IWebSocketConnection> connection)
    {
        _ = connection
            .Setup(c => c.ConnectAsync(s_uri, It.IsAny<CancellationToken>()))
            .Callback(() => connection.SetupGet(c => c.State).Returns(WebSocketState.Open))
            .Returns(Task.CompletedTask);

        _ = connection
            .Setup(c => c.ReceiveAsync(It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(
                (Memory<byte> _, CancellationToken token) =>
                    new ValueTask<ValueWebSocketReceiveResult>(
                        Task.Delay(Timeout.Infinite, token)
                            .ContinueWith(
                                _ => new ValueWebSocketReceiveResult(
                                    0,
                                    WebSocketMessageType.Close,
                                    true
                                ),
                                TaskContinuationOptions.ExecuteSynchronously
                            )
                    )
            );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ConnectAsync_HandshakeNeverArrives_Fails()
    {
        FakeTimeProvider time = new();
        (
            ObsWebSocketClient client,
            Mock<IWebSocketConnection> connection,
            Mock<IWebSocketMessageSerializer> _,
            Mock<IWebSocketConnectionFactory> __
        ) = TestUtils.BuildMockedClientInfrastructure(
            options =>
            {
                options.ServerUri = s_uri;
                options.HandshakeTimeoutMs = 200;
                options.AutoReconnectEnabled = false;
                options.MaxReconnectAttempts = 0;
            },
            timeProvider: time
        );

        await using ObsWebSocketClient owned = client;
        SetupSilentTransport(connection);

        Exception? failure = await PumpForFailureAsync(client.ConnectAsync(), time);

        Assert.IsNotNull(failure, "a handshake that never completes must fail the connect");
        Assert.IsFalse(client.IsConnected);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ConnectAsync_RefusedWithAutoReconnectOff_TriesOnce()
    {
        FakeTimeProvider time = new();
        (
            ObsWebSocketClient client,
            Mock<IWebSocketConnection> connection,
            Mock<IWebSocketMessageSerializer> _,
            Mock<IWebSocketConnectionFactory> __
        ) = TestUtils.BuildMockedClientInfrastructure(
            options =>
            {
                options.ServerUri = s_uri;
                options.AutoReconnectEnabled = false;
                options.MaxReconnectAttempts = 0;
            },
            timeProvider: time
        );

        await using ObsWebSocketClient owned = client;

        int attempts = 0;
        _ = connection
            .Setup(c => c.ConnectAsync(s_uri, It.IsAny<CancellationToken>()))
            .Callback(() => attempts++)
            .ThrowsAsync(new WebSocketException("refused"));

        Exception? failure = await PumpForFailureAsync(client.ConnectAsync(), time);

        Assert.IsNotNull(failure);
        Assert.AreEqual(1, attempts, "auto reconnect is off, so exactly one attempt is expected");
        Assert.IsFalse(client.IsConnected);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ConnectAsync_RefusedWithAutoReconnectOn_Retries()
    {
        FakeTimeProvider time = new();
        (
            ObsWebSocketClient client,
            Mock<IWebSocketConnection> connection,
            Mock<IWebSocketMessageSerializer> _,
            Mock<IWebSocketConnectionFactory> __
        ) = TestUtils.BuildMockedClientInfrastructure(
            options =>
            {
                options.ServerUri = s_uri;
                options.AutoReconnectEnabled = true;
                options.MaxReconnectAttempts = 2;
                options.InitialReconnectDelayMs = 1;
                options.MaxReconnectDelayMs = 2;
            },
            timeProvider: time
        );

        await using ObsWebSocketClient owned = client;

        int attempts = 0;
        _ = connection
            .Setup(c => c.ConnectAsync(s_uri, It.IsAny<CancellationToken>()))
            .Callback(() => attempts++)
            .ThrowsAsync(new WebSocketException("refused"));

        Exception? failure = await PumpForFailureAsync(client.ConnectAsync(), time);

        Assert.IsNotNull(failure);
        Assert.IsGreaterThan(1, attempts, "the retry limit allows more than the first attempt");
        Assert.IsFalse(client.IsConnected);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task DisconnectAsync_NeverConnected_DoesNothing()
    {
        (
            ObsWebSocketClient client,
            Mock<IWebSocketConnection> _,
            Mock<IWebSocketMessageSerializer> __,
            Mock<IWebSocketConnectionFactory> ___
        ) = TestUtils.BuildMockedClientInfrastructure(options => options.ServerUri = s_uri);

        await using ObsWebSocketClient owned = client;

        await client.DisconnectAsync();

        Assert.IsFalse(client.IsConnected);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ReidentifyAsync_NotConnected_Throws()
    {
        (
            ObsWebSocketClient client,
            Mock<IWebSocketConnection> _,
            Mock<IWebSocketMessageSerializer> __,
            Mock<IWebSocketConnectionFactory> ___
        ) = TestUtils.BuildMockedClientInfrastructure(options => options.ServerUri = s_uri);

        await using ObsWebSocketClient owned = client;

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            client.ReidentifyAsync(null)
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task CallAsync_NotConnected_Throws()
    {
        (
            ObsWebSocketClient client,
            Mock<IWebSocketConnection> _,
            Mock<IWebSocketMessageSerializer> __,
            Mock<IWebSocketConnectionFactory> ___
        ) = TestUtils.BuildMockedClientInfrastructure(options => options.ServerUri = s_uri);

        await using ObsWebSocketClient owned = client;

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            client.CallAsync<object>("GetVersion")
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task CallAsync_SendThrows_SurfacesToCaller()
    {
        (
            ObsWebSocketClient client,
            Mock<IWebSocketMessageSerializer> _,
            Mock<IWebSocketConnection> connection
        ) = TestUtils.SetupConnectedClientForceState();

        await using ObsWebSocketClient owned = client;

        _ = connection
            .Setup(c =>
                c.SendAsync(
                    It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<WebSocketMessageType>(),
                    true,
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new WebSocketException("send failed"));

        _ = await Assert.ThrowsExactlyAsync<WebSocketException>(() =>
            client.CallAsync<object>("GetVersion")
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    [DataRow(4009, true)]
    [DataRow(4011, false)]
    public async Task HandleServerClose_CloseCode_OnlyAuthenticationFailureIsFatal(
        int closeCode,
        bool fatal
    )
    {
        (
            ObsWebSocketClient client,
            Mock<IWebSocketMessageSerializer> _,
            Mock<IWebSocketConnection> connection
        ) = TestUtils.SetupConnectedClientForceState();

        await using ObsWebSocketClient owned = client;

        ObsConnectionContext context = TestUtils.GetPrivateField<ObsConnectionContext>(
            client,
            "_connection"
        )!;
        connection.SetupGet(c => c.State).Returns(WebSocketState.CloseReceived);
        connection.SetupGet(c => c.CloseStatus).Returns((WebSocketCloseStatus)closeCode);
        connection.SetupGet(c => c.CloseStatusDescription).Returns("closed by the server");

        Action<ObsConnectionContext> handleServerClose = TestUtils.GetPrivateMethodDelegate<
            Action<ObsConnectionContext>
        >(client, "HandleServerClose")!;
        handleServerClose(context);

        Exception failure = await GetFaultAsync(context.Identified.Task);

        // Only a rejected identify may stop the reconnect loop; every other close is retryable.
        if (fatal)
        {
            Assert.IsInstanceOfType<AuthenticationFailureException>(failure);
        }
        else
        {
            Assert.IsNotInstanceOfType<AuthenticationFailureException>(failure);
            Assert.IsInstanceOfType<ObsWebSocketException>(failure);
        }
    }

    private static async Task<Exception> GetFaultAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception error)
        {
            return error;
        }

        throw new AssertFailedException("the handshake was expected to fail");
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task WaitForResponseAsync_FaultedThenLifetimeCancelled_SurfacesFault()
    {
        (
            ObsWebSocketClient client,
            Mock<IWebSocketMessageSerializer> _,
            Mock<IWebSocketConnection> __
        ) = TestUtils.SetupConnectedClientForceState();

        await using ObsWebSocketClient owned = client;

        Func<TaskCompletionSource<object>, int, string, CancellationToken, Task<object>> wait =
            TestUtils.GetPrivateMethodDelegate<
                Func<TaskCompletionSource<object>, int, string, CancellationToken, Task<object>>
            >(client, "WaitForResponseAsync")!;

        // A dropped connection faults the pending request and then cancels the client lifetime.
        // The fault reaches the waiter a thread-pool turn later, the cancellation at once.
        TaskCompletionSource<object> pending = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using CancellationTokenSource lifetime = new();
        Task<object> waiting = wait(pending, 30_000, "Request 'GetVersion'", lifetime.Token);

        ObsWebSocketException lost = new("Connection closed by server.");
        _ = pending.TrySetException(lost);
        await lifetime.CancelAsync();

        ObsWebSocketException surfaced = await Assert.ThrowsAsync<ObsWebSocketException>(() =>
            waiting
        );
        Assert.AreSame(lost, surfaced.InnerException, "the cause must not be lost to the cancel");
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task WaitForResponseAsync_CallerCancelled_ThrowsOperationCanceled()
    {
        (
            ObsWebSocketClient client,
            Mock<IWebSocketMessageSerializer> _,
            Mock<IWebSocketConnection> __
        ) = TestUtils.SetupConnectedClientForceState();

        await using ObsWebSocketClient owned = client;

        Func<TaskCompletionSource<object>, int, string, CancellationToken, Task<object>> wait =
            TestUtils.GetPrivateMethodDelegate<
                Func<TaskCompletionSource<object>, int, string, CancellationToken, Task<object>>
            >(client, "WaitForResponseAsync")!;

        TaskCompletionSource<object> pending = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using CancellationTokenSource caller = new();
        Task<object> waiting = wait(pending, 30_000, "Request 'GetVersion'", caller.Token);

        await caller.CancelAsync();

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        (
            ObsWebSocketClient client,
            Mock<IWebSocketConnection> _,
            Mock<IWebSocketMessageSerializer> __,
            Mock<IWebSocketConnectionFactory> ___
        ) = TestUtils.BuildMockedClientInfrastructure(options => options.ServerUri = s_uri);

        await client.DisposeAsync();
        await client.DisposeAsync();
    }
}
