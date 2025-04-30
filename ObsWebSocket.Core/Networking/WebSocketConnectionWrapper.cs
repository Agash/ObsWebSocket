using System.Net.WebSockets;

namespace ObsWebSocket.Core.Networking;

/// <summary>
/// Concrete implementation of IWebSocketConnection wrapping a ClientWebSocket.
/// </summary>
internal sealed class WebSocketConnectionWrapper(ClientWebSocket clientWebSocket)
    : IWebSocketConnection
{
    private readonly ClientWebSocket _webSocket = clientWebSocket;

    public WebSocketState State => _webSocket.State;
    public WebSocketCloseStatus? CloseStatus => _webSocket.CloseStatus;
    public string? CloseStatusDescription => _webSocket.CloseStatusDescription;
    public string? SubProtocol => _webSocket.SubProtocol;
    public ClientWebSocketOptions Options => _webSocket.Options;

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
        _webSocket.ConnectAsync(uri, cancellationToken);

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken
    ) => _webSocket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

    public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken
    ) => _webSocket.ReceiveAsync(buffer, cancellationToken);

    public Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken
    ) => _webSocket.CloseAsync(closeStatus, statusDescription, cancellationToken);

    public Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken
    ) => _webSocket.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public void Abort() => _webSocket.Abort();

    public void Dispose() => _webSocket.Dispose();
}
