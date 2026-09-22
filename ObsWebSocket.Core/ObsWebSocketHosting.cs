using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ObsWebSocket.Core.Networking;

namespace ObsWebSocket.Core;

/// <summary>
/// Connects the client when the host starts and disconnects when it stops.
/// </summary>
/// <param name="client">The client to manage.</param>
/// <param name="options">Monitored options, watched for endpoint changes.</param>
/// <param name="logger">Logger for connection outcomes.</param>
/// <param name="name">The key this client is registered under, or null for the unnamed client.</param>
internal sealed class ObsWebSocketConnectionService(
    ObsWebSocketClient client,
    IOptionsMonitor<ObsWebSocketClientOptions> options,
    ILogger<ObsWebSocketConnectionService> logger,
    string? name = null
) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>Pending configuration change; only the newest matters.</summary>
    private readonly Channel<ObsConnectionSettings> _pending =
        Channel.CreateBounded<ObsConnectionSettings>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            }
        );

    private IDisposable? _optionsWatch;
    private Task? _transitions;
    private ObsConnectionSettings? _connectedWith;

    private string OptionsName => name ?? Options.DefaultName;

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Before the watch, so a change during startup is not dropped.
        _transitions = Task.Run(ProcessTransitionsAsync, CancellationToken.None);

        _optionsWatch = options.OnChange(
            (updated, changedName) =>
            {
                // OnChange fires for every named instance, so ignore the ones that are not ours.
                if (
                    string.Equals(
                        changedName ?? Options.DefaultName,
                        OptionsName,
                        StringComparison.Ordinal
                    )
                )
                {
                    OnOptionsChanged(updated);
                }
            }
        );
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _connectedWith = ObsConnectionSettings.Capture(options.Get(OptionsName));

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObsWebSocketException or OperationCanceledException)
        {
            // A streaming tool should still start when OBS is not running yet; the client's own
            // reconnect handles it from here.
            logger.LogObsUnreachableAtStartup(ex);
        }
    }

    /// <summary>Queues a reconnect when something the connection is built from changes.</summary>
    private void OnOptionsChanged(ObsWebSocketClientOptions updated)
    {
        if (updated.ServerUri is null || _connectedWith?.RequiresNewConnection(updated) is not true)
        {
            return;
        }

        logger.LogConnectionSettingsChanged(updated.ServerUri);

        _ = _pending.Writer.TryWrite(ObsConnectionSettings.Capture(updated));
    }

    /// <summary>Applies queued configuration changes, one at a time, until the host stops.</summary>
    /// <remarks>One reader, awaited by <see cref="StopAsync"/>, so transitions cannot overlap or
    /// outlive shutdown.</remarks>
    private async Task ProcessTransitionsAsync()
    {
        try
        {
            await foreach (
                ObsConnectionSettings _ in _pending
                    .Reader.ReadAllAsync(_stopping.Token)
                    .ConfigureAwait(false)
            )
            {
                try
                {
                    await client.DisconnectAsync().ConfigureAwait(false);
                    await ConnectAsync(_stopping.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                    when (ex is ObsWebSocketException or OperationCanceledException)
                {
                    logger.LogReconnectAfterSettingsChangeFailed(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _optionsWatch?.Dispose();
        _stopping.Dispose();
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop accepting changes before draining.
        _optionsWatch?.Dispose();
        _optionsWatch = null;
        _ = _pending.Writer.TryComplete();

        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_transitions is { } transitions)
        {
            // Awaited so an in-flight reconnect cannot outlive shutdown.
            await transitions.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _transitions = null;
        }

        if (client.IsConnected)
        {
            await client
                .DisconnectAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Reports whether the client is connected to OBS.
/// </summary>
/// <param name="client">The client to report on.</param>
internal sealed class ObsWebSocketHealthCheck(ObsWebSocketClient client) : IHealthCheck
{
    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            client.IsConnected
                ? HealthCheckResult.Healthy("Connected to OBS.")
                : HealthCheckResult.Unhealthy("Not connected to OBS.")
        );
}

/// <summary>
/// Host integration for the client.
/// </summary>
public static class ObsWebSocketHostingExtensions
{
    /// <summary>
    /// Connects when the host starts and disconnects when it stops, so an application does not
    /// need its own background service for the connection.
    /// </summary>
    /// <remarks>
    /// A connection that cannot be established at startup is logged rather than thrown, because
    /// OBS is often started after the application; reconnect takes over from there.
    /// </remarks>
    /// <param name="services">The service collection the client was added to.</param>
    /// <returns>The same collection, for chaining.</returns>
    [Obsolete(
        "Chain WithAutoConnect off AddObsWebSocketClient instead, which also works for a named client. This forwarder will be removed in a future release."
    )]
    public static IServiceCollection WithAutoConnect(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _ = services.AddHostedService<ObsWebSocketConnectionService>();
        return services;
    }

    /// <summary>
    /// Adds the client, taking its endpoint from a connection string.
    /// </summary>
    /// <remarks>
    /// Reads <c>ConnectionStrings:{name}</c>, so the endpoint is configured the same way as any
    /// other resource an application connects to. A password may be supplied in the connection
    /// string as a <c>password</c> query parameter, or set on the options.
    /// </remarks>
    /// <param name="builder">The host application builder.</param>
    /// <param name="connectionName">The connection string name, also the key for the client.</param>
    /// <param name="configureOptions">An optional action to configure the remaining options.</param>
    /// <returns>A builder for configuring this client.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the connection string is missing.</exception>
    public static IObsWebSocketClientBuilder AddObsWebSocketClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<ObsWebSocketClientOptions>? configureOptions = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        string connectionString =
            builder.Configuration.GetConnectionString(connectionName)
            ?? throw new InvalidOperationException(
                $"No connection string named '{connectionName}' was found. Add ConnectionStrings:{connectionName}, for example ws://localhost:4455."
            );

        return builder.Services.AddObsWebSocketClient(options =>
        {
            ApplyConnectionString(options, connectionString);
            configureOptions?.Invoke(options);
        });
    }

    /// <summary>
    /// Reads an endpoint, and optionally a password, out of a connection string.
    /// </summary>
    /// <param name="options">The options to populate.</param>
    /// <param name="connectionString">The connection string to read.</param>
    internal static void ApplyConnectionString(
        ObsWebSocketClientOptions options,
        string connectionString
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        Uri uri = new(connectionString, UriKind.Absolute);

        if (!string.IsNullOrEmpty(uri.Query))
        {
            foreach (string pair in uri.Query.TrimStart('?').Split('&'))
            {
                string[] parts = pair.Split('=', 2);
                if (
                    parts.Length == 2
                    && parts[0].Equals("password", StringComparison.OrdinalIgnoreCase)
                )
                {
                    options.Password = Uri.UnescapeDataString(parts[1]);
                }
            }
        }

        options.ServerUri = new UriBuilder(uri) { Query = string.Empty }.Uri;
    }

    /// <summary>
    /// Adds a health check reporting whether the client is connected.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">The name to register the check under.</param>
    /// <param name="failureStatus">Status to report when not connected.</param>
    /// <param name="tags">Tags for filtering checks.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IHealthChecksBuilder AddObsWebSocket(
        this IHealthChecksBuilder builder,
        string name = "obs-websocket",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCheck<ObsWebSocketHealthCheck>(name, failureStatus, tags ?? []);
    }
}
