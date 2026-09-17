using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Core;

/// <summary>
/// Extension methods for setting up ObsWebSocketClient in an <see cref="IServiceCollection"/>.
/// </summary>
public static class ObsWebSocketServiceCollectionExtensions
{
    /// <summary>
    /// Adds the ObsWebSocketClient and its required dependencies to the specified <see cref="IServiceCollection"/>.
    /// The consuming application is responsible for registering logging services (e.g., by calling `services.AddLogging()`).
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add the services to.</param>
    /// <param name="configureOptions">An optional action to configure the <see cref="ObsWebSocketClientOptions"/>.</param>
    /// <returns>A builder for configuring this client.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="services"/> is null.</exception>
    public static IObsWebSocketClientBuilder AddObsWebSocketClient(
        this IServiceCollection services,
        Action<ObsWebSocketClientOptions>? configureOptions = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        OptionsBuilder<ObsWebSocketClientOptions> optionsBuilder =
            services.AddOptions<ObsWebSocketClientOptions>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<ObsWebSocketClientOptions>,
                ObsWebSocketClientOptionsValidator
            >()
        );

        if (configureOptions is not null)
        {
            _ = optionsBuilder.Configure(configureOptions);
        }

        AddSharedServices(services);

        // For callers resolving a serializer directly; the client uses the factory per connection.
        _ = services.AddSingleton<IWebSocketMessageSerializer>(sp =>
            sp.GetRequiredService<ObsSerializerFactory>()(
                sp.GetRequiredService<IOptions<ObsWebSocketClientOptions>>().Value.Format
            )
        );

        // Resolve options through the monitor, so a configuration change is picked up rather
        // than the values captured when the container was built.
        _ = services.AddSingleton<IOptions<ObsWebSocketClientOptions>>(
            sp => new MonitorBackedOptions(
                sp.GetRequiredService<IOptionsMonitor<ObsWebSocketClientOptions>>(),
                name: null
            )
        );

        services.TryAddSingleton(sp => Create(sp, name: null));

        return new ObsWebSocketClientBuilder(services, name: null);
    }

    /// <summary>Registers what every client needs, named or not.</summary>
    /// <remarks>Shared so the named and unnamed registration paths cannot drift.</remarks>
    private static void AddSharedServices(IServiceCollection services)
    {
        services.TryAddSingleton<JsonMessageSerializer>();
        services.TryAddSingleton<MsgPackMessageSerializer>();
        services.TryAddSingleton<IWebSocketConnectionFactory, WebSocketConnectionFactory>();
        services.TryAddSingleton(TimeProvider.System);
        _ = services.AddMetrics();
        services.TryAddSingleton<ObsWebSocketMetrics>();

        services.TryAddSingleton<ObsSerializerFactory>(sp =>
            format =>
                format switch
                {
                    SerializationFormat.MsgPack =>
                        sp.GetRequiredService<MsgPackMessageSerializer>(),
                    SerializationFormat.Json or _ => sp.GetRequiredService<JsonMessageSerializer>(),
                }
        );
    }

    /// <summary>
    /// Adds a named ObsWebSocketClient, for applications driving more than one OBS instance.
    /// Resolve it with <c>[FromKeyedServices(name)]</c> or
    /// <see cref="ServiceProviderKeyedServiceExtensions.GetRequiredKeyedService{T}(IServiceProvider, object?)"/>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add the services to.</param>
    /// <param name="name">The key identifying this client.</param>
    /// <param name="configureOptions">An optional action to configure this client's options.</param>
    /// <returns>A builder for configuring this client.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="name"/> is null or empty.</exception>
    public static IObsWebSocketClientBuilder AddObsWebSocketClient(
        this IServiceCollection services,
        string name,
        Action<ObsWebSocketClientOptions>? configureOptions = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(name);

        OptionsBuilder<ObsWebSocketClientOptions> optionsBuilder =
            services.AddOptions<ObsWebSocketClientOptions>(name);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<ObsWebSocketClientOptions>,
                ObsWebSocketClientOptionsValidator
            >()
        );

        if (configureOptions is not null)
        {
            _ = optionsBuilder.Configure(configureOptions);
        }

        AddSharedServices(services);

        _ = services.AddKeyedSingleton(name, (sp, key) => Create(sp, (string)key!));

        return new ObsWebSocketClientBuilder(services, name);
    }

    /// <summary>
    /// Builds a client for one registration, named or not.
    /// </summary>
    /// <param name="services">The provider to resolve from.</param>
    /// <param name="name">The client's key, or <see langword="null"/> for the unnamed client.</param>
    private static ObsWebSocketClient Create(IServiceProvider services, string? name)
    {
        MonitorBackedOptions options = new(
            services.GetRequiredService<IOptionsMonitor<ObsWebSocketClientOptions>>(),
            name
        );

        // Surfaces misconfiguration at resolve rather than on the first connect. Does not
        // freeze anything; the client goes on reading the monitor per call.
        _ = options.Value;

        return new ObsWebSocketClient(
            services.GetRequiredService<ILogger<ObsWebSocketClient>>(),
            services.GetRequiredService<ObsSerializerFactory>(),
            options,
            services.GetRequiredService<IWebSocketConnectionFactory>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ObsWebSocketMetrics>()
        );
    }
}

/// <summary>
/// Presents one client's current monitored options as <see cref="IOptions{T}"/>.
/// </summary>
/// <param name="monitor">The monitor to read from.</param>
/// <param name="name">The client's key, or <see langword="null"/> for the unnamed client.</param>
internal sealed class MonitorBackedOptions(
    IOptionsMonitor<ObsWebSocketClientOptions> monitor,
    string? name
) : IOptions<ObsWebSocketClientOptions>
{
    /// <inheritdoc/>
    public ObsWebSocketClientOptions Value => monitor.Get(name ?? Options.DefaultName);
}
