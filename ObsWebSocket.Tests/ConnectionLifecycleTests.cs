using System.Net.WebSockets;
using Moq;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Tests;

/// <summary>
/// The per-connection state: what a connection context owns, when it signals its end, and the
/// delay curve a reconnect follows.
/// </summary>
[TestClass]
public sealed class ConnectionLifecycleTests
{
    private const int TestTimeout = 30_000;
    private static readonly Uri s_uri = new("ws://testhost:4455");

    private static ObsWebSocketClientOptions Options(
        Action<ObsWebSocketClientOptions>? configure = null
    )
    {
        ObsWebSocketClientOptions options = new() { ServerUri = s_uri };
        configure?.Invoke(options);
        return options;
    }

    private static ObsConnectionContext CreateContext(
        Mock<IWebSocketConnection> transport,
        CancellationToken lifetime = default,
        Action<ObsWebSocketClientOptions>? configure = null
    ) =>
        new(
            transport.Object,
            Mock.Of<IWebSocketMessageSerializer>(),
            ObsConnectionSettings.Capture(Options(configure)),
            lifetime
        );

    #region Context

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task Context_New_ExposesPartsAndIsOpen()
    {
        Mock<IWebSocketConnection> transport = new();
        IWebSocketMessageSerializer serializer = Mock.Of<IWebSocketMessageSerializer>();
        ObsConnectionSettings settings = ObsConnectionSettings.Capture(Options());

        await using ObsConnectionContext context = new(
            transport.Object,
            serializer,
            settings,
            CancellationToken.None
        );

        Assert.AreSame(transport.Object, context.Transport);
        Assert.AreSame(serializer, context.Serializer);
        Assert.AreSame(settings, context.Settings);
        Assert.IsFalse(context.ConnectionClosed.IsCancellationRequested);
        Assert.IsFalse(context.Hello.Task.IsCompleted);
        Assert.IsFalse(context.Identified.Task.IsCompleted);
        Assert.IsNull(context.ReceiveTask);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task Close_Open_CancelsTokenAndTearsDownSocket()
    {
        Mock<IWebSocketConnection> transport = new();
        await using ObsConnectionContext context = CreateContext(transport);

        context.Close();

        Assert.IsTrue(context.ConnectionClosed.IsCancellationRequested);
        transport.Verify(t => t.Abort(), Times.Once);
        transport.Verify(t => t.Dispose(), Times.Once);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task Close_SocketFaultsOnTeardown_DoesNotThrow()
    {
        Mock<IWebSocketConnection> transport = new();
        transport.Setup(t => t.Abort()).Throws(new WebSocketException("already faulted"));
        transport.Setup(t => t.Dispose()).Throws(new ObjectDisposedException("socket"));

        await using ObsConnectionContext context = CreateContext(transport);

        context.Close();

        Assert.IsTrue(context.ConnectionClosed.IsCancellationRequested);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task Context_ClientLifetimeCancelled_SignalsClosed()
    {
        using CancellationTokenSource lifetime = new();
        Mock<IWebSocketConnection> transport = new();
        await using ObsConnectionContext context = CreateContext(transport, lifetime.Token);

        await lifetime.CancelAsync();

        Assert.IsTrue(context.ConnectionClosed.IsCancellationRequested);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task Context_LifetimeAlreadyCancelled_StartsClosed()
    {
        using CancellationTokenSource lifetime = new();
        await lifetime.CancelAsync();

        Mock<IWebSocketConnection> transport = new();
        await using ObsConnectionContext context = CreateContext(transport, lifetime.Token);

        Assert.IsTrue(context.ConnectionClosed.IsCancellationRequested);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task DisposeAsync_WithReceiveLoop_WaitsForIt()
    {
        Mock<IWebSocketConnection> transport = new();
        ObsConnectionContext context = CreateContext(transport);

        TaskCompletionSource loopFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.ReceiveTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, context.ConnectionClosed);
            }
            catch (OperationCanceledException)
            {
                loopFinished.SetResult();
            }
        });

        await context.DisposeAsync();

        Assert.IsTrue(loopFinished.Task.IsCompletedSuccessfully);
        Assert.IsTrue(context.ReceiveTask.IsCompleted);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task DisposeAsync_ReceiveLoopFaulted_DoesNotThrow()
    {
        Mock<IWebSocketConnection> transport = new();
        ObsConnectionContext context = CreateContext(transport);
        context.ReceiveTask = Task.FromException(new WebSocketException("socket died"));

        await context.DisposeAsync();

        transport.Verify(t => t.Abort(), Times.Once);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task DisposeAsync_CalledTwice_TearsDownOnce()
    {
        Mock<IWebSocketConnection> transport = new();
        ObsConnectionContext context = CreateContext(transport);

        await context.DisposeAsync();
        await context.DisposeAsync();

        transport.Verify(t => t.Abort(), Times.Once);
        transport.Verify(t => t.Dispose(), Times.Once);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task Close_AfterDispose_DoesNotThrow()
    {
        Mock<IWebSocketConnection> transport = new();
        ObsConnectionContext context = CreateContext(transport);

        await context.DisposeAsync();
        context.Close();

        transport.Verify(t => t.Abort(), Times.Exactly(2));
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task Identified_Replaced_KeepsHelloAndAbandonsOldWaiter()
    {
        Mock<IWebSocketConnection> transport = new();
        await using ObsConnectionContext context = CreateContext(transport);

        TaskCompletionSource<object> first = context.Identified;
        context.Hello.SetResult(new object());

        TaskCompletionSource<object> second = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        context.Identified = second;
        second.SetResult(new object());

        Assert.AreNotSame(first, context.Identified);
        Assert.IsFalse(first.Task.IsCompleted, "the replaced waiter is abandoned, not completed");
        Assert.IsTrue(context.Hello.Task.IsCompletedSuccessfully);
        Assert.IsTrue(context.Identified.Task.IsCompletedSuccessfully);
    }

    #endregion

    #region Settings

    [TestMethod]
    [Timeout(TestTimeout)]
    public void Capture_Options_CopiesConnectionValues()
    {
        ObsConnectionSettings settings = ObsConnectionSettings.Capture(
            Options(options =>
            {
                options.Password = "secret";
                options.Format = SerializationFormat.MsgPack;
                options.EventSubscriptions = EventSubscription.Scenes;
                options.HandshakeTimeoutMs = 1234;
                options.AutoReconnectEnabled = true;
                options.MaxReconnectAttempts = 7;
                options.InitialReconnectDelayMs = 250;
                options.MaxReconnectDelayMs = 9000;
                options.ReconnectBackoffMultiplier = 2.5;
            })
        );

        Assert.AreEqual(s_uri, settings.ServerUri);
        Assert.AreEqual("secret", settings.Password);
        Assert.AreEqual(SerializationFormat.MsgPack, settings.Format);
        Assert.AreEqual(EventSubscription.Scenes, settings.EventSubscriptions);
        Assert.AreEqual(1234, settings.HandshakeTimeoutMs);
        Assert.IsTrue(settings.AutoReconnectEnabled);
        Assert.AreEqual(7, settings.MaxReconnectAttempts);
        Assert.AreEqual(250, settings.InitialReconnectDelayMs);
        Assert.AreEqual(9000, settings.MaxReconnectDelayMs);
        Assert.AreEqual(2.5, settings.ReconnectBackoffMultiplier);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public void Capture_NoSubscriptions_DefaultsToAll()
    {
        ObsConnectionSettings settings = ObsConnectionSettings.Capture(
            Options(options => options.EventSubscriptions = null)
        );

        Assert.AreEqual(EventSubscription.All, settings.EventSubscriptions);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public void Capture_MultiplierBelowOne_ClampsCopyOnly()
    {
        ObsWebSocketClientOptions options = Options(o => o.ReconnectBackoffMultiplier = 0.25);

        ObsConnectionSettings settings = ObsConnectionSettings.Capture(options);

        Assert.AreEqual(1.0, settings.ReconnectBackoffMultiplier);
        Assert.AreEqual(
            0.25,
            options.ReconnectBackoffMultiplier,
            "clamping must not write back to the monitored instance"
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public void Capture_NoServerUri_Throws()
    {
        _ = Assert.ThrowsExactly<ArgumentNullException>(() =>
            ObsConnectionSettings.Capture(new ObsWebSocketClientOptions())
        );
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => ObsConnectionSettings.Capture(null!));
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    [DataRow("uri")]
    [DataRow("password")]
    [DataRow("format")]
    [DataRow("subscriptions")]
    public void RequiresNewConnection_SocketValueChanged_ReturnsTrue(string change)
    {
        ObsConnectionSettings settings = ObsConnectionSettings.Capture(
            Options(options =>
            {
                options.Password = "secret";
                options.EventSubscriptions = EventSubscription.All;
            })
        );

        ObsWebSocketClientOptions updated = Options(options =>
        {
            options.Password = "secret";
            options.EventSubscriptions = EventSubscription.All;
            switch (change)
            {
                case "uri":
                    options.ServerUri = new Uri("ws://elsewhere:4455");
                    break;
                case "password":
                    options.Password = "other";
                    break;
                case "format":
                    options.Format = SerializationFormat.MsgPack;
                    break;
                default:
                    options.EventSubscriptions = EventSubscription.Scenes;
                    break;
            }
        });

        Assert.IsTrue(settings.RequiresNewConnection(updated));
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public void RequiresNewConnection_PerCallValueChanged_ReturnsFalse()
    {
        ObsConnectionSettings settings = ObsConnectionSettings.Capture(
            Options(options => options.Password = "secret")
        );

        ObsWebSocketClientOptions updated = Options(options =>
        {
            options.Password = "secret";
            options.HandshakeTimeoutMs = 99_000;
            options.AutoReconnectEnabled = !settings.AutoReconnectEnabled;
            options.MaxReconnectAttempts = 42;
            options.InitialReconnectDelayMs = 4321;
            options.MaxReconnectDelayMs = 54_321;
            options.ReconnectBackoffMultiplier = 3.0;
            options.RequestTimeoutMs = 1;
        });

        Assert.IsFalse(settings.RequiresNewConnection(updated));
        _ = Assert.ThrowsExactly<ArgumentNullException>(() =>
            settings.RequiresNewConnection(null!)
        );
    }

    #endregion

    #region Reconnect delays

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetDelayAsync_Disabled_ReturnsZero()
    {
        Assert.AreEqual(TimeSpan.Zero, await ReconnectDelays.Disabled.GetDelayAsync(0));
        Assert.AreEqual(TimeSpan.Zero, await ReconnectDelays.Disabled.GetDelayAsync(9));
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetDelayAsync_SuccessiveAttempts_GrowsToCeiling()
    {
        ReconnectDelays delays = new(
            Options(options =>
            {
                options.InitialReconnectDelayMs = 100;
                options.MaxReconnectDelayMs = 800;
                options.ReconnectBackoffMultiplier = 2.0;
            })
        );

        // Jitter spreads each delay over 75 to 125 percent of its nominal value, so the
        // assertions are on the band rather than the exact number.
        await AssertWithinJitterAsync(delays, retryIndex: 0, nominalMs: 100);
        await AssertWithinJitterAsync(delays, retryIndex: 1, nominalMs: 200);
        await AssertWithinJitterAsync(delays, retryIndex: 2, nominalMs: 400);
        await AssertWithinJitterAsync(delays, retryIndex: 3, nominalMs: 800);
        await AssertWithinJitterAsync(delays, retryIndex: 10, nominalMs: 800);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetDelayAsync_NegativeIndex_TreatedAsFirst()
    {
        ReconnectDelays delays = new(
            Options(options =>
            {
                options.InitialReconnectDelayMs = 100;
                options.MaxReconnectDelayMs = 800;
                options.ReconnectBackoffMultiplier = 2.0;
            })
        );

        await AssertWithinJitterAsync(delays, retryIndex: -5, nominalMs: 100);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetDelayAsync_MultiplierOne_RepeatsInitialDelay()
    {
        ReconnectDelays delays = new(
            Options(options =>
            {
                options.InitialReconnectDelayMs = 500;
                options.MaxReconnectDelayMs = 5000;
                options.ReconnectBackoffMultiplier = 1.0;
            })
        );

        await AssertWithinJitterAsync(delays, retryIndex: 0, nominalMs: 500);
        await AssertWithinJitterAsync(delays, retryIndex: 4, nominalMs: 500);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetDelayAsync_FromSettings_UsesTheirCurve()
    {
        ObsConnectionSettings settings = ObsConnectionSettings.Capture(
            Options(options =>
            {
                options.InitialReconnectDelayMs = 200;
                options.MaxReconnectDelayMs = 400;
                options.ReconnectBackoffMultiplier = 2.0;
            })
        );

        ReconnectDelays delays = new(settings);

        await AssertWithinJitterAsync(delays, retryIndex: 0, nominalMs: 200);
        await AssertWithinJitterAsync(delays, retryIndex: 5, nominalMs: 400);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetDelayAsync_ZeroDelay_StaysZero()
    {
        ReconnectDelays delays = new(
            Options(options =>
            {
                options.InitialReconnectDelayMs = 0;
                options.MaxReconnectDelayMs = 0;
            })
        );

        Assert.AreEqual(TimeSpan.Zero, await delays.GetDelayAsync(0));
        Assert.AreEqual(TimeSpan.Zero, await delays.GetDelayAsync(3));
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public void ReconnectDelays_NullSource_Throws()
    {
        _ = Assert.ThrowsExactly<ArgumentNullException>(() =>
            new ReconnectDelays((ObsWebSocketClientOptions)null!)
        );
        _ = Assert.ThrowsExactly<ArgumentNullException>(() =>
            new ReconnectDelays((ObsConnectionSettings)null!)
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetDelayAsync_CancelledToken_StillReturnsDelay()
    {
        ReconnectDelays delays = new(Options(options => options.InitialReconnectDelayMs = 100));
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        // The lookup is pure arithmetic, so a cancelled token is simply carried through.
        TimeSpan delay = await delays.GetDelayAsync(0, cancelled.Token);

        Assert.IsGreaterThan(TimeSpan.Zero, delay);
    }

    private static async Task AssertWithinJitterAsync(
        ReconnectDelays delays,
        int retryIndex,
        double nominalMs
    )
    {
        TimeSpan delay = await delays.GetDelayAsync(retryIndex);

        Assert.IsGreaterThanOrEqualTo(
            TimeSpan.FromMilliseconds(nominalMs * 0.75),
            delay,
            $"retry {retryIndex} fell below the jitter band for {nominalMs} ms"
        );
        Assert.IsLessThanOrEqualTo(
            TimeSpan.FromMilliseconds(nominalMs * 1.25),
            delay,
            $"retry {retryIndex} exceeded the jitter band for {nominalMs} ms"
        );
    }

    #endregion
}
