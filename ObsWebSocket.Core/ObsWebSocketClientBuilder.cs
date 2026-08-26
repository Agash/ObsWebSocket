using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ObsWebSocket.Core;

/// <summary>
/// The client returned by <c>AddObsWebSocketClient</c>, so the things that apply to one client are
/// chained off the call that registered it rather than applied to the whole container.
/// </summary>
/// <remarks>
/// This is what lets an application drive two OBS instances and give each its own behaviour, which
/// a bare <see cref="IServiceCollection"/> extension cannot express because it has no way to know
/// which client is meant.
/// </remarks>
public interface IObsWebSocketClientBuilder
{
    /// <summary>The collection the client was registered in.</summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// The key this client is registered under, or <see langword="null"/> for the unnamed client.
    /// </summary>
    string? Name { get; }
}

/// <param name="services">The collection the client was registered in.</param>
/// <param name="name">The key the client is registered under, or null for the unnamed client.</param>
internal sealed class ObsWebSocketClientBuilder(IServiceCollection services, string? name)
    : IObsWebSocketClientBuilder
{
    /// <inheritdoc/>
    public IServiceCollection Services { get; } = services;

    /// <inheritdoc/>
    public string? Name { get; } = name;
}

/// <summary>
/// The per-client options, chained off the registration.
/// </summary>
public static class ObsWebSocketClientBuilderExtensions
{
    /// <summary>
    /// Connects when the host starts and disconnects when it stops, so an application does not
    /// need its own background service for the connection.
    /// </summary>
    /// <remarks>
    /// A connection that cannot be established at startup is logged rather than thrown, because
    /// OBS is often started after the application; reconnect takes over from there.
    /// </remarks>
    /// <param name="builder">The client to connect automatically.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IObsWebSocketClientBuilder WithAutoConnect(
        this IObsWebSocketClientBuilder builder
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        string? name = builder.Name;
        _ = builder.Services.AddHostedService(sp => new ObsWebSocketConnectionService(
            name is null
                ? sp.GetRequiredService<ObsWebSocketClient>()
                : sp.GetRequiredKeyedService<ObsWebSocketClient>(name),
            sp.GetRequiredService<IOptionsMonitor<ObsWebSocketClientOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ObsWebSocketConnectionService>>(),
            name
        ));

        return builder;
    }

    /// <summary>
    /// Adds a health check reporting whether this client is connected.
    /// </summary>
    /// <param name="builder">The client to report on.</param>
    /// <param name="name">
    /// The name to register the check under. Defaults to <c>obs-websocket</c> for the unnamed
    /// client, and <c>obs-websocket-{key}</c> for a named one, so two clients do not collide.
    /// </param>
    /// <param name="failureStatus">Status to report when not connected.</param>
    /// <param name="tags">Tags for filtering checks.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IObsWebSocketClientBuilder WithHealthCheck(
        this IObsWebSocketClientBuilder builder,
        string? name = null,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        string? key = builder.Name;
        string checkName = name ?? (key is null ? "obs-websocket" : $"obs-websocket-{key}");

        _ = builder
            .Services.AddHealthChecks()
            .Add(
                new HealthCheckRegistration(
                    checkName,
                    sp => new ObsWebSocketHealthCheck(
                        key is null
                            ? sp.GetRequiredService<ObsWebSocketClient>()
                            : sp.GetRequiredKeyedService<ObsWebSocketClient>(key)
                    ),
                    failureStatus,
                    tags
                )
            );

        return builder;
    }

    /// <summary>
    /// Registers the reconnect pipeline for this client.
    /// </summary>
    /// <param name="builder">The client to configure.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IObsWebSocketClientBuilder WithReconnectPipeline(
        this IObsWebSocketClientBuilder builder
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        _ = builder.Services.AddObsWebSocketReconnectPipeline();
        return builder;
    }

    /// <summary>
    /// Configures this client's options.
    /// </summary>
    /// <param name="builder">The client to configure.</param>
    /// <param name="configure">Applied to this client's options.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IObsWebSocketClientBuilder Configure(
        this IObsWebSocketClientBuilder builder,
        Action<ObsWebSocketClientOptions> configure
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        _ = builder
            .Services.AddOptions<ObsWebSocketClientOptions>(builder.Name ?? Options.DefaultName)
            .Configure(configure);

        return builder;
    }
}
