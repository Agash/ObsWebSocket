using ObsWebSocket.Core.Protocol;

namespace ObsWebSocket.Core;

/// <summary>
/// Identifies a request within a batch, without a response type.
/// </summary>
/// <param name="Index">Position of the request in the batch.</param>
public readonly record struct BatchRef(int Index);

/// <summary>
/// Identifies a request within a batch, carrying the type of its response.
/// </summary>
/// <typeparam name="TResponse">The response record this request produces.</typeparam>
/// <param name="Index">Position of the request in the batch.</param>
public readonly record struct BatchRef<TResponse>(int Index)
    where TResponse : class
{
    /// <summary>Drops the response type, leaving a plain reference.</summary>
    /// <param name="reference">The reference to convert.</param>
    public static implicit operator BatchRef(BatchRef<TResponse> reference) => new(reference.Index);

    /// <summary>Drops the response type, leaving a plain reference.</summary>
    public BatchRef ToBatchRef() => new(Index);
}

/// <summary>
/// The results of a batch call, addressable by the references the builder handed out.
/// </summary>
/// <remarks>
/// Indexing by a <see cref="BatchRef{TResponse}"/> ties a result to the request that produced it
/// and to that request's response type, so neither the position nor the type has to be restated
/// at the call site.
/// </remarks>
public sealed class BatchResults : IReadOnlyList<RequestResponsePayload<object>>
{
    private readonly IReadOnlyList<RequestResponsePayload<object>> _results;

    private readonly bool _payloadsTrustworthy;

    /// <summary>Initializes results from the payloads OBS returned.</summary>
    /// <param name="results">The results, in submission order.</param>
    /// <param name="payloadsTrustworthy">
    /// Whether each result's data belongs to the request it is attached to. False for parallel
    /// execution, where OBS pairs every result with another request's response.
    /// </param>
    public BatchResults(
        IReadOnlyList<RequestResponsePayload<object>> results,
        bool payloadsTrustworthy = true
    )
    {
        ArgumentNullException.ThrowIfNull(results);
        _results = results;
        _payloadsTrustworthy = payloadsTrustworthy;
    }

    /// <summary>Number of results returned.</summary>
    /// <remarks>
    /// Fewer than the number of requests sent when OBS stopped early on a failure.
    /// </remarks>
    public int Count => _results.Count;

    /// <summary>The raw results, in submission order.</summary>
    public IReadOnlyList<RequestResponsePayload<object>> Raw => _results;

    /// <summary>Gets the result for a request by position.</summary>
    /// <param name="index">Position of the request in the batch.</param>
    public RequestResponsePayload<object> this[int index] => _results[index];

    /// <summary>Gets the result for a request that returns no data.</summary>
    /// <param name="reference">The reference the builder returned.</param>
    /// <exception cref="ObsWebSocketException">Thrown if the batch stopped before this request ran.</exception>
    public RequestResponsePayload<object> this[BatchRef reference] => Require(reference.Index);

    /// <summary>Gets the typed response for a request.</summary>
    /// <typeparam name="TResponse">The response record for that request, inferred from the reference.</typeparam>
    /// <param name="reference">The reference the builder returned.</param>
    /// <exception cref="ObsWebSocketException">Thrown if the batch stopped before this request ran.</exception>
    /// <exception cref="ObsWebSocketRequestException">Thrown if OBS rejected that request.</exception>
    public TResponse Get<TResponse>(BatchRef<TResponse> reference)
        where TResponse : class => Require(reference.Index).GetRequiredData<TResponse>();

    /// <summary>
    /// Reads a typed response, reporting whether it was available rather than throwing.
    /// </summary>
    /// <typeparam name="TResponse">The response record for that request.</typeparam>
    /// <param name="reference">The reference the builder returned.</param>
    /// <param name="data">The response data, when one was returned.</param>
    public bool TryGet<TResponse>(BatchRef<TResponse> reference, out TResponse? data)
        where TResponse : class
    {
        if (!_payloadsTrustworthy || reference.Index >= _results.Count)
        {
            data = null;
            return false;
        }

        return _results[reference.Index].TryGetData(out data);
    }

    /// <inheritdoc/>
    public IEnumerator<RequestResponsePayload<object>> GetEnumerator() => _results.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        GetEnumerator();

    /// <summary>Returns whether every request in the batch succeeded.</summary>
    public bool AllSucceeded() => _results.All(r => r.RequestStatus.Result);

    /// <summary>Returns the results OBS reported as failed.</summary>
    public IEnumerable<RequestResponsePayload<object>> GetFailures() =>
        _results.Where(r => !r.RequestStatus.Result);

    private RequestResponsePayload<object> Require(int index)
    {
        if (!_payloadsTrustworthy)
        {
            throw new ObsWebSocketException(
                "OBS pairs each result with another request's response data when a batch runs with RequestBatchExecutionType.Parallel, so reading one by reference would return the wrong request's data. Its own response is already mis-paired, so this cannot be corrected here. Use a serial execution type, or read Raw and accept that the payloads are unreliable."
            );
        }

        return RequireCore(index);
    }

    private RequestResponsePayload<object> RequireCore(int index) =>
        index < _results.Count
            ? _results[index]
            : throw new ObsWebSocketException(
                $"The batch returned {_results.Count} result(s), so the request at position {index} never ran. This happens when haltOnFailure stopped the batch early."
            );
}
