using System.Net.WebSockets;

namespace ObsWebSocket.Core.Networking;

/// <summary>
/// Abstraction for WebSocket operations required by ObsWebSocketClient.
/// Allows for mocking the WebSocket connection during testing.
/// </summary>
public interface IWebSocketConnection : IDisposable
{
    WebSocketState State { get; }
    WebSocketCloseStatus? CloseStatus { get; }
    string? CloseStatusDescription { get; }
    string? SubProtocol { get; }
    ClientWebSocketOptions Options { get; }

    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);
    ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken
    );
    ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken
    );
    Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken
    );
    Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken
    );
    void Abort();
}
