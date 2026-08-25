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
/// Conveniences for the <c>Stream</c> category, alongside its generated requests.
/// </summary>
public readonly partial struct StreamRequestGroup
{
    private static readonly TimeSpan s_defaultOutputTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Starts or stops streaming and waits for OBS to confirm the state change.
        /// </summary>
        /// <param name="activate"><see langword="true"/> to start streaming; <see langword="false"/> to stop it.</param>
        /// <param name="timeout">Maximum time to wait for the state-change event. Defaults to 10 seconds.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>
        /// The state reported by the event, or <see langword="null"/> if the timeout elapsed first.
        /// </returns>
        /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
        public async Task<OutputState?> SetStreamActiveAndWaitAsync(
            bool activate,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default
        )
        {
            client.EnsureConnected();

            Task<StreamStateChangedEventArgs> waitTask = client.WaitForEventAsync<StreamStateChangedEventArgs>(
                predicate: _ => true,
                timeout: timeout ?? s_defaultOutputTimeout,
                cancellationToken: cancellationToken
            );

            if (activate)
            {
                await client.Stream.StartStreamAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.Stream.StopStreamAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                StreamStateChangedEventArgs ev = await waitTask.ConfigureAwait(false);
                return OutputStateExtensions.FromWireValue(ev.EventData.OutputState);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        /// <summary>
        /// Returns whether streaming is currently active.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
        public async Task<bool> IsStreamActiveAsync(
            CancellationToken cancellationToken = default
        )
        {
            client.EnsureConnected();
            GetStreamStatusResponseData? status = await client
                .Stream.GetStreamStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            return status?.OutputActive ?? false;
        }
}
