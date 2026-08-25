using System.Text.Json;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Core;

/// <summary>
/// Reads typed data out of the results of a batch call.
/// </summary>
/// <remarks>
/// <see cref="ObsWebSocketClient.CallBatchAsync(IEnumerable{BatchRequestItem}, Protocol.Generated.RequestBatchExecutionType?, bool?, int?, CancellationToken)"/>
/// returns results whose payloads are transport-shaped, because a batch may mix request types.
/// These helpers turn a result into the response record for its request.
/// </remarks>
public static class BatchResultExtensions
{
    /// <summary>
    /// Deserializes a batch result into the response record for its request type.
    /// </summary>
    /// <typeparam name="TResponse">The response record for the request that produced this result.</typeparam>
    /// <param name="result">The result to read.</param>
    /// <returns>The response data, or <see langword="null"/> when the request carried none.</returns>
    /// <exception cref="ObsWebSocketSerializationException">
    /// Thrown if the payload cannot be read as <typeparamref name="TResponse"/>.
    /// </exception>
    public static TResponse? GetData<TResponse>(this RequestResponsePayload<object> result)
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.ResponseData is null)
        {
            return null;
        }

        if (result.ResponseData is TResponse already)
        {
            return already;
        }

        // The MessagePack transport hands back the raw payload bytes, the JSON transport a
        // JsonElement, so a batch result has to be read according to which produced it.
        if (result.ResponseData is ReadOnlyMemory<byte> packed)
        {
            try
            {
                return MessagePack.MessagePackSerializer.Deserialize<TResponse>(
                    packed,
                    MsgPackMessageSerializer.s_msgPackOptions
                );
            }
            catch (MessagePack.MessagePackSerializationException ex)
            {
                throw new ObsWebSocketSerializationException(
                    $"Failed to read batch result for '{result.RequestType}' as {typeof(TResponse).Name}.",
                    ex
                );
            }
        }

        if (result.ResponseData is not JsonElement element)
        {
            throw new ObsWebSocketSerializationException(
                $"Batch result for '{result.RequestType}' carried {result.ResponseData.GetType().Name}, which cannot be read as {typeof(TResponse).Name}."
            );
        }

        // An absent payload arrives as default(JsonElement), which is Undefined rather than null.
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        try
        {
            return element.Deserialize(
                (System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse>)
                    ObsWebSocketJsonContext.Default.Options.GetTypeInfo(typeof(TResponse))
            );
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new ObsWebSocketSerializationException(
                $"Failed to read batch result for '{result.RequestType}' as {typeof(TResponse).Name}.",
                ex
            );
        }
    }

    /// <summary>
    /// Deserializes a batch result, throwing when the request reported failure or carried no data.
    /// </summary>
    /// <typeparam name="TResponse">The response record for the request that produced this result.</typeparam>
    /// <param name="result">The result to read.</param>
    /// <returns>The response data.</returns>
    /// <exception cref="ObsWebSocketRequestException">Thrown if OBS reported this request as failed.</exception>
    /// <exception cref="ObsWebSocketSerializationException">Thrown if the payload is missing or unreadable.</exception>
    public static TResponse GetRequiredData<TResponse>(this RequestResponsePayload<object> result)
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.RequestStatus.Result)
        {
            throw new ObsWebSocketRequestException(
                $"Batch request '{result.RequestType}' failed with code {result.RequestStatus.Code}: {result.RequestStatus.Comment ?? "No comment"}",
                result.RequestType,
                result.RequestId,
                result.RequestStatus,
                result.RequestStatus.Comment
            );
        }

        return result.GetData<TResponse>()
            ?? throw new ObsWebSocketSerializationException(
                $"Batch request '{result.RequestType}' succeeded but returned no {typeof(TResponse).Name} payload."
            );
    }

    /// <summary>
    /// Returns whether every request in the batch succeeded.
    /// </summary>
    /// <param name="results">The batch results.</param>
    public static bool AllSucceeded(this IEnumerable<RequestResponsePayload<object>> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return results.All(r => r.RequestStatus.Result);
    }

    /// <summary>
    /// Returns the results that OBS reported as failed.
    /// </summary>
    /// <param name="results">The batch results.</param>
    public static IEnumerable<RequestResponsePayload<object>> Failures(
        this IEnumerable<RequestResponsePayload<object>> results
    )
    {
        ArgumentNullException.ThrowIfNull(results);
        return results.Where(r => !r.RequestStatus.Result);
    }
}
