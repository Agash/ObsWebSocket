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
    /// <returns>The original <see cref="IServiceCollection"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="services"/> is null.</exception>
    public static IServiceCollection AddObsWebSocketClient(
        this IServiceCollection services,
        Action<ObsWebSocketClientOptions>? configureOptions = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        // Ensure Options infrastructure is registered
        services.AddOptions();

        // Configure options if an action is provided
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }

        services.TryAddSingleton<JsonMessageSerializer>();
        services.TryAddSingleton<MsgPackMessageSerializer>();

        // This factory determines which concrete serializer to use based on options.
        services.AddSingleton<IWebSocketMessageSerializer>(sp =>
        {
            ObsWebSocketClientOptions options = sp.GetRequiredService<
                IOptions<ObsWebSocketClientOptions>
            >().Value;
            return options.Format switch
            {
                SerializationFormat.MsgPack => sp.GetRequiredService<MsgPackMessageSerializer>(),
                SerializationFormat.Json or _ => sp.GetRequiredService<JsonMessageSerializer>(), // Default to JSON
            };
        });

        services.TryAddSingleton<IWebSocketConnectionFactory, WebSocketConnectionFactory>();

        services.TryAddSingleton(sp =>
        {
            ILogger<ObsWebSocketClient> logger = sp.GetRequiredService<
                ILogger<ObsWebSocketClient>
            >();
            IWebSocketMessageSerializer serializer =
                sp.GetRequiredService<IWebSocketMessageSerializer>();
            IOptions<ObsWebSocketClientOptions> options = sp.GetRequiredService<
                IOptions<ObsWebSocketClientOptions>
            >();
            IWebSocketConnectionFactory factory =
                sp.GetRequiredService<IWebSocketConnectionFactory>();

            // Pass dependencies to the constructor
            return new ObsWebSocketClient(logger, serializer, options, factory);
        });

        return services;
    }
}
