using ObsWebSocket.Core.Events;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Generated;

namespace ObsWebSocket.Core;

/// <summary>
/// The client level conveniences: waiting for an event, and sending a batch. Neither belongs to
/// one protocol category, so both stay on the client rather than on a category group.
/// </summary>
public static partial class ObsWebSocketClientHelpers
{
    extension(ObsWebSocketClient client)
    {
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
        /// <exception cref="ObsWebSocketTimeoutException">Thrown if the timeout elapses first.</exception>
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
        /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
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
        /// Builds and sends a batch in one call, for the common case where the references are
        /// captured in the same scope they are read in.
        /// </summary>
        /// <param name="build">Adds the requests to send.</param>
        /// <param name="executionType">How OBS should schedule the requests.</param>
        /// <param name="haltOnFailure">Whether OBS should stop at the first failing request.</param>
        /// <param name="timeoutMs">Optional override for the request timeout.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>The results, addressable by reference or by position.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
        public Task<BatchResults> CallBatchAsync(
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
                builder,
                executionType,
                haltOnFailure,
                timeoutMs,
                cancellationToken
            );
        }
    }
}
