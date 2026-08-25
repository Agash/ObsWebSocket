using ObsWebSocket.Core.Events;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Core;

/// <summary>
/// Output control, existence checks, volume, media transport, and event-wait conveniences.
/// </summary>
public static class ObsWebSocketClientConvenienceExtensions
{
    private static readonly TimeSpan s_defaultOutputTimeout = TimeSpan.FromSeconds(10);

    extension(ObsWebSocketClient client)
    {

        // ────────────────────────────────────────────────────────────────────────
        // Output control
        // ────────────────────────────────────────────────────────────────────────









        // ────────────────────────────────────────────────────────────────────────
        // Existence checks
        // ────────────────────────────────────────────────────────────────────────



        // ────────────────────────────────────────────────────────────────────────
        // Volume
        // ────────────────────────────────────────────────────────────────────────





        // ────────────────────────────────────────────────────────────────────────
        // Media transport
        // ────────────────────────────────────────────────────────────────────────











        // ────────────────────────────────────────────────────────────────────────
        // WaitForEventAsync overloads
        // ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Waits for the next occurrence of a typed OBS event, with no timeout. The wait ends only
        /// when the event arrives or <paramref name="cancellationToken"/> fires.
        /// </summary>
        /// <typeparam name="TEventArgs">The event args type to wait for.</typeparam>
        /// <param name="cancellationToken">A token to cancel the wait.</param>
        /// <returns>The event args.</returns>
        public Task<TEventArgs> WaitForEventAsync<TEventArgs>(
            CancellationToken cancellationToken = default
        )
            where TEventArgs : ObsEventArgs =>
            client.WaitForEventAsync<TEventArgs>(
                static _ => true,
                Timeout.InfiniteTimeSpan,
                cancellationToken
            );

        /// <summary>
        /// Waits for the next occurrence of a typed OBS event, giving up after <paramref name="timeout"/>.
        /// </summary>
        /// <typeparam name="TEventArgs">The event args type to wait for.</typeparam>
        /// <param name="timeout">How long to wait before giving up.</param>
        /// <param name="cancellationToken">A token to cancel the wait.</param>
        /// <returns>The event args.</returns>
        /// <exception cref="TimeoutException">Thrown if the timeout elapses first.</exception>
        public Task<TEventArgs> WaitForEventAsync<TEventArgs>(
            TimeSpan timeout,
            CancellationToken cancellationToken = default
        )
            where TEventArgs : ObsEventArgs =>
            client.WaitForEventAsync<TEventArgs>(static _ => true, timeout, cancellationToken);

        /// <summary>
        /// Waits for the next matching occurrence of a typed OBS event, with no timeout.
        /// </summary>
        /// <typeparam name="TEventArgs">The event args type to wait for.</typeparam>
        /// <param name="predicate">Returns <see langword="true"/> for the event to stop on.</param>
        /// <param name="cancellationToken">A token to cancel the wait.</param>
        /// <returns>The matching event args.</returns>
        public Task<TEventArgs> WaitForEventAsync<TEventArgs>(
            Func<TEventArgs, bool> predicate,
            CancellationToken cancellationToken = default
        )
            where TEventArgs : ObsEventArgs =>
            client.WaitForEventAsync(predicate, Timeout.InfiniteTimeSpan, cancellationToken);

        // ────────────────────────────────────────────────────────────────────────
        // Typed batch execution
        // ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Sends a batch built with <see cref="ObsBatchBuilder"/>, returning results addressable
        /// by the references the builder handed out.
        /// </summary>
        /// <param name="batch">The batch to send.</param>
        /// <param name="executionType">How OBS should schedule the requests.</param>
        /// <param name="haltOnFailure">Whether OBS should stop at the first failing request.</param>
        /// <param name="timeoutMs">Optional override for the request timeout.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>The results, addressable by reference or by position.</returns>
        public async Task<BatchResults> CallBatchAsync(
            ObsBatchBuilder batch,
            RequestBatchExecutionType? executionType = null,
            bool? haltOnFailure = null,
            int? timeoutMs = null,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(batch);

            List<RequestResponsePayload<object>> results = await client
                .CallBatchAsync(
                    batch.Build(),
                    executionType,
                    haltOnFailure,
                    timeoutMs,
                    cancellationToken
                )
                .ConfigureAwait(false);

            // OBS mis-pairs result payloads under parallel execution.
            return new BatchResults(
                results,
                payloadsTrustworthy: executionType != RequestBatchExecutionType.Parallel
            );
        }

        /// <summary>
        /// Builds and sends a batch using the typed builder, so each request type is paired with
        /// its own data record instead of a loose string and object.
        /// </summary>
        /// <param name="build">Adds the requests to send.</param>
        /// <param name="executionType">How OBS should schedule the requests.</param>
        /// <param name="haltOnFailure">Whether OBS should stop at the first failing request.</param>
        /// <param name="timeoutMs">Optional override for the request timeout.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>One result per request, in order.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
        public Task<List<RequestResponsePayload<object>>> CallBatchAsync(
            Action<ObsBatchBuilder> build,
            RequestBatchExecutionType? executionType = null,
            bool? haltOnFailure = null,
            int? timeoutMs = null,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(build);

            ObsBatchBuilder builder = new();
            build(builder);
            return client.CallBatchAsync(
                builder.Build(),
                executionType,
                haltOnFailure,
                timeoutMs,
                cancellationToken
            );
        }

        // Scene item ids are Number on the wire, so the generated surface uses double.










    }
}
