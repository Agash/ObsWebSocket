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
/// Conveniences for the <c>Filters</c> category, alongside its generated requests.
/// </summary>
public readonly partial struct FiltersGroup
{
    /// <summary>
    /// Retrieves the settings for a specific filter on a source and deserializes them using an explicit <see cref="JsonTypeInfo{T}"/>.
    /// Suitable for both library-defined and consumer-defined settings types.
    /// </summary>
    /// <typeparam name="T">The C# type to deserialize the filter settings into.</typeparam>
    /// <param name="sourceName">The name of the source.</param>
    /// <param name="filterName">The name of the filter.</param>
    /// <param name="typeInfo">The JSON type metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The deserialized settings, or null if the source/filter is not found or deserialization fails.</returns>
    /// <exception cref="ObsWebSocketException">Thrown for OBS errors other than 'ResourceNotFound'.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task<T?> GetSourceFilterSettingsAsync<T>(string sourceName,
        string filterName,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        ArgumentException.ThrowIfNullOrEmpty(filterName);
        ArgumentNullException.ThrowIfNull(typeInfo);
        client.EnsureConnected();

        GetSourceFilterResponseData? filterInfo;
        try
        {
            filterInfo = await client
                .Filters.GetSourceFilterAsync(
                    new GetSourceFilterRequestData(sourceName: sourceName, filterName: filterName),
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (ObsWebSocketException ex)
            when (ex.Message.Contains("NotFound", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains(
                    $"code {(int)Core.Protocol.Generated.RequestStatus.ResourceNotFound}:",
                    StringComparison.Ordinal
                )
            )
        {
            return null;
        }

        if (filterInfo?.FilterSettings == null)
        {
            return null;
        }

        try
        {
            return filterInfo.FilterSettings.Value.Deserialize(typeInfo);
        }
        catch (JsonException jsonEx)
        {
            client._logger.LogError(
                jsonEx,
                "Failed to deserialize filter settings for '{FilterName}' on '{SourceName}' to type {TypeName}.",
                filterName,
                sourceName,
                typeof(T).Name
            );
            return null;
        }
    }

    /// <summary>
    /// Retrieves the settings for a specific filter on a source. The type must be registered in <c>ObsWebSocketJsonContext</c>.
    /// </summary>
    /// <typeparam name="T">The C# type to deserialize the filter settings into. Must be a library-registered settings type.</typeparam>
    /// <param name="sourceName">The name of the source.</param>
    /// <param name="filterName">The name of the filter.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The deserialized settings, or null if the source/filter is not found or deserialization fails.</returns>
    /// <exception cref="ObsWebSocketException">Thrown for OBS errors other than 'ResourceNotFound'.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public Task<T?> GetSourceFilterSettingsAsync<T>(string sourceName,
        string filterName,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        JsonTypeInfo<T> typeInfo = ObsWebSocketClientHelpers.GetRegisteredTypeInfo<T>();
        return client.Filters.GetSourceFilterSettingsAsync(sourceName, filterName, typeInfo, cancellationToken);
    }

    /// <summary>
    /// Sets the settings for a specific filter on a source using a strongly-typed settings object and an explicit <see cref="JsonTypeInfo{T}"/>.
    /// Suitable for both library-defined and consumer-defined settings types.
    /// </summary>
    /// <typeparam name="T">The C# type representing the filter settings.</typeparam>
    /// <param name="sourceName">The name of the source.</param>
    /// <param name="filterName">The name of the filter.</param>
    /// <param name="settings">The settings object to apply.</param>
    /// <param name="typeInfo">The JSON type metadata for <typeparamref name="T"/>.</param>
    /// <param name="overlay">True (default) to merge settings; false to reset to defaults and then apply.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if OBS fails or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task SetSourceFilterSettingsAsync<T>(string sourceName,
        string filterName,
        T settings,
        JsonTypeInfo<T> typeInfo,
        bool overlay = true,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        ArgumentException.ThrowIfNullOrEmpty(filterName);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(typeInfo);
        client.EnsureConnected();

        JsonElement settingsElement;
        try
        {
            settingsElement = JsonSerializer.SerializeToElement(settings, typeInfo);
        }
        catch (JsonException jsonEx)
        {
            throw new ObsWebSocketException(
                $"Failed to serialize settings object of type '{typeof(T).Name}' for filter '{filterName}'.",
                jsonEx
            );
        }

        await client
            .Filters.SetSourceFilterSettingsAsync(new SetSourceFilterSettingsRequestData(
                    filterSettings: settingsElement,
                    sourceName: sourceName,
                    filterName: filterName,
                    overlay: overlay
                ),
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the settings for a specific filter on a source using a strongly-typed settings object. The type must be registered in <c>ObsWebSocketJsonContext</c>.
    /// </summary>
    /// <typeparam name="T">The C# type representing the filter settings. Must be a library-registered settings type.</typeparam>
    /// <param name="sourceName">The name of the source.</param>
    /// <param name="filterName">The name of the filter.</param>
    /// <param name="settings">The settings object to apply.</param>
    /// <param name="overlay">True (default) to merge settings; false to reset to defaults and then apply.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if the type is not registered, OBS returns an error, or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public Task SetSourceFilterSettingsAsync<T>(string sourceName,
        string filterName,
        T settings,
        bool overlay = true,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        JsonTypeInfo<T> typeInfo = ObsWebSocketClientHelpers.GetRegisteredTypeInfo<T>();
        return client.Filters.SetSourceFilterSettingsAsync(sourceName, filterName, settings, typeInfo, overlay, cancellationToken);
    }

    /// <summary>
    /// Creates a new filter on a source with strongly-typed settings and an explicit <see cref="JsonTypeInfo{T}"/>.
    /// Suitable for both library-defined and consumer-defined settings types.
    /// </summary>
    /// <typeparam name="T">The C# type representing the filter settings.</typeparam>
    /// <param name="sourceName">The name of the source to add the filter to.</param>
    /// <param name="filterName">The name for the new filter.</param>
    /// <param name="filterKind">The kind of filter to create (e.g., "gain_filter").</param>
    /// <param name="settings">The initial settings for the filter.</param>
    /// <param name="typeInfo">The JSON type metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if OBS fails or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task CreateSourceFilterAsync<T>(string sourceName,
        string filterName,
        string filterKind,
        T settings,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        ArgumentException.ThrowIfNullOrEmpty(filterName);
        ArgumentException.ThrowIfNullOrEmpty(filterKind);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(typeInfo);
        client.EnsureConnected();

        JsonElement settingsElement;
        try
        {
            settingsElement = JsonSerializer.SerializeToElement(settings, typeInfo);
        }
        catch (JsonException jsonEx)
        {
            throw new ObsWebSocketException(
                $"Failed to serialize settings object of type '{typeof(T).Name}' for filter '{filterName}'.",
                jsonEx
            );
        }

        await client
            .Filters.CreateSourceFilterAsync(new CreateSourceFilterRequestData(
                    filterName: filterName,
                    filterKind: filterKind,
                    sourceName: sourceName,
                    filterSettings: settingsElement
                ),
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a new filter on a source with strongly-typed settings. The type must be registered in <c>ObsWebSocketJsonContext</c>.
    /// </summary>
    /// <typeparam name="T">The C# type representing the filter settings. Must be a library-registered settings type.</typeparam>
    /// <param name="sourceName">The name of the source to add the filter to.</param>
    /// <param name="filterName">The name for the new filter.</param>
    /// <param name="filterKind">The kind of filter to create (e.g., "gain_filter").</param>
    /// <param name="settings">The initial settings for the filter.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if the type is not registered, OBS returns an error, or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public Task CreateSourceFilterAsync<T>(string sourceName,
        string filterName,
        string filterKind,
        T settings,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        JsonTypeInfo<T> typeInfo = ObsWebSocketClientHelpers.GetRegisteredTypeInfo<T>();
        return client.Filters.CreateSourceFilterAsync(sourceName, filterName, filterKind, settings, typeInfo, cancellationToken);
    }

    /// <summary>
    /// Gets the default settings for a source filter kind as a strongly-typed object.
    /// </summary>
    /// <typeparam name="T">The C# type to deserialize default filter settings into.</typeparam>
    /// <param name="filterKind">The identifier of the filter kind (e.g., "gain_filter").</param>
    /// <param name="typeInfo">The <see cref="JsonTypeInfo{T}"/> for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The deserialized default settings, or <see langword="null"/> if no settings are present.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if OBS returns an error or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task<T?> GetSourceFilterDefaultSettingsAsync<T>(string filterKind,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(filterKind);
        ArgumentNullException.ThrowIfNull(typeInfo);
        client.EnsureConnected();

        GetSourceFilterDefaultSettingsResponseData? response = await client
            .Filters.GetSourceFilterDefaultSettingsAsync(new GetSourceFilterDefaultSettingsRequestData(filterKind: filterKind),
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);

        return response?.DefaultFilterSettings is not { } element ? null : JsonSerializer.Deserialize(element, typeInfo);
    }

    /// <summary>
    /// Gets the default settings for a source filter kind as a strongly-typed object. The type must be registered in <c>ObsWebSocketJsonContext</c>.
    /// </summary>
    /// <typeparam name="T">The C# type representing the default filter settings. Must be a library-registered settings type.</typeparam>
    /// <param name="filterKind">The identifier of the filter kind (e.g., "gain_filter").</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The deserialized default settings, or <see langword="null"/> if no settings are present.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if the type is not registered, OBS returns an error, or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public Task<T?> GetSourceFilterDefaultSettingsAsync<T>(string filterKind,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        JsonTypeInfo<T> typeInfo = ObsWebSocketClientHelpers.GetRegisteredTypeInfo<T>();
        return client.Filters.GetSourceFilterDefaultSettingsAsync(filterKind, typeInfo, cancellationToken);
    }
}
