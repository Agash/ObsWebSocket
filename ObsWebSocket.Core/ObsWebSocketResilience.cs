using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ObsWebSocket.Core.Protocol.Generated;
using Polly;
using Polly.Retry;

namespace ObsWebSocket.Core;

/// <summary>
/// Resilience pipelines the client executes through.
/// </summary>
/// <remarks>
/// Register a pipeline under the same key after <c>AddObsWebSocketClient</c> to replace the
/// default. Reconnect is not a pipeline: a clean disconnect is not an exception, so the
/// connection loop owns that control flow and takes only its delay curve from
/// <see cref="IObsReconnectDelays"/>.
/// </remarks>
public static class ObsWebSocketResilience
{
    /// <summary>Key the NotReady retry pipeline is registered under.</summary>
    public const string NotReadyPipelineKey = "obs-websocket-not-ready";

    /// <summary>
    /// Registers the default NotReady retry pipeline, described by
    /// <see cref="ObsWebSocketClientOptions.NotReadyRetry"/>.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddObsWebSocketNotReadyPipeline(
        this IServiceCollection services
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        _ = services.AddResiliencePipeline(
            NotReadyPipelineKey,
            static (builder, context) =>
            {
                ObsWebSocketClientOptions options = context
                    .ServiceProvider.GetRequiredService<IOptions<ObsWebSocketClientOptions>>()
                    .Value;

                builder.TimeProvider =
                    context.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System;

                if (options.NotReadyRetry.Enabled)
                {
                    _ = builder.AddRetry(CreateNotReadyRetryOptions(options.NotReadyRetry));
                }
            }
        );

        return services;
    }

    /// <summary>
    /// Builds the retry strategy for requests OBS refuses as not ready.
    /// </summary>
    /// <param name="options">Options describing the retry behaviour.</param>
    internal static RetryStrategyOptions CreateNotReadyRetryOptions(NotReadyRetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new RetryStrategyOptions
        {
            MaxRetryAttempts = Math.Max(options.MaxRetryAttempts, 1),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromMilliseconds(Math.Max(options.InitialDelayMs, 0)),
            MaxDelay = TimeSpan.FromMilliseconds(
                Math.Max(options.MaxDelayMs, options.InitialDelayMs)
            ),
            ShouldHandle = static args =>
                ValueTask.FromResult(
                    args.Outcome.Exception is ObsWebSocketRequestException request
                        && request.StatusCode == RequestStatusCode.NotReady
                ),
        };
    }

    /// <summary>
    /// Builds the retry strategy describing the reconnect backoff curve.
    /// </summary>
    /// <param name="backoffMultiplier">Growth applied per attempt.</param>
    /// <param name="initialDelayMs">Delay before the first retry.</param>
    /// <param name="maxDelayMs">Ceiling on the delay.</param>
    internal static RetryStrategyOptions CreateReconnectRetryOptions(
        double backoffMultiplier,
        int initialDelayMs,
        int maxDelayMs
    )
    {
        double multiplier = backoffMultiplier > 1.0 ? backoffMultiplier : 1.0;
        double initialMs = initialDelayMs;
        double maxMs = Math.Max(maxDelayMs, initialMs);

        return new RetryStrategyOptions
        {
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
