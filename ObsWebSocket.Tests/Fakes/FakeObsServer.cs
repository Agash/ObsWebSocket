using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ObsWebSocket.Core.Networking;

namespace ObsWebSocket.Tests.Fakes;

/// <summary>
/// An OBS that answers on the wire, in memory. It speaks the JSON sub-protocol the client
/// negotiates, so a test drives the real handshake, receive loop and request correlation without
/// a running OBS.
/// </summary>
/// <remarks>
/// Requests are answered by handlers registered with <see cref="OnRequest"/>; anything else gets
/// an empty success. A handler is free to raise events before it returns, which is how the helpers
/// that send a request and then wait for an event are exercised.
/// </remarks>
internal sealed class FakeObsServer : IWebSocketConnection, IWebSocketConnectionFactory
{
    private Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>();
    private readonly Dictionary<string, Func<JsonElement?, RequestOutcome>> _handlers = new(
        StringComparer.Ordinal
    );
    private readonly List<string> _requests = [];
    private readonly List<Uri> _connectedTo = [];
    private readonly Lock _gate = new();
    private readonly ClientWebSocket _optionsCarrier = new();
    private ReadOnlyMemory<byte> _pending;

    /// <summary>Password the identify payload has to authenticate against, if any.</summary>
    public string? Password { get; init; }

    /// <summary>Close code sent instead of Identified, when the handshake is meant to fail.</summary>
    public int? RejectIdentifyWith { get; init; }

    /// <summary>Leaves a re-identify unanswered, as a stalled OBS would.</summary>
    public bool IgnoreReidentify { get; set; }

    /// <summary>Refuses every connection, as when OBS is not running.</summary>
    public bool RefuseConnections { get; set; }

    /// <summary>The endpoints connected to, in order.</summary>
    public IReadOnlyList<Uri> ConnectedTo
    {
        get
        {
            using (_gate.EnterScope())
            {
                return [.. _connectedTo];
            }
        }
    }

    /// <summary>Request types received, in order.</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            using (_gate.EnterScope())
            {
                return [.. _requests];
            }
        }
    }

    /// <inheritdoc/>
    public WebSocketState State { get; private set; } = WebSocketState.None;

    /// <inheritdoc/>
    public WebSocketCloseStatus? CloseStatus { get; private set; }

    /// <inheritdoc/>
    public string? CloseStatusDescription { get; private set; }

    /// <inheritdoc/>
    public string? SubProtocol => "obswebsocket.json";

    /// <inheritdoc/>
    public ClientWebSocketOptions Options => _optionsCarrier.Options;

    /// <summary>Answers <paramref name="requestType"/> with <paramref name="handler"/>.</summary>
    /// <param name="requestType">The OBS request type to answer.</param>
    /// <param name="handler">Reads the request data and returns the response.</param>
    public FakeObsServer OnRequest(string requestType, Func<JsonElement?, RequestOutcome> handler)
    {
        using (_gate.EnterScope())
        {
            _handlers[requestType] = handler;
        }

        return this;
    }

    /// <summary>Answers <paramref name="requestType"/> with a fixed success payload.</summary>
    /// <param name="requestType">The OBS request type to answer.</param>
    /// <param name="responseJson">The <c>responseData</c> object, as JSON.</param>
    public FakeObsServer Returns(string requestType, string responseJson) =>
        OnRequest(requestType, _ => RequestOutcome.Success(responseJson));

    /// <summary>Answers <paramref name="requestType"/> with a failure status.</summary>
    /// <param name="requestType">The OBS request type to answer.</param>
    /// <param name="code">The request status code OBS would report.</param>
    public FakeObsServer Fails(string requestType, int code) =>
        OnRequest(requestType, _ => RequestOutcome.Failure(code));

    /// <summary>Pushes a frame to the client exactly as written.</summary>
    /// <param name="message">The frame body.</param>
    public void PushRaw(string message) => Push(message);

    /// <summary>Pushes an event to the client.</summary>
    /// <param name="eventType">The OBS event type.</param>
    /// <param name="eventDataJson">The <c>eventData</c> object, as JSON.</param>
    public void RaiseEvent(string eventType, string eventDataJson = "{}") =>
        Push(
            "{\"op\":5,\"d\":{\"eventType\":\""
                + eventType
                + "\",\"eventIntent\":0,\"eventData\":"
                + eventDataJson
                + "}}"
        );

    /// <summary>Closes the connection from the server side.</summary>
    /// <param name="code">The close code to report.</param>
    /// <param name="description">The close reason to report.</param>
    public void CloseFromServer(int code, string description = "closed by the server")
    {
        CloseStatus = (WebSocketCloseStatus)code;
        CloseStatusDescription = description;
        State = WebSocketState.CloseReceived;
        _outbound.Writer.TryWrite([]);
    }

    /// <inheritdoc/>
    public IWebSocketConnection CreateConnection() => this;

    /// <inheritdoc/>
    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (RefuseConnections)
        {
            throw new WebSocketException(WebSocketError.Faulted, "Connection refused.");
        }

        using (_gate.EnterScope())
        {
            _connectedTo.Add(uri);
        }

        // A reconnect gets a fresh queue: the previous one was completed when that socket went.
        if (_outbound.Reader.Completion.IsCompleted)
        {
            _outbound = Channel.CreateUnbounded<byte[]>();
        }

        _pending = default;
        CloseStatus = null;
        CloseStatusDescription = null;
        State = WebSocketState.Open;

        string authentication = Password is null
            ? "null"
            : """{"challenge":"challenge","salt":"salt"}""";
        Push(
            "{\"op\":0,\"d\":{\"obsWebSocketVersion\":\"5.7.0\",\"rpcVersion\":1,\"authentication\":"
                + authentication
                + "}}"
        );

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken
    )
    {
        using JsonDocument document = JsonDocument.Parse(buffer);
        JsonElement root = document.RootElement;
        int op = root.GetProperty("op").GetInt32();
        JsonElement data = root.GetProperty("d");

        switch (op)
        {
            case 3 when IgnoreReidentify:
                break;

            case 1
            or 3: // Identify and Reidentify
                if (RejectIdentifyWith is { } rejection)
                {
                    CloseFromServer(rejection, "Authentication failed.");
                    break;
                }

                Push("""{"op":2,"d":{"negotiatedRpcVersion":1}}""");
                break;

            case 6:
                Respond(data);
                break;

            case 8:
                RespondToBatch(data);
                break;

            default:
                break;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        if (_pending.IsEmpty)
        {
            _pending = await _outbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_pending.IsEmpty)
        {
            State = WebSocketState.CloseReceived;
            return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        int count = Math.Min(buffer.Length, _pending.Length);
        _pending[..count].CopyTo(buffer);
        _pending = _pending[count..];

        return new ValueWebSocketReceiveResult(count, WebSocketMessageType.Text, _pending.IsEmpty);
    }

    /// <inheritdoc/>
    public Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken
    )
    {
        State = WebSocketState.Closed;
        _ = _outbound.Writer.TryComplete();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken
    ) => CloseAsync(closeStatus, statusDescription, cancellationToken);

    /// <inheritdoc/>
    public void Abort()
    {
        State = WebSocketState.Aborted;
        _ = _outbound.Writer.TryComplete();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _ = _outbound.Writer.TryComplete();
        _optionsCarrier.Dispose();
    }

    private void Respond(JsonElement data)
    {
        string requestType = data.GetProperty("requestType").GetString()!;
        string requestId = data.GetProperty("requestId").GetString()!;
        // Cloned: the document this element points into is gone by the time a handler reads it.
        JsonElement? requestData = data.TryGetProperty("requestData", out JsonElement payload)
            ? payload.Clone()
            : null;

        Func<JsonElement?, RequestOutcome>? handler;
        using (_gate.EnterScope())
        {
            _requests.Add(requestType);
            _ = _handlers.TryGetValue(requestType, out handler);
        }

        RequestOutcome outcome = handler?.Invoke(requestData) ?? RequestOutcome.Success("{}");
        if (outcome == RequestOutcome.NoReply)
        {
            return;
        }

        Push($$"""{"op":7,"d":{{Result(requestType, requestId, outcome)}}}""");
    }

    private void RespondToBatch(JsonElement data)
    {
        string batchId = data.GetProperty("requestId").GetString()!;
        List<string> results = [];

        foreach (JsonElement item in data.GetProperty("requests").EnumerateArray())
        {
            string requestType = item.GetProperty("requestType").GetString()!;
            string requestId = item.TryGetProperty("requestId", out JsonElement id)
                ? id.GetString()!
                : batchId;
            JsonElement? requestData = item.TryGetProperty("requestData", out JsonElement payload)
                ? payload.Clone()
                : null;

            Func<JsonElement?, RequestOutcome>? handler;
            using (_gate.EnterScope())
            {
                _requests.Add(requestType);
                _ = _handlers.TryGetValue(requestType, out handler);
            }

            RequestOutcome outcome = handler?.Invoke(requestData) ?? RequestOutcome.Success("{}");
            results.Add(Result(requestType, requestId, outcome));
        }

        Push(
            "{\"op\":9,\"d\":{\"requestId\":\""
                + batchId
                + "\",\"results\":["
                + string.Join(",", results)
                + "]}}"
        );
    }

    private static string Result(string requestType, string requestId, RequestOutcome outcome)
    {
        string status =
            $$"""{"result":{{(outcome.Code == 100 ? "true" : "false")}},"code":{{outcome.Code}}}""";
        string responseData = outcome.ResponseJson is null
            ? ""
            : $""","responseData":{outcome.ResponseJson}""";

        return $$"""{"requestType":"{{requestType}}","requestId":"{{requestId}}","requestStatus":{{status}}{{responseData}}}""";
    }

    private void Push(string message) =>
        _ = _outbound.Writer.TryWrite(Encoding.UTF8.GetBytes(message));

    /// <summary>What the server answers one request with.</summary>
    /// <param name="Code">The OBS request status code.</param>
    /// <param name="ResponseJson">The response data as JSON, or null for none.</param>
    internal readonly record struct RequestOutcome(int Code, string? ResponseJson)
    {
        /// <summary>A successful response carrying <paramref name="responseJson"/>.</summary>
        /// <param name="responseJson">The response data as JSON.</param>
        public static RequestOutcome Success(string? responseJson = "{}") => new(100, responseJson);

        /// <summary>No response at all, as when OBS goes quiet mid-request.</summary>
        public static RequestOutcome NoReply { get; } = new(0, null);

        /// <summary>A failed response carrying <paramref name="code"/>.</summary>
        /// <param name="code">The OBS request status code.</param>
        public static RequestOutcome Failure(int code) => new(code, null);
    }
}
