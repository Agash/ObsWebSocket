using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ObsWebSocket.Core.Events;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Common;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Protocol.Common.FilterSettings;
using ObsWebSocket.Core.Protocol.Common.InputSettings;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Core;

/// <summary>
/// Conveniences for the <c>Transitions</c> category, alongside its generated requests.
/// </summary>
public readonly partial struct TransitionsRequestGroup
{
    /// <summary>
    /// Gets the settings for the current scene transition as a strongly-typed object.
    /// </summary>
    /// <typeparam name="T">The C# type to deserialize transition settings into.</typeparam>
    /// <param name="typeInfo">The <see cref="JsonTypeInfo{T}"/> for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The deserialized transition settings, or <see langword="null"/> if no settings are present.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if OBS returns an error or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task<T?> GetCurrentSceneTransitionSettingsAsync<T>(JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        client.EnsureConnected();

        GetCurrentSceneTransitionResponseData? response = await client
            .Transitions.GetCurrentSceneTransitionAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return response?.TransitionSettings is not { } element ? null : JsonSerializer.Deserialize(element, typeInfo);
    }

    /// <summary>
    /// Gets the settings for the current scene transition as a strongly-typed object. The type must be registered in <c>ObsWebSocketJsonContext</c>.
    /// </summary>
    /// <typeparam name="T">The C# type representing the transition settings. Must be a library-registered settings type.</typeparam>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The deserialized transition settings, or <see langword="null"/> if no settings are present.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if the type is not registered, OBS returns an error, or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public Task<T?> GetCurrentSceneTransitionSettingsAsync<T>(CancellationToken cancellationToken = default
    )
        where T : class
    {
        JsonTypeInfo<T> typeInfo = ObsWebSocketClientHelpers.GetRegisteredTypeInfo<T>();
        return client.Transitions.GetCurrentSceneTransitionSettingsAsync(typeInfo, cancellationToken);
    }

    /// <summary>
    /// Sets the settings for the current scene transition from a strongly-typed object.
    /// </summary>
    /// <typeparam name="T">The C# type representing the transition settings.</typeparam>
    /// <param name="settings">The settings to apply.</param>
    /// <param name="typeInfo">The <see cref="JsonTypeInfo{T}"/> for <typeparamref name="T"/>.</param>
    /// <param name="overlay">If <see langword="true"/>, the provided settings are overlaid on top of the existing settings. Defaults to <see langword="true"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if OBS returns an error or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task SetCurrentSceneTransitionSettingsAsync<T>(T settings,
        JsonTypeInfo<T> typeInfo,
        bool? overlay = true,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        client.EnsureConnected();

        JsonElement settingsElement = JsonSerializer.SerializeToElement(settings, typeInfo);

        await client
            .Transitions.SetCurrentSceneTransitionSettingsAsync(new SetCurrentSceneTransitionSettingsRequestData(
                    transitionSettings: settingsElement,
                    overlay: overlay
                ),
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the settings for the current scene transition from a strongly-typed object. The type must be registered in <c>ObsWebSocketJsonContext</c>.
    /// </summary>
    /// <typeparam name="T">The C# type representing the transition settings. Must be a library-registered settings type.</typeparam>
    /// <param name="settings">The settings to apply.</param>
    /// <param name="overlay">If <see langword="true"/>, the provided settings are overlaid on top of the existing settings. Defaults to <see langword="true"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if the type is not registered, OBS returns an error, or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public Task SetCurrentSceneTransitionSettingsAsync<T>(T settings,
        bool? overlay = true,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        JsonTypeInfo<T> typeInfo = ObsWebSocketClientHelpers.GetRegisteredTypeInfo<T>();
        return client.Transitions.SetCurrentSceneTransitionSettingsAsync(settings, typeInfo, overlay, cancellationToken);
    }
}
