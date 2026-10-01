using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Networking;

namespace ObsWebSocket.Tests.Fakes;

/// <summary>
/// A client connected to a <see cref="FakeObsServer"/> through the ordinary DI registration, so
/// the handshake, receive loop and request correlation are the real ones.
/// </summary>
internal sealed class FakeObsClient : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private FakeObsClient(ServiceProvider provider, FakeObsServer server, ObsWebSocketClient client)
    {
        _provider = provider;
        Server = server;
        Client = client;
    }

    /// <summary>The OBS the client is talking to.</summary>
    public FakeObsServer Server { get; }

    /// <summary>The connected client.</summary>
    public ObsWebSocketClient Client { get; }

    /// <summary>Builds a client against <paramref name="server"/> without connecting it.</summary>
    /// <param name="server">The OBS to answer on the wire.</param>
    /// <param name="configure">Further client options.</param>
    /// <param name="logging">Where the client logs; nowhere when null.</param>
    public static FakeObsClient Build(
        FakeObsServer server,
        Action<ObsWebSocketClientOptions>? configure = null,
        ILoggerProvider? logging = null
    )
    {
        ServiceCollection services = new();
        _ = services.AddLogging(builder =>
        {
            if (logging is null)
            {
                _ = builder.SetMinimumLevel(LogLevel.None);
            }
            else
            {
                _ = builder.ClearProviders().AddProvider(logging).SetMinimumLevel(LogLevel.Trace);
            }
        });
        _ = services.AddObsWebSocketClient(options =>
        {
            options.ServerUri = new Uri("ws://fake-obs:4455");
            options.AutoReconnectEnabled = false;
            options.HandshakeTimeoutMs = 5000;
            options.RequestTimeoutMs = 5000;
            configure?.Invoke(options);
        });
        _ = services.AddSingleton<IWebSocketConnectionFactory>(server);

        ServiceProvider provider = services.BuildServiceProvider();
        return new FakeObsClient(
            provider,
            server,
            provider.GetRequiredService<ObsWebSocketClient>()
        );
    }

    /// <summary>Builds a client against <paramref name="server"/> and connects it.</summary>
    /// <param name="server">The OBS to answer on the wire.</param>
    /// <param name="configure">Further client options.</param>
    /// <param name="logging">Where the client logs; nowhere when null.</param>
    /// <param name="cancellationToken">A token to cancel the connect.</param>
    public static async Task<FakeObsClient> ConnectAsync(
        FakeObsServer server,
        Action<ObsWebSocketClientOptions>? configure = null,
        ILoggerProvider? logging = null,
        CancellationToken cancellationToken = default
    )
    {
        FakeObsClient fake = Build(server, configure, logging);
        await fake.Client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return fake;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await _provider.DisposeAsync().ConfigureAwait(false);
}
