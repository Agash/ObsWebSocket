using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace ObsWebSocket.Core;

/// <summary>
/// The resilience pipeline governing connection attempts.
/// </summary>
/// <remarks>
/// The pipeline is registered by name, so an application can replace the reconnect policy
/// wholesale by registering its own pipeline under <see cref="ReconnectPipelineKey"/> after
/// calling <c>AddObsWebSocketClient</c>, instead of being limited to the reconnect options.
/// </remarks>
public static class ObsWebSocketResilience
{
    /// <summary>Key the reconnect pipeline is registered under.</summary>
    public const string ReconnectPipelineKey = "obs-websocket-reconnect";

    /// <summary>
    /// Registers the default reconnect pipeline. Delays grow by
    /// <see cref="ObsWebSocketClientOptions.ReconnectBackoffMultiplier"/>, are capped at
    /// <see cref="ObsWebSocketClientOptions.MaxReconnectDelayMs"/>, and carry jitter so several
    /// clients recovering from one outage do not retry in lockstep.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddObsWebSocketReconnectPipeline(
        this IServiceCollection services
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        _ = services.AddResiliencePipeline(
            ReconnectPipelineKey,
            static (builder, context) =>
            {
                ObsWebSocketClientOptions options = context
                    .ServiceProvider.GetRequiredService<IOptions<ObsWebSocketClientOptions>>()
                    .Value;

                builder.TimeProvider =
                    context.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System;

                _ = builder.AddRetry(CreateRetryOptions(options));
            }
        );

        return services;
    }

    /// <summary>
    /// Builds the retry strategy described by the reconnect options.
    /// </summary>
    /// <param name="options">Options describing the retry behaviour.</param>
    internal static RetryStrategyOptions CreateRetryOptions(ObsWebSocketClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        double multiplier =
            options.ReconnectBackoffMultiplier > 1.0 ? options.ReconnectBackoffMultiplier : 1.0;
        double initialMs = options.InitialReconnectDelayMs;
        double maxMs = Math.Max(options.MaxReconnectDelayMs, initialMs);

        return new RetryStrategyOptions
        {
            // The connection loop decides how many attempts to make, because it also decides
            // which failures are fatal. This strategy supplies the delay between them.
            MaxRetryAttempts = int.MaxValue,
            UseJitter = true,
            Delay = TimeSpan.FromMilliseconds(initialMs),
            MaxDelay = TimeSpan.FromMilliseconds(maxMs),
            ShouldHandle = static args =>
                ValueTask.FromResult(
                    args.Outcome.Exception
                        is not null
                            and not AuthenticationFailureException
                            and not OperationCanceledException
                ),
            DelayGenerator = args =>
                ValueTask.FromResult<TimeSpan?>(
                    TimeSpan.FromMilliseconds(
                        Math.Min(initialMs * Math.Pow(multiplier, args.AttemptNumber), maxMs)
                    )
                ),
        };
    }
}
