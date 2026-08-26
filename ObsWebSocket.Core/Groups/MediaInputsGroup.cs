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
/// Conveniences for the <c>MediaInputs</c> category, alongside its generated requests.
/// </summary>
public readonly partial struct MediaInputsGroup
{
    /// <summary>
    /// Triggers a media action on an input using the typed <see cref="MediaInputAction"/> enum
    /// rather than a protocol string constant.
    /// </summary>
    /// <param name="inputName">The name of the media input.</param>
    /// <param name="action">The transport action to perform.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="ObsWebSocketException">Thrown if OBS rejects the request.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task TriggerMediaActionAsync(
        string inputName,
        MediaInputAction action,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(inputName);
        client.EnsureConnected();

        await client
            .MediaInputs.TriggerMediaInputActionAsync(
                new TriggerMediaInputActionRequestData
                {
                    InputName = inputName,
                    MediaAction = action.ToWireValue(),
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>Plays a media input.</summary>
    public Task PlayMediaAsync(string inputName, CancellationToken cancellationToken = default) =>
        client.MediaInputs.TriggerMediaActionAsync(
            inputName,
            MediaInputAction.Play,
            cancellationToken
        );

    /// <summary>Pauses a media input.</summary>
    public Task PauseMediaAsync(string inputName, CancellationToken cancellationToken = default) =>
        client.MediaInputs.TriggerMediaActionAsync(
            inputName,
            MediaInputAction.Pause,
            cancellationToken
        );

    /// <summary>Stops a media input.</summary>
    public Task StopMediaAsync(string inputName, CancellationToken cancellationToken = default) =>
        client.MediaInputs.TriggerMediaActionAsync(
            inputName,
            MediaInputAction.Stop,
            cancellationToken
        );

    /// <summary>Restarts a media input from the beginning.</summary>
    public Task RestartMediaAsync(
        string inputName,
        CancellationToken cancellationToken = default
    ) =>
        client.MediaInputs.TriggerMediaActionAsync(
            inputName,
            MediaInputAction.Restart,
            cancellationToken
        );
}
