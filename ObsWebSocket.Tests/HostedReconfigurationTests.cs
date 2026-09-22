using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Tests.Fakes;

namespace ObsWebSocket.Tests;

/// <summary>
/// The hosted connection following configuration: a change to what the socket is built from has
/// to reconnect, anything else must not, and shutdown has to leave nothing running.
/// </summary>
[TestClass]
public sealed class HostedReconfigurationTests
{
    private const int TestTimeout = 30_000;

    private sealed class Harness : IAsyncDisposable
    {
        public required ServiceProvider Provider { get; init; }
        public required IConfigurationRoot Configuration { get; init; }
        public required FakeObsServer Server { get; init; }
        public required IHostedService Service { get; init; }
        public required ObsWebSocketClient Client { get; init; }

        public void Change(string key, string value)
        {
            Configuration[$"Obs:{key}"] = value;
            Configuration.Reload();
        }

        public async ValueTask DisposeAsync() => await Provider.DisposeAsync();
    }

    private static Harness Build(FakeObsServer server, string? name = null)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Obs:ServerUri"] = "ws://first:4455",
                    ["Obs:AutoReconnectEnabled"] = "false",
                    ["Obs:RequestTimeoutMs"] = "5000",
                }
            )
            .Build();

        ServiceCollection services = new();
        _ = services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.None));
        IObsWebSocketClientBuilder client = name is null
            ? services.AddObsWebSocketClient()
            : services.AddObsWebSocketClient(name);
        _ = services.Configure<ObsWebSocketClientOptions>(
            name ?? Microsoft.Extensions.Options.Options.DefaultName,
            configuration.GetSection("Obs")
        );
        _ = client.WithAutoConnect();
        _ = services.AddSingleton<IWebSocketConnectionFactory>(server);

        ServiceProvider provider = services.BuildServiceProvider();
        return new Harness
        {
            Provider = provider,
            Configuration = configuration,
            Server = server,
            Service = provider.GetServices<IHostedService>().Single(),
            Client = name is null
                ? provider.GetRequiredService<ObsWebSocketClient>()
                : provider.GetRequiredKeyedService<ObsWebSocketClient>(name),
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task Service_StartThenStop_ConnectsThenDisconnects()
    {
        await using Harness harness = Build(new FakeObsServer());

        await harness.Service.StartAsync(CancellationToken.None);
        Assert.IsTrue(harness.Client.IsConnected);

        await harness.Service.StopAsync(CancellationToken.None);
        Assert.IsFalse(harness.Client.IsConnected);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task StartAsync_ObsUnreachable_DoesNotThrow()
    {
        await using Harness harness = Build(new FakeObsServer { RefuseConnections = true });

        await harness.Service.StartAsync(CancellationToken.None);

        Assert.IsFalse(harness.Client.IsConnected);
        await harness.Service.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task OptionsChange_NewEndpoint_Reconnects()
    {
        await using Harness harness = Build(new FakeObsServer());
        await harness.Service.StartAsync(CancellationToken.None);

        harness.Change("ServerUri", "ws://second:4455");

        await WaitUntilAsync(() =>
            harness.Server.ConnectedTo.Count == 2 && harness.Client.IsConnected
        );
        Assert.AreEqual(new Uri("ws://second:4455"), harness.Server.ConnectedTo[^1]);

        await harness.Service.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task OptionsChange_PerCallValue_KeepsConnection()
    {
        await using Harness harness = Build(new FakeObsServer());
        await harness.Service.StartAsync(CancellationToken.None);

        harness.Change("RequestTimeoutMs", "9000");

        // Nothing to wait on for an absence, so give a reconnect time it would have needed.
        await Task.Delay(200);
        Assert.HasCount(1, harness.Server.ConnectedTo);
        Assert.IsTrue(harness.Client.IsConnected);

        await harness.Service.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task OptionsChange_NamedClient_Reconnects()
    {
        await using Harness harness = Build(new FakeObsServer(), name: "studio");
        await harness.Service.StartAsync(CancellationToken.None);

        harness.Change("ServerUri", "ws://second:4455");

        await WaitUntilAsync(() =>
            harness.Server.ConnectedTo.Count == 2 && harness.Client.IsConnected
        );
        Assert.AreEqual(new Uri("ws://second:4455"), harness.Server.ConnectedTo[^1]);

        await harness.Service.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task OptionsChange_ReconnectFails_LaterChangeStillApplies()
    {
        FakeObsServer server = new();
        await using Harness harness = Build(server);
        await harness.Service.StartAsync(CancellationToken.None);

        server.RefuseConnections = true;
        harness.Change("ServerUri", "ws://second:4455");

        await WaitUntilAsync(() => !harness.Client.IsConnected);

        // A later change still gets through once OBS is back.
        server.RefuseConnections = false;
        harness.Change("ServerUri", "ws://third:4455");
        await WaitUntilAsync(() => harness.Client.IsConnected);
        Assert.AreEqual(new Uri("ws://third:4455"), server.ConnectedTo[^1]);

        await harness.Service.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task StopAsync_NotStarted_DoesNothing()
    {
        await using Harness harness = Build(new FakeObsServer());

        await harness.Service.StopAsync(CancellationToken.None);

        Assert.IsFalse(harness.Client.IsConnected);
    }

    [TestMethod]
    public async Task WithAutoConnect_ObsoleteForwarder_RegistersService()
    {
        ServiceCollection services = new();
        _ = services.AddLogging();
        _ = services.AddObsWebSocketClient(o => o.ServerUri = new Uri("ws://localhost:4455"));

#pragma warning disable CS0618 // The forwarder under test is the obsolete one.
        _ = services.WithAutoConnect();
#pragma warning restore CS0618

        await using ServiceProvider provider = services.BuildServiceProvider();
        Assert.ContainsSingle(provider.GetServices<IHostedService>());
    }
}
