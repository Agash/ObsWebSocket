using Microsoft.Extensions.Options;

namespace ObsWebSocket.Core;

/// <summary>
/// Validates <see cref="ObsWebSocketClientOptions"/> when the client is resolved.
/// </summary>
/// <remarks>
/// Written out rather than using DataAnnotations, which reflects over the options type and is
/// not trim or Native AOT safe.
/// </remarks>
internal sealed class ObsWebSocketClientOptionsValidator
    : IValidateOptions<ObsWebSocketClientOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, ObsWebSocketClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        if (options.ServerUri is null)
        {
            failures.Add("ServerUri is required, for example ws://localhost:4455.");
        }
        else if (options.ServerUri.Scheme is not ("ws" or "wss"))
        {
            failures.Add(
                $"ServerUri scheme must be ws or wss, but was '{options.ServerUri.Scheme}'."
            );
        }

        if (options.HandshakeTimeoutMs <= 0)
        {
            failures.Add("HandshakeTimeoutMs must be greater than zero.");
        }

        if (options.RequestTimeoutMs <= 0)
        {
            failures.Add("RequestTimeoutMs must be greater than zero.");
        }

        if (options.InitialReconnectDelayMs < 0)
        {
            failures.Add("InitialReconnectDelayMs cannot be negative.");
        }

        if (options.MaxReconnectDelayMs < options.InitialReconnectDelayMs)
        {
            failures.Add("MaxReconnectDelayMs cannot be less than InitialReconnectDelayMs.");
        }

        if (options.ReconnectBackoffMultiplier < 1.0)
        {
            failures.Add("ReconnectBackoffMultiplier must be at least 1.0.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
