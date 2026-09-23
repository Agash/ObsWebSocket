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
/// Conveniences for the <c>General</c> category, alongside its generated requests.
/// </summary>
public readonly partial struct GeneralGroup
{
    /// <summary>
    /// Triggers an OBS hotkey by its canonical name (e.g., "OBSWebSocket.StartStream").
    /// </summary>
    /// <param name="hotkeyName">The canonical name of the hotkey.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if OBS fails to trigger the hotkey (e.g., hotkey not found).</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task TriggerHotkeyAsync(
        string hotkeyName,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(hotkeyName);
        client.EnsureConnected();

        await client
            .General.TriggerHotkeyByNameAsync(
                new TriggerHotkeyByNameRequestData(hotkeyName: hotkeyName), // contextName defaults to null/Any
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Calls a request a third-party plugin registered with obs-websocket, with typed data both
    /// ways.
    /// </summary>
    /// <typeparam name="TRequest">The request data the vendor expects.</typeparam>
    /// <typeparam name="TResponse">The response data the vendor returns.</typeparam>
    /// <param name="vendorName">The vendor the request belongs to.</param>
    /// <param name="requestType">The vendor's request type.</param>
    /// <param name="requestData">The data to send.</param>
    /// <param name="requestTypeInfo">Metadata for <typeparamref name="TRequest"/>.</param>
    /// <param name="responseTypeInfo">Metadata for <typeparamref name="TResponse"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The vendor's response data, or <see langword="default"/> when it sent none.</returns>
    /// <exception cref="ObsWebSocketException">Thrown if OBS or the vendor rejects the request, or the data cannot be serialized or read.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task<TResponse?> CallVendorRequestAsync<TRequest, TResponse>(
        string vendorName,
        string requestType,
        TRequest requestData,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(vendorName);
        ArgumentException.ThrowIfNullOrEmpty(requestType);
        ArgumentNullException.ThrowIfNull(requestTypeInfo);
        ArgumentNullException.ThrowIfNull(responseTypeInfo);
        client.EnsureConnected();

        JsonElement data = ObsWebSocketClientOperations.SerializeFreeForm(
            requestData,
            requestTypeInfo,
            "requestData"
        );

        CallVendorRequestResponseData response = await client
            .General.CallVendorRequestAsync(
                new CallVendorRequestRequestData(
                    vendorName: vendorName,
                    requestType: requestType,
                    requestData: data
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        return response.GetResponseData(responseTypeInfo);
    }

    /// <summary>
    /// Broadcasts a <c>CustomEvent</c> carrying a caller-defined payload to every client
    /// subscribed to general events.
    /// </summary>
    /// <typeparam name="T">The payload's type.</typeparam>
    /// <param name="eventData">The payload to send.</param>
    /// <param name="typeInfo">Metadata for <typeparamref name="T"/>, typically from your own <c>JsonSerializerContext</c>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if OBS returns an error or serialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task BroadcastCustomEventAsync<T>(
        T eventData,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        client.EnsureConnected();

        JsonElement data = ObsWebSocketClientOperations.SerializeFreeForm(
            eventData,
            typeInfo,
            "eventData"
        );

        await client
            .General.BroadcastCustomEventAsync(
                new BroadcastCustomEventRequestData(eventData: data),
                cancellationToken
            )
            .ConfigureAwait(false);
    }
}
