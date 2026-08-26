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
/// Conveniences for the <c>Config</c> category, alongside its generated requests.
/// </summary>
public readonly partial struct ConfigGroup
{
    /// <summary>
    /// Gets the current stream service settings as a strongly-typed object. The service type string is discarded.
    /// </summary>
    /// <typeparam name="T">The C# type to deserialize stream service settings into.</typeparam>
    /// <param name="typeInfo">The <see cref="JsonTypeInfo{T}"/> for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The deserialized stream service settings, or <see langword="null"/> if no settings are present.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if OBS returns an error or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task<T?> GetStreamServiceSettingsAsync<T>(
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        client.EnsureConnected();

        GetStreamServiceSettingsResponseData? response = await client
            .Config.GetStreamServiceSettingsAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return response?.StreamServiceSettings is not { } element
            ? null
            : JsonSerializer.Deserialize(element, typeInfo);
    }

    /// <summary>
    /// Gets the current stream service settings as a strongly-typed object. The type must be registered in <c>ObsWebSocketJsonContext</c>.
    /// </summary>
    /// <typeparam name="T">The C# type representing the stream service settings. Must be a library-registered settings type.</typeparam>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The deserialized stream service settings, or <see langword="null"/> if no settings are present.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if the type is not registered, OBS returns an error, or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public Task<T?> GetStreamServiceSettingsAsync<T>(CancellationToken cancellationToken = default)
        where T : class
    {
        JsonTypeInfo<T> typeInfo = ObsWebSocketClientHelpers.GetRegisteredTypeInfo<T>();
        return client.Config.GetStreamServiceSettingsAsync(typeInfo, cancellationToken);
    }

    /// <summary>
    /// Sets the current stream service settings from a strongly-typed object.
    /// </summary>
    /// <typeparam name="T">The C# type representing the stream service settings.</typeparam>
    /// <param name="streamServiceType">The stream service type identifier (e.g., "rtmp_custom", "rtmp_common").</param>
    /// <param name="settings">The settings to apply.</param>
    /// <param name="typeInfo">The <see cref="JsonTypeInfo{T}"/> for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if OBS returns an error or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task SetStreamServiceSettingsAsync<T>(
        string streamServiceType,
        T settings,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(streamServiceType);
        ArgumentNullException.ThrowIfNull(typeInfo);
        client.EnsureConnected();

        JsonElement settingsElement = JsonSerializer.SerializeToElement(settings, typeInfo);

        await client
            .Config.SetStreamServiceSettingsAsync(
                new SetStreamServiceSettingsRequestData(
                    streamServiceType: streamServiceType,
                    streamServiceSettings: settingsElement
                ),
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the current stream service settings from a strongly-typed object. The type must be registered in <c>ObsWebSocketJsonContext</c>.
    /// </summary>
    /// <typeparam name="T">The C# type representing the stream service settings. Must be a library-registered settings type.</typeparam>
    /// <param name="streamServiceType">The stream service type identifier (e.g., "rtmp_custom", "rtmp_common").</param>
    /// <param name="settings">The settings to apply.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if the type is not registered, OBS returns an error, or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public Task SetStreamServiceSettingsAsync<T>(
        string streamServiceType,
        T settings,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        JsonTypeInfo<T> typeInfo = ObsWebSocketClientHelpers.GetRegisteredTypeInfo<T>();
        return client.Config.SetStreamServiceSettingsAsync(
            streamServiceType,
            settings,
            typeInfo,
            cancellationToken
        );
    }

    /// <summary>
    /// Ensures the specified Scene Collection is currently active. If not, attempts to switch to it.
    /// </summary>
    /// <param name="targetSceneCollectionName">The name of the desired scene collection.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>True if the target scene collection is active after the call; false if the switch failed (e.g., not found).</returns>
    /// <exception cref="ObsWebSocketException">Thrown for unexpected OBS errors during the process.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task<bool> EnsureSceneCollectionActiveAsync(
        string targetSceneCollectionName,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(targetSceneCollectionName);
        client.EnsureConnected();

        GetSceneCollectionListResponseData? currentResponse = await client
            .Config.GetSceneCollectionListAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (
            string.Equals(
                currentResponse?.CurrentSceneCollectionName,
                targetSceneCollectionName,
                StringComparison.Ordinal
            )
        )
        {
            return true; // Already active
        }

        // Need to switch
        try
        {
            await client
                .Config.SetCurrentSceneCollectionAsync(
                    new SetCurrentSceneCollectionRequestData(
                        sceneCollectionName: targetSceneCollectionName
                    ),
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
            return true; // Switch command sent successfully
        }
        // OBS answers a name it does not know with either status, depending on the request.
        catch (ObsWebSocketRequestException ex)
            when (ex.StatusCode
                    is RequestStatusCode.ResourceNotFound
                        or RequestStatusCode.InvalidRequestField
            )
        {
            client._logger.LogWarning(
                "Failed to set scene collection to '{TargetName}': Not found or invalid.",
                targetSceneCollectionName
            );
            return false; // Switch failed because target doesn't exist
        }
        // Let other exceptions propagate
    }

    /// <summary>
    /// Ensures the specified Profile is currently active. If not, attempts to switch to it.
    /// </summary>
    /// <param name="targetProfileName">The name of the desired profile.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>True if the target profile is active after the call; false if the switch failed (e.g., not found).</returns>
    /// <exception cref="ObsWebSocketException">Thrown for unexpected OBS errors during the process.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task<bool> EnsureProfileActiveAsync(
        string targetProfileName,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(targetProfileName);
        client.EnsureConnected();

        GetProfileListResponseData? currentResponse = await client
            .Config.GetProfileListAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (
            string.Equals(
                currentResponse?.CurrentProfileName,
                targetProfileName,
                StringComparison.Ordinal
            )
        )
        {
            return true; // Already active
        }

        // Need to switch
        try
        {
            await client
                .Config.SetCurrentProfileAsync(
                    new SetCurrentProfileRequestData(profileName: targetProfileName),
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
            return true; // Switch command sent successfully
        }
        // OBS answers a name it does not know with either status, depending on the request.
        catch (ObsWebSocketRequestException ex)
            when (ex.StatusCode
                    is RequestStatusCode.ResourceNotFound
                        or RequestStatusCode.InvalidRequestField
            )
        {
            client._logger.LogWarning(
                "Failed to set profile to '{TargetName}': Not found or invalid.",
                targetProfileName
            );
            return false; // Switch failed because target doesn't exist
        }
        // Let other exceptions propagate
    }
}
