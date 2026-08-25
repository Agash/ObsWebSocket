using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ObsWebSocket.Core;

namespace ObsWebSocket.Tests;

/// <summary>
/// Covers the host integration: connection strings, the auto-connect service and the health check.
/// </summary>
[TestClass]
public sealed class HostingTests
{
    [TestMethod]
    public void ApplyConnectionString_EndpointOnly_SetsServerUri()
    {
        ObsWebSocketClientOptions options = new();
        ObsWebSocketHostingExtensions.ApplyConnectionString(options, "ws://localhost:4455");

        Assert.AreEqual(new Uri("ws://localhost:4455"), options.ServerUri);
        Assert.IsNull(options.Password);
    }

    [TestMethod]
    public void ApplyConnectionString_WithPassword_SplitsItOutOfTheUri()
    {
        ObsWebSocketClientOptions options = new();
        ObsWebSocketHostingExtensions.ApplyConnectionString(
            options,
            "ws://obs.local:4455?password=hunter%202"
        );

        Assert.AreEqual(new Uri("ws://obs.local:4455"), options.ServerUri);
        Assert.AreEqual("hunter 2", options.Password, "the value should be unescaped");
        Assert.IsTrue(
            string.IsNullOrEmpty(options.ServerUri!.Query),
            "the password must not remain on the endpoint"
        );
    }

    [TestMethod]
    public void AddObsWebSocketClient_FromConnectionString_ResolvesAConfiguredClient()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:obs"] = "ws://localhost:4455" }
        );

        _ = builder.AddObsWebSocketClient("obs");

        using IHost host = builder.Build();
        ObsWebSocketClient client = host.Services.GetRequiredService<ObsWebSocketClient>();

        Assert.IsNotNull(client);
        Assert.IsFalse(client.IsConnected);
    }

    [TestMethod]
    public void AddObsWebSocketClient_MissingConnectionString_ExplainsWhat()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        InvalidOperationException ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => builder.AddObsWebSocketClient("obs")
        );

        StringAssert.Contains(ex.Message, "ConnectionStrings:obs");
    }

    [TestMethod]
    public async Task HealthCheck_WhenNotConnected_ReportsUnhealthy()
    {
        ServiceCollection services = new();
        _ = services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        _ = services.AddObsWebSocketClient(o => o.ServerUri = new Uri("ws://localhost:4455"));
        _ = services.AddHealthChecks().AddObsWebSocket();

        await using ServiceProvider provider = services.BuildServiceProvider();
        HealthCheckService checks = provider.GetRequiredService<HealthCheckService>();

        HealthReport report = await checks.CheckHealthAsync(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(HealthStatus.Unhealthy, report.Status);
        Assert.IsTrue(report.Entries.ContainsKey("obs-websocket"));
    }

    [TestMethod]
    public async Task WithAutoConnect_WhenObsIsUnreachable_DoesNotPreventStartup()
    {
        // OBS is often started after the application, so an unreachable endpoint has to be
        // survivable rather than fatal.
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Critical);
        _ = builder.Services.AddObsWebSocketClient(o =>
        {
            o.ServerUri = new Uri("ws://127.0.0.1:59999");
            o.AutoReconnectEnabled = false;
            o.HandshakeTimeoutMs = 200;
        });
        _ = builder.Services.WithAutoConnect();

        using IHost host = builder.Build();

        await host.StartAsync(TestContext.CancellationTokenSource.Token);
        await host.StopAsync(TestContext.CancellationTokenSource.Token);
    }

    public TestContext TestContext { get; set; } = null!;
}
