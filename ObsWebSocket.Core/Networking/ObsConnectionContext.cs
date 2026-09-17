using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Core.Networking;

/// <summary>
/// One connection to OBS: its socket, the serializer for the sub-protocol that socket negotiated,
/// the settings it was built from, its cancellation, and the handshake it is waiting on.
/// </summary>
/// <remarks>
/// Replaced wholesale rather than mutated, so a connection cannot hold a serializer that disagrees
/// with its own settings.
/// </remarks>
internal sealed class ObsConnectionContext : IAsyncDisposable
{
    private readonly CancellationTokenSource _closed;
    private int _disposed;

    /// <summary>Builds a context for one connection attempt.</summary>
    /// <param name="transport">The socket this connection runs on.</param>
    /// <param name="serializer">The serializer matching <paramref name="settings"/>' format.</param>
    /// <param name="settings">The settings this connection was built from.</param>
    /// <param name="clientLifetime">The client-wide token, so disposing the client ends this too.</param>
    public ObsConnectionContext(
        IWebSocketConnection transport,
        IWebSocketMessageSerializer serializer,
        ObsConnectionSettings settings,
        CancellationToken clientLifetime
    )
    {
        Transport = transport;
        Serializer = serializer;
        Settings = settings;
        _closed = CancellationTokenSource.CreateLinkedTokenSource(clientLifetime);
        Hello = new TaskCompletionSource<object>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        Identified = new TaskCompletionSource<object>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
    }

    /// <summary>The socket this connection runs on.</summary>
    public IWebSocketConnection Transport { get; }

    /// <summary>The serializer for the sub-protocol this connection negotiated.</summary>
    public IWebSocketMessageSerializer Serializer { get; }

    /// <summary>The settings this connection was established with.</summary>
    public ObsConnectionSettings Settings { get; }

    /// <summary>Signalled when this connection ends, for any reason.</summary>
    public CancellationToken ConnectionClosed => _closed.Token;

    /// <summary>Completed by the receive loop when OBS sends its <c>Hello</c>.</summary>
    public TaskCompletionSource<object> Hello { get; }

    /// <summary>Completed by the receive loop on <c>Identified</c>.</summary>
    /// <remarks>
    /// Replaced per re-identification, which is sound only because re-identification is single
    /// flight: <c>Identified</c> carries no request id, so concurrent waiters are indistinguishable.
    /// </remarks>
    public TaskCompletionSource<object> Identified { get; set; }

    /// <summary>The loop draining <see cref="Transport"/>, once started.</summary>
    public Task? ReceiveTask { get; set; }

    /// <summary>Ends the connection and tears the socket down without awaiting the receive loop.</summary>
    /// <remarks>
    /// Callable from inside the receive loop, where a server-initiated close lands.
    /// </remarks>
    public void Close()
    {
        try
        {
            _closed.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down concurrently; the token is cancelled either way.
        }

        try
        {
            Transport.Abort();
        }
        catch
        {
            // A socket that already faulted is the normal path here.
        }

        try
        {
            Transport.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Disposed by a concurrent teardown.
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Close();

        if (ReceiveTask is { } receiveTask)
        {
            // Guarantees no receive loop outlives the connection it reads. Faults belong to the
            // attempt's owner.
            await receiveTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _closed.Dispose();
    }
}
