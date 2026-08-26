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
    public async Task TriggerHotkeyAsync(string hotkeyName,
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
}
