using System.Net.WebSockets;

namespace ObsWebSocket.Core.Networking;

/// <summary>
/// Abstraction for WebSocket operations required by ObsWebSocketClient.
/// Allows for mocking the WebSocket connection during testing.
/// </summary>
public interface IWebSocketConnection : IDisposable
{
    /// <summary>
    /// Gets the current state of the WebSocket connection.
    /// </summary>
    WebSocketState State { get; }

    /// <summary>
    /// Gets the current close status of the WebSocket connection, if available.
    /// </summary>
    WebSocketCloseStatus? CloseStatus { get; }

    /// <summary>
    /// Gets the close status description of the WebSocket connection, if available.
    /// </summary>
    string? CloseStatusDescription { get; }

    /// <summary>
    /// Gets the Subprotocol used for the WebSocket connection.
    /// </summary>
    string? SubProtocol { get; }

    /// <summary>
    /// Gets the options used to configure the WebSocket connection.
    /// </summary>
    ClientWebSocketOptions Options { get; }

    /// <summary>
    /// Connects to the WebSocket server using the specified URI.
    /// </summary>
    /// <param name="uri"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a message to the WebSocket connection.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="messageType"></param>
    /// <param name="endOfMessage"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Receives a message from the WebSocket connection and returns the result.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Closes the WebSocket connection and sends a close frame to the server.
    /// </summary>
    /// <param name="closeStatus"></param>
    /// <param name="statusDescription"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Closes the output stream of the WebSocket connection without closing the connection itself.
    /// </summary>
    /// <param name="closeStatus"></param>
    /// <param name="statusDescription"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Aborts the WebSocket connection without sending a close frame.
    /// </summary>
    void Abort();
}
