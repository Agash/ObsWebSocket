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
/// Conveniences for the <c>Record</c> category, alongside its generated requests.
/// </summary>
public readonly partial struct RecordGroup
{
    private static readonly TimeSpan s_defaultOutputTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Starts or stops recording and waits for OBS to confirm the state change.
        /// </summary>
        /// <param name="activate"><see langword="true"/> to start recording; <see langword="false"/> to stop it.</param>
        /// <param name="timeout">Maximum time to wait for the state-change event. Defaults to 10 seconds.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>
        /// The state reported by the event, or <see langword="null"/> if the timeout elapsed first.
        /// </returns>
        /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
        public async Task<OutputState?> SetRecordActiveAndWaitAsync(
            bool activate,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default
        )
        {
            client.EnsureConnected();

            // Set up the wait before issuing the command to avoid missing the event.
            Task<RecordStateChangedEventArgs> waitTask = client.WaitForEventAsync<RecordStateChangedEventArgs>(
                predicate: _ => true,
                timeout: timeout ?? s_defaultOutputTimeout,
                cancellationToken: cancellationToken
            );

            if (activate)
            {
                await client.Record.StartRecordAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _ = await client.Record.StopRecordAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                RecordStateChangedEventArgs ev = await waitTask.ConfigureAwait(false);
                return OutputStateExtensions.FromWireValue(ev.EventData.OutputState);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        /// <summary>
        /// Returns whether recording is currently active.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
        public async Task<bool> IsRecordActiveAsync(
            CancellationToken cancellationToken = default
        )
        {
            client.EnsureConnected();
            GetRecordStatusResponseData? status = await client
                .Record.GetRecordStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            return status?.OutputActive ?? false;
        }
}
