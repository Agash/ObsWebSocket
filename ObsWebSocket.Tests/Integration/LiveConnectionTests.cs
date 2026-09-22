using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol.Requests;

namespace ObsWebSocket.Tests.Integration;

/// <summary>
/// The connection lifecycle against a real OBS: handshake, re-identification, disconnect and
/// reconnect, and the failures a wrong endpoint or password produces. The mocked transport cannot
/// prove the handshake is accepted by the server that defines it.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
public sealed class LiveConnectionTests
{
    private const int TimeoutMs = 120_000;

    /// <summary>The context MSTest assigns, used for cancellation and run parameters.</summary>
    public TestContext TestContext { get; set; } = null!;

    private ServiceProvider Build(Action<ObsWebSocketClientOptions> configure)
    {
        (Uri uri, string? password) = ObsLiveEndpoint.Require(TestContext);

        ServiceCollection services = new();
        _ = services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        _ = services
            .AddObsWebSocketClient(options =>
            {
                options.ServerUri = uri;
                options.Password = password;
                options.AutoReconnectEnabled = false;
                configure(options);
            })
            .WithHealthCheck();

        return services.BuildServiceProvider();
    }

    [TestMethod]
    [Timeout(TimeoutMs, CooperativeCancellation = true)]
    public async Task ConnectAsync_AfterOwnDisconnect_Reconnects()
    {
        CancellationToken token = TestContext.CancellationToken;
        await using ServiceProvider provider = Build(_ => { });
        ObsWebSocketClient client = provider.GetRequiredService<ObsWebSocketClient>();

        int connects = 0;
        int disconnects = 0;
        client.Connected += (_, _) => Interlocked.Increment(ref connects);
        client.Disconnected += (_, _) => Interlocked.Increment(ref disconnects);

        await client.ConnectAsync(token).ConfigureAwait(false);
        Assert.IsTrue(client.IsConnected);
        _ = await client.General.GetVersionAsync(token).ConfigureAwait(false);

        await client.DisconnectAsync(cancellationToken: token).ConfigureAwait(false);
        Assert.IsFalse(client.IsConnected);

        // A second cycle on the same instance: the first connection's receive loop and context
        // have to be gone before this can identify again.
        await client.ConnectAsync(token).ConfigureAwait(false);
        Assert.IsTrue(client.IsConnected);
        _ = await client.General.GetVersionAsync(token).ConfigureAwait(false);

        await client.DisconnectAsync(cancellationToken: token).ConfigureAwait(false);

        Assert.AreEqual(2, connects);
        Assert.AreEqual(2, disconnects);
    }

    [TestMethod]
    [Timeout(TimeoutMs, CooperativeCancellation = true)]
    public async Task ConnectAsync_AlreadyConnected_Throws()
    {
        CancellationToken token = TestContext.CancellationToken;
        await using ServiceProvider provider = Build(_ => { });
        ObsWebSocketClient client = provider.GetRequiredService<ObsWebSocketClient>();

        await client.ConnectAsync(token).ConfigureAwait(false);

        _ = await Assert
            .ThrowsExactlyAsync<InvalidOperationException>(() => client.ConnectAsync(token))
            .ConfigureAwait(false);

        await client.DisconnectAsync(cancellationToken: token).ConfigureAwait(false);
    }

    [TestMethod]
    [Timeout(TimeoutMs, CooperativeCancellation = true)]
    public async Task DisconnectAsync_CalledTwice_DoesNothing()
    {
        CancellationToken token = TestContext.CancellationToken;
        await using ServiceProvider provider = Build(_ => { });
        ObsWebSocketClient client = provider.GetRequiredService<ObsWebSocketClient>();

        await client.ConnectAsync(token).ConfigureAwait(false);
        await client.DisconnectAsync(cancellationToken: token).ConfigureAwait(false);
        await client.DisconnectAsync(cancellationToken: token).ConfigureAwait(false);

        Assert.IsFalse(client.IsConnected);
    }

    [TestMethod]
    [Timeout(TimeoutMs, CooperativeCancellation = true)]
    public async Task ReidentifyAsync_LiveObs_ChangesDeliveredEvents()
    {
        CancellationToken token = TestContext.CancellationToken;
        await using ServiceProvider provider = Build(options =>
            options.EventSubscriptions = EventSubscription.None
        );
        ObsWebSocketClient client = provider.GetRequiredService<ObsWebSocketClient>();

        await client.ConnectAsync(token).ConfigureAwait(false);
        Assert.AreEqual(EventSubscription.None, client.CurrentEventSubscriptions);

        TaskCompletionSource<string> sceneChanged = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        client.CurrentProgramSceneChanged += (_, e) =>
            sceneChanged.TrySetResult(e.EventData.SceneName);

        string original = (
            await client
                .Scenes.GetSceneListAsync(new GetSceneListRequestData(), token)
                .ConfigureAwait(false)
        ).CurrentProgramSceneName!;

        // A scene of its own, so both switches below are real changes whatever the collection
        // holds: OBS raises nothing for a switch to the scene already active.
        string own = $"__obsws_reidentify_{Guid.NewGuid():N}"[..28];
        _ = await client.Scenes.CreateSceneAsync(new(sceneName: own), token).ConfigureAwait(false);

        try
        {
            await client.Scenes.SetCurrentProgramSceneAsync(new(own), token).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
            Assert.IsFalse(
                sceneChanged.Task.IsCompleted,
                "nothing was subscribed, so the switch must not have been delivered"
            );

            await client
                .ReidentifyAsync((uint)EventSubscription.Scenes, cancellationToken: token)
                .ConfigureAwait(false);
            Assert.AreEqual(EventSubscription.Scenes, client.CurrentEventSubscriptions);

            await client
                .Scenes.SetCurrentProgramSceneAsync(new(original), token)
                .ConfigureAwait(false);

            string delivered = await sceneChanged
                .Task.WaitAsync(TimeSpan.FromSeconds(10), token)
                .ConfigureAwait(false);
            Assert.AreEqual(original, delivered);
        }
        finally
        {
            await client
                .Scenes.SetCurrentProgramSceneAsync(new(original), CancellationToken.None)
                .ConfigureAwait(false);
            await client
                .Scenes.RemoveSceneAsync(new(sceneName: own), CancellationToken.None)
                .ConfigureAwait(false);
            await client
                .DisconnectAsync(cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    [TestMethod]
    [Timeout(TimeoutMs, CooperativeCancellation = true)]
    public async Task HealthCheck_ConnectionLifecycle_FollowsState()
    {
        CancellationToken token = TestContext.CancellationToken;
        await using ServiceProvider provider = Build(_ => { });
        ObsWebSocketClient client = provider.GetRequiredService<ObsWebSocketClient>();
        HealthCheckService health = provider.GetRequiredService<HealthCheckService>();

        HealthReport beforeConnect = await health.CheckHealthAsync(token).ConfigureAwait(false);
        Assert.AreNotEqual(HealthStatus.Healthy, beforeConnect.Status);

        await client.ConnectAsync(token).ConfigureAwait(false);
        HealthReport connected = await health.CheckHealthAsync(token).ConfigureAwait(false);
        Assert.AreEqual(HealthStatus.Healthy, connected.Status);

        await client.DisconnectAsync(cancellationToken: token).ConfigureAwait(false);
        HealthReport afterDisconnect = await health.CheckHealthAsync(token).ConfigureAwait(false);
        Assert.AreNotEqual(HealthStatus.Healthy, afterDisconnect.Status);
    }

    [TestMethod]
    [Timeout(TimeoutMs, CooperativeCancellation = true)]
    public async Task ConnectAsync_WrongPassword_ThrowsAuthenticationFailure()
    {
        CancellationToken token = TestContext.CancellationToken;
        (_, string? password) = ObsLiveEndpoint.Require(TestContext);
        if (string.IsNullOrEmpty(password))
        {
            ObsLiveEndpoint.Unavailable(
                TestContext,
                "This OBS has authentication disabled, so there is no wrong password."
            );
        }

        await using ServiceProvider provider = Build(options =>
            options.Password = "not-the-password"
        );
        ObsWebSocketClient client = provider.GetRequiredService<ObsWebSocketClient>();

        TaskCompletionSource failureRaised = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        client.AuthenticationFailure += (_, _) => failureRaised.TrySetResult();

        _ = await Assert
            .ThrowsExactlyAsync<AuthenticationFailureException>(() => client.ConnectAsync(token))
            .ConfigureAwait(false);

        await failureRaised.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        Assert.IsFalse(client.IsConnected);
    }

    [TestMethod]
    [Timeout(TimeoutMs, CooperativeCancellation = true)]
    public async Task ConnectAsync_NoServer_FailsAfterRetryLimit()
    {
        CancellationToken token = TestContext.CancellationToken;
        (Uri uri, _) = ObsLiveEndpoint.Require(TestContext);

        ServiceCollection services = new();
        _ = services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.None));
        _ = services.AddObsWebSocketClient(options =>
        {
            // A port nothing listens on, so the connect is refused rather than timing out.
            options.ServerUri = new Uri($"ws://{uri.Host}:{FindClosedPort()}");
            options.AutoReconnectEnabled = true;
            options.MaxReconnectAttempts = 2;
            options.InitialReconnectDelayMs = 10;
            options.MaxReconnectDelayMs = 20;
            options.HandshakeTimeoutMs = 2000;
        });

        await using ServiceProvider provider = services.BuildServiceProvider();
        ObsWebSocketClient client = provider.GetRequiredService<ObsWebSocketClient>();

        int failures = 0;
        client.ConnectionFailed += (_, _) => Interlocked.Increment(ref failures);

        ObsWebSocketException failure = await Assert
            .ThrowsExactlyAsync<ObsWebSocketException>(() => client.ConnectAsync(token))
            .ConfigureAwait(false);

        Assert.IsInstanceOfType<ConnectionAttemptFailedException>(failure.InnerException);
        Assert.IsFalse(client.IsConnected);
        Assert.IsGreaterThan(0, failures);
    }

    private static int FindClosedPort()
    {
        using System.Net.Sockets.Socket probe = new(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp
        );
        probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        int port = ((System.Net.IPEndPoint)probe.LocalEndPoint!).Port;
        return port;
    }
}
