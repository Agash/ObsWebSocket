using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ObsWebSocket.Core.Protocol;

namespace ObsWebSocket.Core;

/// <summary>
/// Builds a batch of OBS requests where each request type is paired with its own data record,
/// so a request name can never be sent with the wrong payload.
/// </summary>
/// <remarks>
/// The generated methods are conveniences over <see cref="BatchRequestItem"/>. Anything they do
/// not cover can still be added with <see cref="Add(BatchRequestItem)"/>, and
/// <see cref="ObsWebSocketClient.CallBatchAsync"/> still accepts a plain list.
/// </remarks>
public sealed partial class ObsBatchBuilder
{
    private readonly List<BatchRequestItem> _items = [];

    /// <summary>
    /// The items accumulated so far, in the order they will be sent.
    /// </summary>
    /// <remarks>A snapshot; later additions to the builder are not reflected.</remarks>
    public IReadOnlyList<BatchRequestItem> Items => [.. _items];

    /// <summary>
    /// Appends a raw batch item. Use this for request types the generated methods do not cover,
    /// or when the payload is a hand-built <see cref="System.Text.Json.JsonElement"/>.
    /// </summary>
    /// <param name="item">The item to append.</param>
    /// <returns>The same builder, for chaining.</returns>
    public ObsBatchBuilder Add(BatchRequestItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
        return this;
    }

    /// <summary>
    /// Appends a request by name with optional data, matching the shape OBS expects on the wire.
    /// </summary>
    /// <param name="requestType">The OBS request type string.</param>
    /// <param name="requestData">The request payload, or <see langword="null"/> when it takes none.</param>
    /// <returns>The same builder, for chaining.</returns>
    public ObsBatchBuilder Add(string requestType, object? requestData = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestType);
        _items.Add(new BatchRequestItem(requestType, requestData));
        return this;
    }

    /// <summary>
    /// Appends a request whose payload is serialized with an explicit <see cref="JsonTypeInfo{T}"/>,
    /// so the call stays trim and Native AOT safe.
    /// </summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="requestType">The OBS request type string.</param>
    /// <param name="requestData">The request payload.</param>
    /// <param name="typeInfo">Serialization metadata for <typeparamref name="T"/>.</param>
    /// <returns>The same builder, for chaining.</returns>
    public ObsBatchBuilder Add<T>(string requestType, T requestData, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(requestType);
        ArgumentNullException.ThrowIfNull(requestData);
        ArgumentNullException.ThrowIfNull(typeInfo);

        _items.Add(
            new BatchRequestItem(
                requestType,
                JsonSerializer.SerializeToElement(requestData, typeInfo)
            )
        );
        return this;
    }

    /// <summary>
    /// Returns the accumulated items as the list <see cref="ObsWebSocketClient.CallBatchAsync"/> takes.
    /// </summary>
    public List<BatchRequestItem> Build() => [.. _items];
}
