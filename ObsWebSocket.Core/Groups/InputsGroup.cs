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
/// Conveniences for the <c>Inputs</c> category, alongside its generated requests.
/// </summary>
public readonly partial struct InputsGroup
{
    /// <summary>
    /// Sets the text content of a Text (GDI+, Freetype 2, Pango) source.
    /// </summary>
    /// <param name="inputName">The name of the Text source input.</param>
    /// <param name="text">The text content to set.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if OBS fails to set the text (e.g., input not found, not a text source).</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task SetInputTextAsync(string inputName,
        string text,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(inputName);
        ArgumentNullException.ThrowIfNull(text); // Allow empty string, but not null
        client.EnsureConnected();

        await client
            .Inputs.SetInputSettingsAsync(inputName: inputName,
                settings: new TextGdiPlusInputSettings(Text: text),
                overlay: true,
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);
        // Let ObsWebSocketException from the underlying call propagate
    }

    /// <summary>
    /// Sets the mute state for multiple audio inputs using a single batch request.
    /// </summary>
    /// <param name="inputMutes">An enumerable of tuples, where each tuple contains the input name (string) and desired mute state (bool: true=muted, false=unmuted).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A Task representing the completion of the batch request submission. Inspect logs for individual item failures.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if the batch request itself fails (e.g., timeout).</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    /// <exception cref="ArgumentNullException">Thrown if inputMutes is null.</exception>
    public async Task SetInputMutesAsync(IEnumerable<(string InputName, bool IsMuted)> inputMutes,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(inputMutes);
        client.EnsureConnected();

        List<BatchRequestItem> batchItems =
        [
            .. inputMutes.Select(im => new BatchRequestItem(
                RequestType: "SetInputMute",
                RequestData: new SetInputMuteRequestData(
                    inputName: im.InputName,
                    inputMuted: im.IsMuted
                )
            )),
        ];

        if (batchItems.Count == 0)
        {
            client._logger.LogDebug("SetInputMutesAsync called with empty list, nothing to do.");
            return; // Nothing to send
        }

        // Send batch, don't halt on failure
        List<RequestResponsePayload<object>> results = await client
            .CallBatchAsync(
                requests: batchItems,
                haltOnFailure: false,
                executionType: RequestBatchExecutionType.SerialRealtime, // Appropriate for simple state changes
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);

        // Optional: Log failures from results
        foreach (RequestResponsePayload<object> result in results)
        {
            if (!result.RequestStatus.Result)
            {
                // Attempt to find original input name (requires parsing RequestId or matching RequestData - complex)
                // For now, log the failed request type and ID
                client._logger.LogWarning(
                    "Failed batch item in SetInputMutesAsync: RequestType={ReqType}, RequestId={ReqId}, Code={Code}, Comment={Comment}",
                    result.RequestType,
                    result.RequestId,
                    result.RequestStatus.Code,
                    result.RequestStatus.Comment ?? "N/A"
                );
            }
        }
    }

    /// <summary>
    /// Retrieves and deserializes the settings for an input using an explicit <see cref="JsonTypeInfo{T}"/>.
    /// Suitable for both library-defined and consumer-defined settings types.
    /// </summary>
    /// <typeparam name="T">The C# type to deserialize the input settings into.</typeparam>
    /// <param name="inputName">The name of the input.</param>
    /// <param name="typeInfo">The JSON type metadata for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The deserialized settings, or null if the input is not found or deserialization fails.</returns>
    /// <exception cref="ObsWebSocketException">Thrown for unexpected OBS errors.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task<T?> GetInputSettingsAsync<T>(string inputName,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(inputName);
        ArgumentNullException.ThrowIfNull(typeInfo);
        client.EnsureConnected();

        GetInputSettingsResponseData? response;
        try
        {
            response = await client
                .Inputs.GetInputSettingsAsync(new GetInputSettingsRequestData(inputName: inputName),
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

        if (response?.InputSettings == null)
        {
            return null;
        }

        try
        {
            return response.InputSettings.Value.Deserialize(typeInfo);
        }
        catch (JsonException jsonEx)
        {
            client._logger.LogError(
                jsonEx,
                "Failed to deserialize input settings for '{InputName}' to type {TypeName}.",
                inputName,
                typeof(T).Name
            );
            return null;
        }
    }

    /// <summary>
    /// Retrieves and deserializes the settings for an input. The type must be registered in <c>ObsWebSocketJsonContext</c>.
    /// </summary>
    /// <typeparam name="T">The C# type to deserialize the input settings into. Must be a library-registered settings type.</typeparam>
    /// <param name="inputName">The name of the input.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The deserialized settings, or null if the input is not found or deserialization fails.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if the type is not registered or OBS returns an error.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public Task<T?> GetInputSettingsAsync<T>(string inputName,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        JsonTypeInfo<T> typeInfo = ObsWebSocketClientHelpers.GetRegisteredTypeInfo<T>();
        return client.Inputs.GetInputSettingsAsync(inputName, typeInfo, cancellationToken);
    }

    /// <summary>
    /// Sets the settings of an input using a strongly-typed settings object and an explicit <see cref="JsonTypeInfo{T}"/>.
    /// Suitable for both library-defined and consumer-defined settings types.
    /// </summary>
    /// <typeparam name="T">The C# type representing the input settings.</typeparam>
    /// <param name="inputName">The name of the input.</param>
    /// <param name="settings">The settings object to apply.</param>
    /// <param name="typeInfo">The JSON type metadata for <typeparamref name="T"/>.</param>
    /// <param name="overlay">True (default) to merge settings; false to reset to defaults and then apply.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if OBS fails or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task SetInputSettingsAsync<T>(string inputName,
        T settings,
        JsonTypeInfo<T> typeInfo,
        bool overlay = true,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(inputName);
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
                $"Failed to serialize settings object of type '{typeof(T).Name}' for input '{inputName}'.",
                jsonEx
            );
        }

        await client
            .Inputs.SetInputSettingsAsync(new SetInputSettingsRequestData(
                    inputSettings: settingsElement,
                    inputName: inputName,
                    overlay: overlay
                ),
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the settings of an input using a strongly-typed settings object. The type must be registered in <c>ObsWebSocketJsonContext</c>.
    /// </summary>
    /// <typeparam name="T">The C# type representing the input settings. Must be a library-registered settings type.</typeparam>
    /// <param name="inputName">The name of the input.</param>
    /// <param name="settings">The settings object to apply.</param>
    /// <param name="overlay">True (default) to merge settings; false to reset to defaults and then apply.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if the type is not registered, OBS returns an error, or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public Task SetInputSettingsAsync<T>(string inputName,
        T settings,
        bool overlay = true,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        JsonTypeInfo<T> typeInfo = ObsWebSocketClientHelpers.GetRegisteredTypeInfo<T>();
        return client.Inputs.SetInputSettingsAsync(inputName, settings, typeInfo, overlay, cancellationToken);
    }

    /// <summary>
    /// Creates a new input with strongly-typed settings and an explicit <see cref="JsonTypeInfo{T}"/>.
    /// Suitable for both library-defined and consumer-defined settings types.
    /// </summary>
    /// <typeparam name="T">The C# type representing the input settings.</typeparam>
    /// <param name="inputKind">The kind of input to create (e.g., "browser_source").</param>
    /// <param name="inputName">The name for the new input.</param>
    /// <param name="settings">The settings for the new input.</param>
    /// <param name="typeInfo">The JSON type metadata for <typeparamref name="T"/>.</param>
    /// <param name="sceneName">Optional: the name of the scene to add the input to.</param>
    /// <param name="sceneUuid">Optional: the UUID of the scene to add the input to.</param>
    /// <param name="sceneItemEnabled">Optional: initial enabled state of the resulting scene item.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The response data containing the new scene item ID, or null on failure.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if OBS fails or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task<CreateInputResponseData?> CreateInputAsync<T>(string inputKind,
        string inputName,
        T settings,
        JsonTypeInfo<T> typeInfo,
        string? sceneName = null,
        string? sceneUuid = null,
        bool? sceneItemEnabled = null,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(inputKind);
        ArgumentException.ThrowIfNullOrEmpty(inputName);
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
                $"Failed to serialize settings object of type '{typeof(T).Name}' for input '{inputName}'.",
                jsonEx
            );
        }

        return await client
            .Inputs.CreateInputAsync(new CreateInputRequestData(
                    inputName: inputName,
                    inputKind: inputKind,
                    sceneName: sceneName,
                    sceneUuid: sceneUuid,
                    inputSettings: settingsElement,
                    sceneItemEnabled: sceneItemEnabled
                ),
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a new input with strongly-typed settings. The type must be registered in <c>ObsWebSocketJsonContext</c>.
    /// </summary>
    /// <typeparam name="T">The C# type representing the input settings. Must be a library-registered settings type.</typeparam>
    /// <param name="inputKind">The kind of input to create (e.g., "browser_source").</param>
    /// <param name="inputName">The name for the new input.</param>
    /// <param name="settings">The settings for the new input.</param>
    /// <param name="sceneName">Optional: the name of the scene to add the input to.</param>
    /// <param name="sceneUuid">Optional: the UUID of the scene to add the input to.</param>
    /// <param name="sceneItemEnabled">Optional: initial enabled state of the resulting scene item.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The response data containing the new scene item ID, or null on failure.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if the type is not registered, OBS returns an error, or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public Task<CreateInputResponseData?> CreateInputAsync<T>(string inputKind,
        string inputName,
        T settings,
        string? sceneName = null,
        string? sceneUuid = null,
        bool? sceneItemEnabled = null,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        JsonTypeInfo<T> typeInfo = ObsWebSocketClientHelpers.GetRegisteredTypeInfo<T>();
        return client.Inputs.CreateInputAsync(inputKind, inputName, settings, typeInfo, sceneName, sceneUuid, sceneItemEnabled, cancellationToken);
    }

    /// <summary>
    /// Gets the default settings for an input kind as a strongly-typed object.
    /// </summary>
    /// <typeparam name="T">The C# type to deserialize default input settings into.</typeparam>
    /// <param name="inputKind">The identifier of the input kind (e.g., "browser_source").</param>
    /// <param name="typeInfo">The <see cref="JsonTypeInfo{T}"/> for <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The deserialized default settings, or <see langword="null"/> if no settings are present.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if OBS returns an error or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task<T?> GetInputDefaultSettingsAsync<T>(string inputKind,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(inputKind);
        ArgumentNullException.ThrowIfNull(typeInfo);
        client.EnsureConnected();

        GetInputDefaultSettingsResponseData? response = await client
            .Inputs.GetInputDefaultSettingsAsync(new GetInputDefaultSettingsRequestData(inputKind: inputKind),
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);

        return response?.DefaultInputSettings is not { } element ? null : JsonSerializer.Deserialize(element, typeInfo);
    }

    /// <summary>
    /// Gets the default settings for an input kind as a strongly-typed object. The type must be registered in <c>ObsWebSocketJsonContext</c>.
    /// </summary>
    /// <typeparam name="T">The C# type representing the default input settings. Must be a library-registered settings type.</typeparam>
    /// <param name="inputKind">The identifier of the input kind (e.g., "browser_source").</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The deserialized default settings, or <see langword="null"/> if no settings are present.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if the type is not registered, OBS returns an error, or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public Task<T?> GetInputDefaultSettingsAsync<T>(string inputKind,
        CancellationToken cancellationToken = default
    )
        where T : class
    {
        JsonTypeInfo<T> typeInfo = ObsWebSocketClientHelpers.GetRegisteredTypeInfo<T>();
        return client.Inputs.GetInputDefaultSettingsAsync(inputKind, typeInfo, cancellationToken);
    }

        /// <summary>
        /// Sets an input's volume in decibels. The underlying request accepts either decibels or
        /// a multiplier and fails when given neither.
        /// </summary>
        /// <param name="inputName">The name of the input.</param>
        /// <param name="volumeDb">The desired volume in dB. OBS accepts -100 through 26.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <exception cref="ObsWebSocketException">Thrown if OBS rejects the request.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
        public async Task SetInputVolumeDbAsync(
            string inputName,
            double volumeDb,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentException.ThrowIfNullOrEmpty(inputName);
            client.EnsureConnected();

            await client
                .Inputs.SetInputVolumeAsync(
                    new SetInputVolumeRequestData { InputName = inputName, InputVolumeDb = volumeDb },
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Sets an input's volume as a linear multiplier, where <c>1.0</c> is unity gain.
        /// </summary>
        /// <param name="inputName">The name of the input.</param>
        /// <param name="volumeMul">The desired volume multiplier. OBS accepts 0 through 20.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <exception cref="ObsWebSocketException">Thrown if OBS rejects the request.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
        public async Task SetInputVolumeMulAsync(
            string inputName,
            double volumeMul,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentException.ThrowIfNullOrEmpty(inputName);
            client.EnsureConnected();

            await client
                .Inputs.SetInputVolumeAsync(
                    new SetInputVolumeRequestData { InputName = inputName, InputVolumeMul = volumeMul },
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
}
