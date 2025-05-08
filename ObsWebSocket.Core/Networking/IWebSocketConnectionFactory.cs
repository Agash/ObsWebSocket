using System.Net.WebSockets;

namespace ObsWebSocket.Core.Networking;

/// <summary>
/// Defines a factory for creating <see cref="IWebSocketConnection"/> instances.
/// Allows for dependency injection and mocking of WebSocket connections.
/// </summary>
public interface IWebSocketConnectionFactory
{
    /// <summary>
    /// Creates a new instance of an <see cref="IWebSocketConnection"/>.
    /// </summary>
    /// <returns>A new WebSocket connection wrapper.</returns>
    IWebSocketConnection CreateConnection();
}

/// <summary>
/// Default implementation of the <see cref="IWebSocketConnectionFactory"/> interface.
/// Creates instances of <see cref="WebSocketConnectionWrapper"/> wrapping a standard <see cref="System.Net.WebSockets.ClientWebSocket"/>.
/// </summary>
public class WebSocketConnectionFactory : IWebSocketConnectionFactory
{
    /// <summary>
    /// Creates a new instance of an <see cref="IWebSocketConnection"/> using a default <see cref="System.Net.WebSockets.ClientWebSocket"/>.
    /// </summary>
    /// <returns>A new <see cref="WebSocketConnectionWrapper"/>.</returns>
    public IWebSocketConnection CreateConnection() =>
        new WebSocketConnectionWrapper(new ClientWebSocket());
}
