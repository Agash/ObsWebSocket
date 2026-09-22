using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Tests.Integration;

/// <summary>
/// A connected client for one wire format, with the machine specific values a sweep needs.
/// </summary>
internal sealed class LiveClient : IAsyncDisposable
{
    private static readonly SemaphoreSlim s_outputsGate = new(1, 1);
    private static GetOutputListResponseData? s_outputs;

    private readonly ServiceProvider _provider;

    private LiveClient(
        ServiceProvider provider,
        ObsWebSocketClient client,
        ObsSourceKinds kinds,
        GetOutputListResponseData outputs
    )
    {
        _provider = provider;
        Client = client;
        Kinds = kinds;
        Outputs = outputs;
    }

    /// <summary>The connected client.</summary>
    public ObsWebSocketClient Client { get; }

    /// <summary>The input and filter kinds this OBS offers.</summary>
    public ObsSourceKinds Kinds { get; }

    /// <summary>
    /// The outputs, read once per test run before anything changes OBS: enumerating them after
    /// the write sweep has reset video reads a freed encoder and takes OBS down (#25). Every later
    /// client, whatever its wire format, reuses that first read.
    /// </summary>
    public GetOutputListResponseData Outputs { get; }

    /// <summary>The health check service, for the checks that exercise it.</summary>
    public HealthCheckService HealthChecks => _provider.GetRequiredService<HealthCheckService>();

    /// <summary>Connects to the configured OBS using <paramref name="format"/>.</summary>
    /// <param name="format">The wire format to negotiate.</param>
    /// <param name="context">The running test, for cancellation.</param>
    public static async Task<LiveClient> ConnectAsync(
        SerializationFormat format,
        TestContext context
    )
    {
        (Uri uri, string? password) = ObsLiveEndpoint.Require(context);

        ServiceCollection services = new();
        _ = services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        _ = services
            .AddObsWebSocketClient(options =>
            {
                options.ServerUri = uri;
                options.Password = password;
                options.Format = format;
                options.AutoReconnectEnabled = false;
            })
            .WithHealthCheck();

        ServiceProvider provider = services.BuildServiceProvider();
        ObsWebSocketClient client = provider.GetRequiredService<ObsWebSocketClient>();

        await client.ConnectAsync(context.CancellationToken).ConfigureAwait(false);

        ObsSourceKinds kinds = await ObsSourceKinds
            .ResolveAsync(client, context.CancellationToken)
            .ConfigureAwait(false);
        GetOutputListResponseData outputs = await ReadOutputsOnceAsync(
                client,
                context.CancellationToken
            )
            .ConfigureAwait(false);

        return new LiveClient(provider, client, kinds, outputs);
    }

    private static async Task<GetOutputListResponseData> ReadOutputsOnceAsync(
        ObsWebSocketClient client,
        CancellationToken cancellationToken
    )
    {
        await s_outputsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return s_outputs ??= await client
                .Outputs.GetOutputListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _ = s_outputsGate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await _provider.DisposeAsync().ConfigureAwait(false);
}
