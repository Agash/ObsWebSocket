using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using ObsWebSocket.Core.Events;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Common;
using ObsWebSocket.Core.Protocol.Common.FilterSettings;
using ObsWebSocket.Core.Protocol.Common.InputSettings;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Core;

/// <summary>
/// Conveniences for the <c>Outputs</c> category, alongside its generated requests.
/// </summary>
public readonly partial struct OutputsGroup
{
    /// <summary>
    /// Gets the settings for an output as a strongly-typed object.
    /// </summary>
    /// <typeparam name="T">The C# type to deserialize output settings into.</typeparam>
    /// <param name="outputName">The name of the output.</param>
    /// <param name="typeInfo">The <see cref="JsonTypeInfo{T}"/> for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The deserialized output settings, or <see langword="null"/> if no settings are present.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if OBS returns an error or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task<T?> GetOutputSettingsAsync<T>(
        string outputName,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(outputName);
        ArgumentNullException.ThrowIfNull(typeInfo);
        client.EnsureConnected();

        GetOutputSettingsResponseData? response = await client
            .Outputs.GetOutputSettingsAsync(
                new GetOutputSettingsRequestData(outputName: outputName),
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);

        return response?.OutputSettings is not { } element
            ? null
            : JsonSerializer.Deserialize(element, typeInfo);
    }

    /// <summary>
    /// Gets the settings for an output as a strongly-typed object. The type must be registered in <c>ObsWebSocketJsonContext</c>.
    /// </summary>
    /// <typeparam name="T">The C# type representing the output settings. Must be a library-registered settings type.</typeparam>
    /// <param name="outputName">The name of the output.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The deserialized output settings, or <see langword="null"/> if no settings are present.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if the type is not registered, OBS returns an error, or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public Task<T?> GetOutputSettingsAsync<T>(
        string outputName,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        JsonTypeInfo<T> typeInfo = ObsWebSocketClientHelpers.GetRegisteredTypeInfo<T>();
        return client.Outputs.GetOutputSettingsAsync(outputName, typeInfo, cancellationToken);
    }

    /// <summary>
    /// Sets the settings for an output from a strongly-typed object.
    /// </summary>
    /// <typeparam name="T">The C# type representing the output settings.</typeparam>
    /// <param name="outputName">The name of the output.</param>
    /// <param name="settings">The settings to apply.</param>
    /// <param name="typeInfo">The <see cref="JsonTypeInfo{T}"/> for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if OBS returns an error or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task SetOutputSettingsAsync<T>(
        string outputName,
        T settings,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(outputName);
        ArgumentNullException.ThrowIfNull(typeInfo);
        client.EnsureConnected();

        JsonElement settingsElement = JsonSerializer.SerializeToElement(settings, typeInfo);

        await client
            .Outputs.SetOutputSettingsAsync(
                new SetOutputSettingsRequestData(
                    outputName: outputName,
                    outputSettings: settingsElement
                ),
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the settings for an output from a strongly-typed object. The type must be registered in <c>ObsWebSocketJsonContext</c>.
    /// </summary>
    /// <typeparam name="T">The C# type representing the output settings. Must be a library-registered settings type.</typeparam>
    /// <param name="outputName">The name of the output.</param>
    /// <param name="settings">The settings to apply.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if the type is not registered, OBS returns an error, or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public Task SetOutputSettingsAsync<T>(
        string outputName,
        T settings,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        JsonTypeInfo<T> typeInfo = ObsWebSocketClientHelpers.GetRegisteredTypeInfo<T>();
        return client.Outputs.SetOutputSettingsAsync(
            outputName,
            settings,
            typeInfo,
            cancellationToken
        );
    }

    /// <summary>
    /// Returns <see langword="true"/> if the OBS virtual camera output is currently active.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task<bool> IsVirtualCamActiveAsync(CancellationToken cancellationToken = default)
    {
        client.EnsureConnected();
        GetVirtualCamStatusResponseData? status = await client
            .Outputs.GetVirtualCamStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        return status?.OutputActive ?? false;
    }

    /// <summary>
    /// Starts or stops the virtual camera and waits until the
    /// <see cref="VirtualcamStateChangedEventArgs"/> confirms the desired state,
    /// or until <paramref name="timeout"/> elapses.
    /// </summary>
    /// <param name="activate"><see langword="true"/> to start the virtual camera; <see langword="false"/> to stop it.</param>
    /// <param name="timeout">
    /// Maximum time to wait for the state-change event.
    /// Defaults to 10 seconds.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The final <c>OutputActive</c> state reported by the event,
    /// or <see langword="null"/> if the timeout elapsed before the event arrived.
    /// </returns>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task<bool?> SetVirtualCamActiveAndWaitAsync(
        bool activate,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default
    )
    {
        client.EnsureConnected();

        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);

        // Set up the wait before issuing the command to avoid missing the event.
        Task<VirtualcamStateChangedEventArgs> waitTask =
            client.WaitForEventAsync<VirtualcamStateChangedEventArgs>(
                predicate: _ => true,
                timeout: effectiveTimeout,
                cancellationToken: cancellationToken
            );

        if (activate)
        {
            await client.Outputs.StartVirtualCamAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await client.Outputs.StopVirtualCamAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            VirtualcamStateChangedEventArgs ev = await waitTask.ConfigureAwait(false);
            return ev.EventData.OutputActive;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}
