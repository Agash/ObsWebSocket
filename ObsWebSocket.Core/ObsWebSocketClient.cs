using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ObsWebSocket.Core.Events;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Events;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Core;

/// <summary>
/// Internal enum to track connection state for managing ConnectAsync/DisconnectAsync calls.
/// </summary>
internal enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Disconnecting,
}

/// <summary>
/// Client for interacting with the OBS Studio WebSocket v5 API using modern C#/.NET.
/// Provides methods for connecting, sending requests, handling responses, and receiving events.
/// Manages connection lifecycle, including automatic reconnection (if enabled).
/// </summary>
/// <remarks>
/// This client manages the WebSocket connection lifecycle, message serialization/deserialization,
/// request/response correlation, and event dispatching.
/// It is designed to be thread-safe for public operations after connection.
/// Use <see cref="ConnectAsync(CancellationToken)"/> to initiate the connection using configured options.
/// Register event handlers for connection states (e.g., <see cref="Connected"/>, <see cref="Disconnected"/>) and OBS events.
/// Remember to dispose of the client instance using <see cref="DisposeAsync"/> to ensure proper cleanup.
/// </remarks>
/// <remarks>
/// Initializes a new instance of the <see cref="ObsWebSocketClient"/> class.
/// </remarks>
public sealed partial class ObsWebSocketClient(
    ILogger<ObsWebSocketClient> logger,
    IWebSocketMessageSerializer serializer,
    IOptions<ObsWebSocketClientOptions> options,
    IWebSocketConnectionFactory? connectionFactory = null
) : IAsyncDisposable
{
    #region Fields
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IWebSocketMessageSerializer _serializer =
        serializer ?? throw new ArgumentNullException(nameof(serializer));
    private readonly IOptions<ObsWebSocketClientOptions> _options =
        options ?? throw new ArgumentNullException(nameof(options));
    private readonly IWebSocketConnectionFactory _connectionFactory =
        connectionFactory ?? new WebSocketConnectionFactory();
    public const int ReceiveBufferSize = 8192;
    public const int DefaultHandshakeTimeoutMs = 5000;
    public const int DefaultRequestTimeoutMs = 10000;
    public const int DefaultBatchTimeoutMultiplier = 2;

    private IWebSocketConnection? _webSocket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private volatile ConnectionState _connectionState = ConnectionState.Disconnected;
    private Task? _connectionLoopTask;
    private CancellationTokenSource? _clientLifetimeCts;
    private readonly Lock _connectionLock = new();
    private Exception? _completionException;

    private TaskCompletionSource<object>? _helloTcs;
    private TaskCompletionSource<object>? _identifiedTcs;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<object>> _pendingRequests =
        new();
    private readonly ConcurrentDictionary<
        string,
        TaskCompletionSource<object>
    > _pendingBatchRequests = new();

    private TaskCompletionSource _initialConnectionTcs = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private static readonly JsonSerializerOptions s_payloadJsonOptions = new(
        JsonSerializerDefaults.Web
    );
    #endregion

    #region Properties
    /// <summary>
    /// Gets a value indicating whether the client is currently connected and identified successfully.
    /// </summary>
    public bool IsConnected { get; private set; }

    /// <summary>
    /// Gets the RPC protocol version negotiated with the server during the last successful handshake.
    /// Returns <c>null</c> if the client is not connected or the handshake hasn't completed.
    /// </summary>
    public int? NegotiatedRpcVersion { get; private set; }

    /// <summary>
    /// Gets the event subscription flags that were last successfully acknowledged by the server
    /// via an <c>Identified</c> message (either initial connection or after <see cref="ReidentifyAsync"/>).
    /// Returns <c>null</c> if the client is not connected or the handshake hasn't completed.
    /// See <see cref="ObsWebSocket.Core.Protocol.Generated.EventSubscription"/> for flag values.
    /// </summary>
    public uint? CurrentEventSubscriptions { get; private set; }
    #endregion

    #region Connection State Events
    /// <inheritdoc/>
    public event EventHandler<ConnectingEventArgs>? Connecting;

    /// <inheritdoc/>
    public event EventHandler? Connected;

    /// <inheritdoc/>
    public event EventHandler<DisconnectedEventArgs>? Disconnected;

    /// <inheritdoc/>
    public event EventHandler<ConnectionFailedEventArgs>? ConnectionFailed;

    /// <inheritdoc/>
    public event EventHandler<AuthenticationFailureEventArgs>? AuthenticationFailure;
    #endregion

    // Generated OBS event fields reside in ObsWebSocketClient.Events.g.cs

    #region Public API Methods

    /// <inheritdoc/>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObsWebSocketClientOptions currentOptions = _options.Value;
        ArgumentNullException.ThrowIfNull(
            currentOptions.ServerUri,
            nameof(currentOptions.ServerUri)
        );
        if (currentOptions.ReconnectBackoffMultiplier < 1.0)
        {
            currentOptions.ReconnectBackoffMultiplier = 1.0;
        }

        Task? loopTask;
        TaskCompletionSource currentInitialConnectionTcs; // Capture the TCS for this specific call

        using (_connectionLock.EnterScope())
        {
            if (_connectionState != ConnectionState.Disconnected)
            {
                throw new InvalidOperationException($"Client is already {_connectionState}.");
            }

            _connectionState = ConnectionState.Connecting;
            _clientLifetimeCts?.Dispose();
            _clientLifetimeCts = new CancellationTokenSource();
            _completionException = null;
            _initialConnectionTcs = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            ); // Create a new TCS
            currentInitialConnectionTcs = _initialConnectionTcs; // Capture it

            loopTask = _connectionLoopTask = Task.Run(
                () => ConnectionLoopAsync(currentOptions, cancellationToken),
                CancellationToken.None
            );
        }

        _logger.LogInformation(
            "Starting connection sequence for {Uri}...",
            currentOptions.ServerUri
        );

        try
        {
            // Await the initial connection TCS, not the whole loop task
            using CancellationTokenSource linkedTimeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            int overallTimeout = Math.Max(
                options.Value.HandshakeTimeoutMs * 2,
                DefaultRequestTimeoutMs
            ); // Be generous
            linkedTimeoutCts.CancelAfter(TimeSpan.FromMilliseconds(overallTimeout));

            await currentInitialConnectionTcs
                .Task.WaitAsync(linkedTimeoutCts.Token)
                .ConfigureAwait(false);

            // If TCS completed successfully, connection is established.
            _logger.LogInformation("ConnectAsync initial connection confirmed successfully.");
        }
        catch (Exception ex) // Catches exceptions set on the TCS or cancellation
        {
            _logger.LogError(ex, "ConnectAsync failed to establish initial connection.");
            // Ensure finalization happens if the TCS faulted, loop might still be running/cleaning up
            if (
                _connectionState
                is not ConnectionState.Disconnected
                    and not ConnectionState.Disconnecting
            )
            {
                // Don't wait indefinitely, trigger finalize and let it run
                _ = FinalizeDisconnectionAsync(
                    WebSocketCloseStatus.InternalServerError,
                    "Initial connection failed.",
                    ex
                );
            }

            throw; // Rethrow the exception that caused the failure (from TCS or cancellation)
        }
    }

    /// <inheritdoc/>
    public async Task ReidentifyAsync(
        uint? eventSubscriptions = null,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default
    )
    {
        EnsureConnected();
        Debug.Assert(_clientLifetimeCts != null);

        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _clientLifetimeCts.Token
        );

        _identifiedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously); // Reset for re-identify
        int effectiveTimeout = timeoutMs ?? _options.Value.RequestTimeoutMs;

        try
        {
            await SendMessageAsync(
                    WebSocketOpCode.Reidentify,
                    new ReidentifyPayload(eventSubscriptions),
                    linkedCts.Token
                )
                .ConfigureAwait(false);

            _logger.LogDebug(
                "Waiting for Identified after Reidentify (Timeout: {TimeoutMs}ms)...",
                effectiveTimeout
            );
            object identifiedMessageObj = await WaitForHandshakeMessageAsync(
                    _identifiedTcs,
                    effectiveTimeout,
                    "Identified (after Reidentify)",
                    linkedCts.Token
                )
                .ConfigureAwait(false);
            IdentifiedPayload identifiedPayload = ExtractPayloadFromHandshake<IdentifiedPayload>(
                identifiedMessageObj,
                "Identified (after Reidentify)"
            );

            _logger.LogInformation(
                "Re-identification successful. RPC Version: {RpcVersion}",
                identifiedPayload.NegotiatedRpcVersion
            );

            NegotiatedRpcVersion = identifiedPayload.NegotiatedRpcVersion;
            CurrentEventSubscriptions = eventSubscriptions; // Store the flags that were just acknowledged
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "ReidentifyAsync failed.");
            _identifiedTcs?.TrySetException(ex);
            throw ex is ObsWebSocketException or OperationCanceledException
                ? ex
                : new ObsWebSocketException($"Reidentify failed: {ex.Message}", ex);
        }
        catch (OperationCanceledException) when (_clientLifetimeCts.IsCancellationRequested)
        {
            _logger.LogWarning("ReidentifyAsync canceled due to client shutdown.");
            _identifiedTcs?.TrySetCanceled(_clientLifetimeCts.Token);
            throw;
        }
        finally
        {
            _identifiedTcs = null;
        }
    }

    /// <summary>
    /// Sends a request to the OBS WebSocket server and awaits its response.
    /// Use this for requests expected to return complex objects (classes/records).
    /// </summary>
    /// <typeparam name="TResponse">The expected type of the response data payload (must be a reference type).</typeparam>
    /// <param name="requestType">The OBS WebSocket request type string.</param>
    /// <param name="requestData">Optional data payload for the request. Should be serializable to the format expected by OBS for the request type.</param>
    /// <param name="timeoutMs">Optional timeout in milliseconds to wait for the response. Defaults to <see cref="ObsWebSocketClientOptions.RequestTimeoutMs"/>.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>
    /// A task representing the asynchronous operation. Yields the deserialized response data payload
    /// (<see cref="TResponse"/>), or <c>null</c> if the successful response does not contain data.
    /// </returns>
    /// <exception cref="ObsWebSocketException">Thrown if the request fails on the OBS side (indicated by the response status) or if serialization/deserialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    /// <exception cref="OperationCanceledException">Thrown if the operation is cancelled via the <paramref name="cancellationToken"/> or the request times out.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="requestType"/> is null or empty.</exception>
    public async Task<TResponse?> CallAsync<TResponse>(
        string requestType,
        object? requestData = null,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default
    )
        where TResponse : class
    {
        ArgumentException.ThrowIfNullOrEmpty(requestType);
        EnsureConnected();
        Debug.Assert(_clientLifetimeCts != null);

        string requestId = Guid.NewGuid().ToString();
        TaskCompletionSource<object> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(requestId, tcs))
        {
            throw new ObsWebSocketException($"Duplicate request ID: {requestId}");
        }

        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _clientLifetimeCts.Token
        );

        try
        {
            await SendMessageAsync(
                    WebSocketOpCode.Request,
                    new RequestPayload(
                        requestType,
                        requestId,
                        SerializeRequestData(requestType, requestData)
                    ),
                    linkedCts.Token
                )
                .ConfigureAwait(false);

            int effectiveTimeout = timeoutMs ?? _options.Value.RequestTimeoutMs;
            _logger.LogDebug(
                "Waiting for Response {RequestId} ({RequestType}, Timeout: {TimeoutMs}ms)...",
                requestId,
                requestType,
                effectiveTimeout
            );

            object responseObj = await WaitForResponseAsync(
                    tcs,
                    effectiveTimeout,
                    $"Request '{requestType}' ({requestId})",
                    linkedCts.Token
                )
                .ConfigureAwait(false);
            RequestResponsePayload<object> response = CastResponsePayload<
                RequestResponsePayload<object>
            >(responseObj, "RequestResponse");

            ProcessResponseStatus(response.RequestStatus, requestType, requestId);
            return _serializer.DeserializePayload<TResponse>(response.ResponseData);
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                ex,
                "CallAsync failed for {RequestType} ({RequestId})",
                requestType,
                requestId
            );
            tcs.TrySetException(ex); // Ensure TCS is completed on failure
            throw;
        }
        catch (OperationCanceledException) when (_clientLifetimeCts.IsCancellationRequested)
        {
            _logger.LogWarning(
                "CallAsync for {RequestType} ({RequestId}) canceled.",
                requestType,
                requestId
            );
            tcs.TrySetCanceled(_clientLifetimeCts.Token);
            throw;
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Sends a request to the OBS WebSocket server and awaits its response.
    /// Use this for requests expected to return value types (structs).
    /// </summary>
    /// <typeparam name="TResponse">The expected type of the response data payload (must be a value type).</typeparam>
    /// <param name="requestType">The OBS WebSocket request type string.</param>
    /// <param name="requestData">Optional data payload for the request. Should be serializable to the format expected by OBS for the request type.</param>
    /// <param name="timeoutMs">Optional timeout in milliseconds to wait for the response. Defaults to <see cref="ObsWebSocketClientOptions.RequestTimeoutMs"/>.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>
    /// A task representing the asynchronous operation. Yields a nullable <see cref="TResponse"/> containing the
    /// deserialized response data payload, or <c>null</c> if the successful response does not contain data.
    /// </returns>
    /// <exception cref="ObsWebSocketException">Thrown if the request fails on the OBS side (indicated by the response status), if serialization/deserialization fails, or if a null value is deserialized for a non-nullable struct.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    /// <exception cref="OperationCanceledException">Thrown if the operation is cancelled via the <paramref name="cancellationToken"/> or the request times out.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="requestType"/> is null or empty.</exception>
    public async Task<TResponse?> CallAsyncValue<TResponse>(
        string requestType,
        object? requestData = null,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default
    )
        where TResponse : struct
    {
        ArgumentException.ThrowIfNullOrEmpty(requestType);
        EnsureConnected();
        Debug.Assert(_clientLifetimeCts != null);

        string requestId = Guid.NewGuid().ToString();
        TaskCompletionSource<object> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(requestId, tcs))
        {
            throw new ObsWebSocketException($"Duplicate request ID: {requestId}");
        }

        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _clientLifetimeCts.Token
        );

        try
        {
            await SendMessageAsync(
                    WebSocketOpCode.Request,
                    new RequestPayload(
                        requestType,
                        requestId,
                        SerializeRequestData(requestType, requestData)
                    ),
                    linkedCts.Token
                )
                .ConfigureAwait(false);

            int effectiveTimeout = timeoutMs ?? _options.Value.RequestTimeoutMs;
            _logger.LogDebug(
                "Waiting for Response {RequestId} ({RequestType}, Timeout: {TimeoutMs}ms)...",
                requestId,
                requestType,
                effectiveTimeout
            );

            object responseObj = await WaitForResponseAsync(
                    tcs,
                    effectiveTimeout,
                    $"Request '{requestType}' ({requestId})",
                    linkedCts.Token
                )
                .ConfigureAwait(false);
            RequestResponsePayload<object> response = CastResponsePayload<
                RequestResponsePayload<object>
            >(responseObj, "RequestResponse");

            ProcessResponseStatus(response.RequestStatus, requestType, requestId);

            TResponse? result = _serializer.DeserializeValuePayload<TResponse>(
                response.ResponseData
            );
            return (!result.HasValue && Nullable.GetUnderlyingType(typeof(TResponse)) == null)
                ? throw new ObsWebSocketException(
                    $"Null deserialization for non-nullable value type '{typeof(TResponse).Name}'."
                )
                : result;
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                ex,
                "CallAsyncValue failed for {RequestType} ({RequestId})",
                requestType,
                requestId
            );
            tcs.TrySetException(ex);
            throw ex is ObsWebSocketException
                ? ex
                : new ObsWebSocketException($"Error in CallAsyncValue for '{requestType}'.", ex);
        }
        catch (OperationCanceledException) when (_clientLifetimeCts.IsCancellationRequested)
        {
            _logger.LogWarning(
                "CallAsyncValue for {RequestType} ({RequestId}) canceled.",
                requestType,
                requestId
            );
            tcs.TrySetCanceled(_clientLifetimeCts.Token);
            throw;
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    /// <inheritdoc/>
    public async Task<List<RequestResponsePayload<object>>> CallBatchAsync(
        IEnumerable<BatchRequestItem> requests,
        RequestBatchExecutionType? executionType = null,
        bool? haltOnFailure = null,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(requests);
        EnsureConnected();
        Debug.Assert(_clientLifetimeCts != null);

        string batchRequestId = Guid.NewGuid().ToString();
        TaskCompletionSource<object> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<RequestPayload> requestPayloads = [];
        int index = 0;
        foreach (BatchRequestItem item in requests)
        {
            if (string.IsNullOrEmpty(item.RequestType))
            {
                throw new ArgumentException(
                    $"Request type required at index {index}.",
                    nameof(requests)
                );
            }

            requestPayloads.Add(
                new RequestPayload(
                    item.RequestType,
                    $"{batchRequestId}_{index++}",
                    SerializeRequestData($"batch item '{item.RequestType}'", item.RequestData)
                )
            );
        }

        if (requestPayloads.Count == 0)
        {
            _logger.LogWarning("Empty batch request.");
            return [];
        }

        if (!_pendingBatchRequests.TryAdd(batchRequestId, tcs))
        {
            throw new ObsWebSocketException($"Duplicate batch ID: {batchRequestId}");
        }

        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _clientLifetimeCts.Token
        );

        try
        {
            await SendMessageAsync(
                    WebSocketOpCode.RequestBatch,
                    new RequestBatchPayload(
                        batchRequestId,
                        haltOnFailure,
                        executionType,
                        requestPayloads
                    ),
                    linkedCts.Token
                )
                .ConfigureAwait(false);

            int baseTimeout = _options.Value.RequestTimeoutMs;
            int effectiveTimeout =
                timeoutMs
                ?? (
                    (baseTimeout * DefaultBatchTimeoutMultiplier)
                    + (baseTimeout * requestPayloads.Count / 2)
                );
            _logger.LogDebug(
                "Waiting for Batch Response {BatchRequestId} ({RequestCount}, Timeout: {TimeoutMs}ms)...",
                batchRequestId,
                requestPayloads.Count,
                effectiveTimeout
            );

            object responseObj = await WaitForResponseAsync(
                    tcs,
                    effectiveTimeout,
                    $"Batch ({batchRequestId})",
                    linkedCts.Token
                )
                .ConfigureAwait(false);
            RequestBatchResponsePayload<object> response = CastResponsePayload<
                RequestBatchResponsePayload<object>
            >(responseObj, "RequestBatchResponse");

            _logger.LogDebug(
                "Received Batch Response {BatchRequestId} ({ResultCount} results).",
                batchRequestId,
                response.Results.Count
            );
            return response.Results;
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                ex,
                "CallBatchAsync failed for batch {BatchRequestId}",
                batchRequestId
            );
            tcs.TrySetException(ex);
            throw ex is ObsWebSocketException
                ? ex
                : new ObsWebSocketException($"Error in CallBatchAsync for '{batchRequestId}'.", ex);
        }
        catch (OperationCanceledException) when (_clientLifetimeCts.IsCancellationRequested)
        {
            _logger.LogWarning("CallBatchAsync for {BatchRequestId} canceled.", batchRequestId);
            tcs.TrySetCanceled(_clientLifetimeCts.Token);
            throw;
        }
        finally
        {
            _pendingBatchRequests.TryRemove(batchRequestId, out _);
        }
    }

    /// <inheritdoc/>
    public async Task DisconnectAsync(
        WebSocketCloseStatus closeStatus = WebSocketCloseStatus.NormalClosure,
        string statusDescription = "Client requested disconnect",
        CancellationToken cancellationToken = default
    )
    {
        Task? loopTaskToWait;
        bool startedDisconnect;

        using (_connectionLock.EnterScope())
        {
            if (_connectionState is ConnectionState.Disconnected or ConnectionState.Disconnecting)
            {
                _logger.LogDebug(
                    "DisconnectAsync ignored, already {ConnectionState}.",
                    _connectionState
                );
                return;
            }

            startedDisconnect = true;
            _logger.LogInformation("DisconnectAsync initiating graceful shutdown...");
            _connectionState = ConnectionState.Disconnecting;
            IsConnected = false;
            _clientLifetimeCts?.Cancel();
            loopTaskToWait = _connectionLoopTask;
        }

        if (startedDisconnect && loopTaskToWait != null)
        {
            _logger.LogDebug("Waiting for connection loop task to complete during disconnect...");
            try
            {
                using CancellationTokenSource combinedWaitCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                combinedWaitCts.CancelAfter(TimeSpan.FromSeconds(5));
                await loopTaskToWait
                    .WaitAsync(combinedWaitCts.Token)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                _logger.LogDebug(
                    "Connection loop task completed or wait timed out/canceled during DisconnectAsync."
                );
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "Wait for connection loop task timed out or canceled during DisconnectAsync."
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception from connection loop task during DisconnectAsync.");
            }
        }

        // Finalize with a null reason because DisconnectAsync initiated it.
        await FinalizeDisconnectionAsync(closeStatus, statusDescription, reasonException: null)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _logger.LogDebug("DisposeAsync called.");
        await DisconnectAsync(
                WebSocketCloseStatus.NormalClosure,
                "Client disposing",
                CancellationToken.None
            )
            .ConfigureAwait(false);
        GC.SuppressFinalize(this);
        _logger.LogDebug("DisposeAsync completed.");
    }

    #endregion

    #region Connection Loop and Internal Logic

    private async Task ConnectionLoopAsync(
        ObsWebSocketClientOptions options,
        CancellationToken externalCancellationToken
    )
    {
        int attempt = 0;
        int currentDelayMs = options.InitialReconnectDelayMs;
        IWebSocketConnection? previousWebSocket = null;
        Debug.Assert(_clientLifetimeCts != null);
        CancellationToken clientLifetimeToken = _clientLifetimeCts.Token;

        // Capture the initial connection TCS *before* the loop starts
        TaskCompletionSource initialTcs = _initialConnectionTcs;

        using CancellationTokenSource linkedLoopCts =
            CancellationTokenSource.CreateLinkedTokenSource(
                externalCancellationToken,
                clientLifetimeToken
            );
        CancellationToken loopToken = linkedLoopCts.Token;

        try
        {
            while (!loopToken.IsCancellationRequested)
            {
                attempt++;
                bool isConnectedThisAttempt = false;
                Exception? attemptException = null;
                IWebSocketConnection? currentWebSocket = null;

                try
                {
                    using (_connectionLock.EnterScope())
                    {
                        if (_connectionState == ConnectionState.Disconnecting)
                        {
                            break;
                        }

                        _connectionState =
                            (attempt == 1)
                                ? ConnectionState.Connecting
                                : ConnectionState.Reconnecting;
                    }

                    if (attempt > 1) // Delay before Retry
                    {
                        int maxAttempts = options.MaxReconnectAttempts;
                        if (
                            !options.AutoReconnectEnabled
                            || (maxAttempts >= 0 && (attempt - 1) >= maxAttempts)
                        )
                        {
                            _logger.LogWarning(
                                "Max reconnect attempts ({MaxAttempts}) reached or auto-reconnect disabled. Stopping.",
                                maxAttempts
                            );
                            _completionException = new ObsWebSocketException(
                                $"Failed to connect after {attempt - 1} attempts.",
                                _completionException
                            );
                            initialTcs.TrySetException(_completionException); // Fail the initial connect TCS
                            break;
                        }

                        _logger.LogInformation(
                            "Reconnecting attempt {AttemptNumber}/{MaxAttempts} after {DelayMs}ms...",
                            attempt,
                            maxAttempts < 0 ? "Infinite" : maxAttempts,
                            currentDelayMs
                        );
                        await Task.Delay(currentDelayMs, loopToken).ConfigureAwait(false);
                        if (options.ReconnectBackoffMultiplier > 1.0)
                        {
                            currentDelayMs = (int)
                                Math.Min(
                                    options.MaxReconnectDelayMs,
                                    currentDelayMs * options.ReconnectBackoffMultiplier
                                );
                        }
                    }

                    previousWebSocket?.Dispose();
                    previousWebSocket = null;
                    currentWebSocket = _connectionFactory.CreateConnection();
                    RaiseConnectingEvent(options.ServerUri!, attempt);

                    await TryConnectAndIdentifyAsync(options, currentWebSocket, attempt, loopToken)
                        .ConfigureAwait(false);

                    isConnectedThisAttempt = true;
                    using (_connectionLock.EnterScope())
                    {
                        if (_connectionState == ConnectionState.Disconnecting)
                        {
                            _logger.LogInformation(
                                "Connected (Attempt {AttemptNumber}), but disconnect requested. Aborting.",
                                attempt
                            );
                            previousWebSocket = currentWebSocket;
                            _webSocket = null;
                            _completionException = new TaskCanceledException(
                                "Disconnect requested during connect."
                            );
                            initialTcs.TrySetCanceled(loopToken); // Signal cancellation
                            break;
                        }

                        _connectionState = ConnectionState.Connected;
                        IsConnected = true;
                        _webSocket = currentWebSocket;
                        previousWebSocket = null; // Prevent disposal

                        // Receive loop is now started within TryConnectAndIdentifyAsync *before* handshake
                        Debug.Assert(
                            _receiveTask != null,
                            "Receive task should have been started by successful TryConnectAndIdentifyAsync."
                        );
                        _logger.LogDebug(
                            "[Attempt:{AttemptNumber}] Handshake complete, receive loop is running.",
                            attempt
                        );
                    }

                    RaiseConnectedEvent();
                    _logger.LogInformation(
                        "Successfully connected and identified (Attempt {AttemptNumber}).",
                        attempt
                    );
                    _completionException = null;
                    initialTcs.TrySetResult(); // Signal successful initial connection
                    attempt = 0; // Reset attempt count only on full success
                    currentDelayMs = options.InitialReconnectDelayMs;

                    Debug.Assert(_receiveTask != null);
                    _logger.LogDebug(
                        "Connection established. Waiting for receive loop completion or cancellation..."
                    );
                    await _receiveTask.WaitAsync(loopToken).ConfigureAwait(false); // Wait for disconnect/shutdown
                    _logger.LogDebug("Receive loop task completed while connected.");
                }
                catch (OperationCanceledException ex) when (loopToken.IsCancellationRequested)
                {
                    _logger.LogInformation(
                        "Connection loop canceled (Attempt {AttemptNumber}).",
                        attempt
                    );
                    attemptException = ex;
                    initialTcs.TrySetCanceled(loopToken); // Signal cancellation
                    break;
                }
                catch (AuthenticationFailureException authEx)
                {
                    _logger.LogError(
                        authEx,
                        "Authentication failed (Attempt {AttemptNumber}). Stopping.",
                        attempt
                    );
                    attemptException = authEx;
                    RaiseAuthenticationFailureEvent(options.ServerUri!, attempt, authEx);
                    initialTcs.TrySetException(authEx); // Signal auth failure
                    break; // Auth failure is fatal
                }
                catch (ConnectionAttemptFailedException connEx)
                {
                    _logger.LogWarning(
                        connEx.InnerException ?? connEx,
                        "Connect attempt {AttemptNumber} failed. Retrying...",
                        attempt
                    );
                    attemptException = connEx;
                    RaiseConnectionFailedEvent(options.ServerUri!, attempt, connEx);
                    // Only fail initial TCS if retries are disabled or exhausted on the *first* attempt
                    if (
                        attempt == 1
                        && (!options.AutoReconnectEnabled || options.MaxReconnectAttempts == 0)
                    )
                    {
                        initialTcs.TrySetException(connEx);
                    }
                }
                catch (WebSocketException wsEx)
                {
                    _logger.LogWarning(
                        wsEx,
                        "WebSocketException during connection/receive (Attempt {AttemptNumber}). Retrying...",
                        attempt
                    );
                    attemptException = wsEx;
                    RaiseConnectionFailedEvent(options.ServerUri!, attempt, wsEx);
                    if (
                        attempt == 1
                        && (!options.AutoReconnectEnabled || options.MaxReconnectAttempts == 0)
                    )
                    {
                        initialTcs.TrySetException(
                            new ConnectionAttemptFailedException(wsEx.Message, wsEx)
                        );
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Unexpected error in connection loop (Attempt {AttemptNumber}). Retrying...",
                        attempt
                    );
                    attemptException = ex;
                    RaiseConnectionFailedEvent(options.ServerUri!, attempt, ex);
                    if (
                        attempt == 1
                        && (!options.AutoReconnectEnabled || options.MaxReconnectAttempts == 0)
                    )
                    {
                        initialTcs.TrySetException(
                            new ConnectionAttemptFailedException(ex.Message, ex)
                        );
                    }
                }
                finally
                {
                    if (attemptException != null)
                    {
                        _completionException = attemptException;
                    }

                    bool cleanupNeeded =
                        !isConnectedThisAttempt
                        || (isConnectedThisAttempt && attemptException != null);
                    if (cleanupNeeded)
                    {
                        using (_connectionLock.EnterScope())
                        {
                            if (_connectionState != ConnectionState.Disconnecting)
                            {
                                IsConnected = false;
                                _receiveCts?.Cancel();
                                _receiveCts?.Dispose();
                                _receiveCts = null;
                                _receiveTask = null;
                                if (ReferenceEquals(_webSocket, currentWebSocket))
                                {
                                    _webSocket = null;
                                }

                                previousWebSocket = currentWebSocket;
                            }
                        }

                        if (isConnectedThisAttempt && attemptException != null)
                        {
                            _logger.LogWarning(
                                "Connection lost during connected state due to: {ErrorType}",
                                attemptException.GetType().Name
                            );
                        }
                    }
                }
            } // End while
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Catastrophic error in ConnectionLoopAsync.");
            _completionException = ex;
            initialTcs.TrySetException(ex); // Ensure TCS is faulted
        }
        finally
        {
            _logger.LogInformation("Exited connection loop. Finalizing state.");
            // If the loop exited without success/explicit failure setting the initial TCS, fail it now.
            initialTcs.TrySetException(
                _completionException
                    ?? new ObsWebSocketException(
                        "Connection loop exited unexpectedly before initial connection completed."
                    )
            );
            previousWebSocket?.Dispose();
            await FinalizeDisconnectionAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Connection loop ended.",
                    _completionException
                )
                .ConfigureAwait(false);
        }
    }

    /// <summary> Attempts a single connection and handshake sequence. Starts the receive loop upon successful connection. </summary>
    private async Task TryConnectAndIdentifyAsync(
        ObsWebSocketClientOptions options,
        IWebSocketConnection ws,
        int attempt,
        CancellationToken ct
    )
    {
        _helloTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _identifiedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        CancellationTokenSource? localReceiveCts = null;
        Task? localReceiveTask = null;

        try
        {
            // Add SubProtocol only if needed
            if (
                string.IsNullOrEmpty(ws.SubProtocol)
                || ws.SubProtocol != _serializer.ProtocolSubProtocol
            )
            {
                try
                {
                    ws.Options.AddSubProtocol(_serializer.ProtocolSubProtocol);
                }
                catch (ArgumentException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Attempted duplicate subprotocol add: {SubProtocol}",
                        _serializer.ProtocolSubProtocol
                    );
                }
            }

            // Connect
            using CancellationTokenSource connectTimeoutCts = new(options.HandshakeTimeoutMs);
            using CancellationTokenSource linkedConnectCts =
                CancellationTokenSource.CreateLinkedTokenSource(ct, connectTimeoutCts.Token);
            _logger.LogDebug("[Attempt:{AttemptNumber}] Connecting...", attempt);
            try
            {
                await ws.ConnectAsync(options.ServerUri!, linkedConnectCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (connectTimeoutCts.IsCancellationRequested)
            {
                throw new ConnectionAttemptFailedException(
                    $"ConnectAsync timed out (Attempt {attempt})."
                );
            }
            catch (Exception ex)
            {
                throw new ConnectionAttemptFailedException(
                    $"ConnectAsync failed (Attempt {attempt}): {ex.Message}",
                    ex
                );
            }

            _logger.LogInformation(
                "[Attempt:{AttemptNumber}] WebSocket connection established. Protocol: {SubProtocol}",
                attempt,
                ws.SubProtocol ?? "(None)"
            );

            // --- Start Receive Loop *before* Handshake ---
            localReceiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _webSocket = ws; // Assign the current socket temporarily for ReceiveLoopAsync
            localReceiveTask = Task.Run(
                () => ReceiveLoopAsync(localReceiveCts.Token),
                localReceiveCts.Token
            ); // TCS are now initialized before this runs
            _logger.LogDebug(
                "[Attempt:{AttemptNumber}] Receive loop started for handshake.",
                attempt
            );

            // --- Handshake ---
            _logger.LogDebug("[Attempt:{AttemptNumber}] Waiting for Hello...", attempt);
            object helloMsgObj = await WaitForHandshakeMessageAsync(
                    _helloTcs,
                    options.HandshakeTimeoutMs,
                    "Hello",
                    ct
                )
                .ConfigureAwait(false);
            HelloPayload helloPayload = ExtractPayloadFromHandshake<HelloPayload>(
                helloMsgObj,
                "Hello"
            );
            _logger.LogDebug(
                "[Attempt:{AttemptNumber}] Received Hello. RPC Version: {RpcVersion}",
                attempt,
                helloPayload.RpcVersion
            );
            string? authResponse = null;
            if (helloPayload.Authentication != null)
            {
                _logger.LogDebug("[Attempt:{AttemptNumber}] Authentication required.", attempt);
                if (string.IsNullOrEmpty(options.Password))
                {
                    throw new AuthenticationFailureException(
                        "Authentication required by server, but no password was provided."
                    );
                }

                authResponse = AuthenticationHelper.GenerateAuthenticationString(
                    helloPayload.Authentication.Salt,
                    helloPayload.Authentication.Challenge,
                    options.Password
                );
            }

            uint requestedEventSubs = options.EventSubscriptions ?? (uint)EventSubscription.All; // Get flags being requested or default to All

            await SendMessageAsync(
                    WebSocketOpCode.Identify,
                    new IdentifyPayload(helloPayload.RpcVersion, authResponse, requestedEventSubs),
                    ct
                )
                .ConfigureAwait(false);
            _logger.LogDebug("[Attempt:{AttemptNumber}] Waiting for Identified...", attempt);
            object identifiedMsgObj = await WaitForHandshakeMessageAsync(
                    _identifiedTcs,
                    options.HandshakeTimeoutMs,
                    "Identified",
                    ct
                )
                .ConfigureAwait(false);

            IdentifiedPayload identifiedPayload = ExtractPayloadFromHandshake<IdentifiedPayload>(
                identifiedMsgObj,
                "Identified"
            );

            _logger.LogDebug(
                "[Attempt:{AttemptNumber}] Received Identified. Negotiated RPC Version: {NegotiatedRpcVersion}",
                attempt,
                identifiedPayload.NegotiatedRpcVersion
            );

            NegotiatedRpcVersion = identifiedPayload.NegotiatedRpcVersion;
            CurrentEventSubscriptions = requestedEventSubs; // Store the flags that were just

            // Success! Transfer ownership of CTS/Task to class members. ConnectionLoopAsync sets final state.
            _receiveCts = localReceiveCts;
            _receiveTask = localReceiveTask;
            localReceiveCts = null; // Prevent disposal in finally block
            localReceiveTask = null;
        }
        catch (Exception ex)
        {
            NegotiatedRpcVersion = null;
            CurrentEventSubscriptions = null;

            _webSocket = null; // Detach socket on failure

            // Ensure TCS are cleaned up on failure
            _helloTcs?.TrySetException(ex);
            _identifiedTcs?.TrySetException(ex);
            _helloTcs = null; // Clear fields after attempting to set exception
            _identifiedTcs = null;

            // Clean up this attempt's specific receive loop resources
            try
            {
                localReceiveCts?.Cancel();
            }
            catch
            { /* Ignore */
            }

            try
            {
                localReceiveCts?.Dispose();
            }
            catch
            { /* Ignore */
            }

            // Rethrow specific exceptions
            if (
                ex
                is AuthenticationFailureException
                    or ConnectionAttemptFailedException
                    or OperationCanceledException
            )
            {
                throw;
            }

            if (
                ex is ObsWebSocketException obsEx
                && obsEx.Message.Contains("Authentication", StringComparison.OrdinalIgnoreCase)
            )
            {
                throw new AuthenticationFailureException(ex.Message, ex);
            }

            throw new ConnectionAttemptFailedException(
                $"Connection attempt {attempt} failed during handshake: {ex.Message}",
                ex
            );
        }
    }

    /// <summary> Receives messages from the WebSocket. </summary>
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        using MemoryStream bufferStream = new();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        IWebSocketConnection? currentWebSocket = _webSocket;

        try
        {
            if (currentWebSocket is null || currentWebSocket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException(
                    $"Receive loop started with WebSocket not open or null. State: {currentWebSocket?.State}"
                );
            }

            _logger.LogDebug(
                "Receive loop starting for WebSocket {HashCode}.",
                RuntimeHelpers.GetHashCode(currentWebSocket)
            );

            while (!cancellationToken.IsCancellationRequested)
            {
                if (currentWebSocket.State != WebSocketState.Open)
                {
                    _logger.LogWarning(
                        "WebSocket state changed to {WebSocketState} during receive loop.",
                        currentWebSocket.State
                    );
                    throw new WebSocketException(
                        WebSocketError.InvalidState,
                        $"WebSocket state became {currentWebSocket.State} during receive loop."
                    );
                }

                ValueWebSocketReceiveResult result;
                bufferStream.SetLength(0);
                do
                {
                    result = await currentWebSocket
                        .ReceiveAsync(buffer.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        HandleServerClose();
                        return;
                    }

                    await bufferStream
                        .WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken)
                        .ConfigureAwait(false);
                } while (!result.EndOfMessage);

                if (bufferStream.Length == 0)
                {
                    _logger.LogTrace("Received empty message.");
                    continue;
                }

                bufferStream.Position = 0;
                object? incomingMsgObj = await _serializer
                    .DeserializeAsync(bufferStream, cancellationToken)
                    .ConfigureAwait(false);
                if (incomingMsgObj is null)
                {
                    _logger.LogWarning(
                        "Deserialization returned null (Length: {BufferLength}).",
                        bufferStream.Length
                    );
                    continue;
                }

                ProcessIncomingMessage(incomingMsgObj);
            }

            _logger.LogInformation("Receive loop exiting: cancellation requested.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Receive loop cancelled gracefully via token.");
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(
                ex,
                "WebSocketException in receive loop (Code: {WebSocketErrorCode}).",
                ex.WebSocketErrorCode
            );
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected exception in receive loop.");
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _logger.LogDebug(
                "Receive loop finished for WebSocket {HashCode}.",
                RuntimeHelpers.GetHashCode(currentWebSocket)
            );
        }
    }

    /// <summary> Handles server-initiated close frame. </summary>
    private void HandleServerClose()
    {
        IWebSocketConnection? ws = _webSocket;
        bool expectedClosure = _clientLifetimeCts?.IsCancellationRequested ?? false;
        string? desc = ws?.CloseStatusDescription;
        WebSocketCloseStatus? status = ws?.CloseStatus;

        if (expectedClosure)
        {
            _logger.LogInformation(
                "Server acknowledged client closure. Status: {WebSocketCloseStatus}, Desc: {Description}",
                status,
                desc
            );
        }
        else
        {
            _logger.LogWarning(
                "Server initiated unexpected close. Status: {WebSocketCloseStatus}, Desc: {Description}",
                status,
                desc
            );
        }

        ObsWebSocketException closeEx = new(
            $"Connection closed by server. Status: {status}, Description: {desc}"
        );
        CleanupConnectionOnly(closeEx);

        if (ws?.State == WebSocketState.CloseReceived)
        {
            _logger.LogDebug("Acknowledging server close frame...");
            _ = Task.Run(async () =>
            {
                try
                {
                    using CancellationTokenSource ackCts = new(TimeSpan.FromSeconds(2));
                    await ws.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Acknowledging close",
                        ackCts.Token
                    );
                    _logger.LogDebug("Server close frame acknowledged.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to acknowledge server close frame.");
                }
            });
        }
    }

    /// <summary> Cleans up current connection resources and fails pending tasks. </summary>
    private void CleanupConnectionOnly(Exception reasonException)
    {
        _logger.LogTrace(
            "CleanupConnectionOnly due to: {ExceptionType} - {ExceptionMessage}",
            reasonException.GetType().Name,
            reasonException.Message
        );
        IsConnected = false;

        NegotiatedRpcVersion = null;
        CurrentEventSubscriptions = null;

        CancellationTokenSource? receiveLoopCts = Interlocked.Exchange(ref _receiveCts, null);
        CancellationToken tokenForFailure =
            receiveLoopCts?.Token ?? _clientLifetimeCts?.Token ?? CancellationToken.None;

        _helloTcs?.TrySetException(reasonException);
        _helloTcs = null;
        _identifiedTcs?.TrySetException(reasonException);
        _identifiedTcs = null;

        FailPendingRequests(_pendingRequests, reasonException, tokenForFailure);
        FailPendingRequests(_pendingBatchRequests, reasonException, tokenForFailure);

        try
        {
            receiveLoopCts?.Cancel();
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception cancelling receive CTS during cleanup.");
        }

        try
        {
            receiveLoopCts?.Dispose();
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception disposing receive CTS during cleanup.");
        }

        _receiveTask = null;

        IWebSocketConnection? socket = Interlocked.Exchange(ref _webSocket, null);
        if (socket != null)
        {
            _logger.LogDebug(
                "Disposing WebSocket instance {HashCode}",
                RuntimeHelpers.GetHashCode(socket)
            );
            try
            {
                socket.Abort();
            }
            catch { }

            try
            {
                socket.Dispose();
            }
            catch { }
        }
    }

    /// <summary> Performs final cleanup, sets Disconnected state, and raises event. </summary>
    private Task FinalizeDisconnectionAsync(
        WebSocketCloseStatus _,
        string statusDescription,
        Exception? reasonException
    )
    {
        bool needsEvent;
        bool wasDisconnecting;
        using (_connectionLock.EnterScope())
        {
            if (_connectionState == ConnectionState.Disconnected)
            {
                return Task.CompletedTask;
            }

            wasDisconnecting = _connectionState == ConnectionState.Disconnecting; // Check *before* changing state
            _logger.LogDebug(
                "Finalizing disconnection... Reason: {ReasonType}",
                reasonException?.GetType().Name ?? "Graceful"
            );
            _connectionState = ConnectionState.Disconnected;
            needsEvent = true;
        }

        // If DisconnectAsync initiated (wasDisconnecting is true), the reason for the event should be null.
        Exception? eventReason = wasDisconnecting ? null : reasonException;
        CleanupConnectionOnly(reasonException ?? new ObsWebSocketException(statusDescription)); // Cleanup uses the actual reason

        CancellationTokenSource? lifetimeCts = _clientLifetimeCts;
        if (lifetimeCts != null)
        {
            _clientLifetimeCts = null;
            try
            {
                if (!lifetimeCts.IsCancellationRequested)
                {
                    lifetimeCts.Cancel();
                }
            }
            catch { }

            try
            {
                lifetimeCts.Dispose();
            }
            catch { }
        }

        _connectionLoopTask = null;

        if (needsEvent)
        {
            RaiseDisconnectedEvent(eventReason); // Use potentially overridden reason
        }

        _logger.LogInformation(
            "Client definitively disconnected. Reason: {DisconnectionReason}",
            eventReason?.Message ?? statusDescription
        );
        return Task.CompletedTask;
    }

    #endregion

    #region Message Processing
    private static readonly FrozenDictionary<
        string,
        Action<ObsWebSocketClient, object?>
    > s_eventHandlers = InitializeEventHandlers().ToFrozenDictionary();

    private void ProcessIncomingMessage(object messageObject)
    {
        WebSocketOpCode opCode;
        object? payloadData;
        switch (messageObject)
        {
            case IncomingMessage<JsonElement> jsonMsg:
                (opCode, payloadData) = (jsonMsg.Op, jsonMsg.D);
                break;
            case IncomingMessage<object> msgpackMsg:
                (opCode, payloadData) = (msgpackMsg.Op, msgpackMsg.D);
                break;
            default:
                _logger.LogError(
                    "Unexpected incoming message type encountered: {MessageType}",
                    messageObject.GetType().FullName
                );
                return;
        }

        switch (opCode)
        {
            case WebSocketOpCode.Hello:
                _logger.LogTrace("Processing Hello message.");
                _helloTcs?.TrySetResult(messageObject);
                break;
            case WebSocketOpCode.Identified:
                _logger.LogTrace("Processing Identified message.");
                _identifiedTcs?.TrySetResult(messageObject);
                break;
            case WebSocketOpCode.Event:
                HandleEventMessage(payloadData);
                break;
            case WebSocketOpCode.RequestResponse:
                HandleRequestResponseMessage(payloadData);
                break;
            case WebSocketOpCode.RequestBatchResponse:
                HandleRequestBatchResponseMessage(payloadData);
                break;
            default:
                _logger.LogWarning("Received message with unhandled OpCode: {OpCode}", opCode);
                break;
        }
    }

    private void HandleRequestResponseMessage(object? payloadData)
    {
        if (payloadData == null)
        {
            _logger.LogWarning("Received null payload for RequestResponse.");
            return;
        }

        try
        {
            RequestResponsePayload<object>? response = _serializer.DeserializePayload<
                RequestResponsePayload<object>
            >(payloadData);
            if (response is null)
            {
                LogEventDataDeserializationError("RequestResponse wrapper", payloadData);
                return;
            }

            _logger.LogTrace(
                "Processing RequestResponse for RequestId: {RequestId}, Status: {StatusResult}",
                response.RequestId,
                response.RequestStatus.Result
            );
            if (
                _pendingRequests.TryRemove(
                    response.RequestId,
                    out TaskCompletionSource<object>? tcs
                )
            )
            {
                tcs.TrySetResult(response);
            }
            else
            {
                _logger.LogWarning(
                    "Received response for unknown or timed out RequestId: {RequestId}",
                    response.RequestId
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Exception during processing of RequestResponse payload: {Payload}",
                payloadData
            );
        }
    }

    private void HandleRequestBatchResponseMessage(object? payloadData)
    {
        if (payloadData == null)
        {
            _logger.LogWarning("Received null payload for RequestBatchResponse.");
            return;
        }

        try
        {
            RequestBatchResponsePayload<object>? response = _serializer.DeserializePayload<
                RequestBatchResponsePayload<object>
            >(payloadData);
            if (response is null)
            {
                LogEventDataDeserializationError("RequestBatchResponse wrapper", payloadData);
                return;
            }

            _logger.LogTrace(
                "Processing RequestBatchResponse for RequestId: {RequestId} ({ResultCount} results)",
                response.RequestId,
                response.Results?.Count ?? 0
            );
            if (
                _pendingBatchRequests.TryRemove(
                    response.RequestId,
                    out TaskCompletionSource<object>? tcs
                )
            )
            {
                tcs.TrySetResult(response);
            }
            else
            {
                _logger.LogWarning(
                    "Received response for unknown or timed out BatchRequestId: {RequestId}",
                    response.RequestId
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Exception during processing of RequestBatchResponse payload: {Payload}",
                payloadData
            );
        }
    }

    private void HandleEventMessage(object? payloadData)
    {
        if (payloadData == null)
        {
            _logger.LogWarning("Received null payload for Event.");
            return;
        }

        EventPayloadBase<object>? eventPayloadBase = null;
        try
        {
            eventPayloadBase = _serializer.DeserializePayload<EventPayloadBase<object>>(
                payloadData
            );
            if (eventPayloadBase is null)
            {
                LogEventDataDeserializationError("base event structure", payloadData);
                return;
            }

            _logger.LogTrace("Handling incoming event: {EventType}", eventPayloadBase.EventType);
            if (
                s_eventHandlers.TryGetValue(
                    eventPayloadBase.EventType,
                    out Action<ObsWebSocketClient, object?>? handlerAction
                )
            )
            {
                try
                {
                    handlerAction(this, eventPayloadBase.EventData);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Exception occurred within the event handler for {EventType}.",
                        eventPayloadBase.EventType
                    );
                }
            }
            else
            {
                _logger.LogWarning(
                    "Received event with unhandled type: {EventType}",
                    eventPayloadBase.EventType
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Critical exception during event handling for '{EventType}': {Payload}",
                eventPayloadBase?.EventType ?? "Unknown Type",
                payloadData
            );
        }
    }

    private void TryHandleEvent<TPayload, TEventArgs>(
        string eventType,
        object? rawData,
        Func<TPayload, TEventArgs> argsFactory,
        Action<TEventArgs> invoker
    )
        where TPayload : class
        where TEventArgs : EventArgs
    {
        try
        {
            TPayload? payload = _serializer.DeserializePayload<TPayload>(rawData);
            if (payload is not null)
            {
                invoker(argsFactory(payload));
            }
            else
            {
                LogEventDataDeserializationError($"{eventType} payload", rawData);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while trying to handle event {EventType}.", eventType);
        }
    }

    private static Dictionary<
        string,
        Action<ObsWebSocketClient, object?>
    > InitializeEventHandlers() =>
        new(StringComparer.Ordinal)
        {
            // Config
            ["CurrentSceneCollectionChanging"] = (c, d) =>
                c.TryHandleEvent<
                    CurrentSceneCollectionChangingPayload,
                    CurrentSceneCollectionChangingEventArgs
                >(
                    "CurrentSceneCollectionChanging",
                    d,
                    p => new(p),
                    c.OnCurrentSceneCollectionChanging
                ),
            ["CurrentSceneCollectionChanged"] = (c, d) =>
                c.TryHandleEvent<
                    CurrentSceneCollectionChangedPayload,
                    CurrentSceneCollectionChangedEventArgs
                >(
                    "CurrentSceneCollectionChanged",
                    d,
                    p => new(p),
                    c.OnCurrentSceneCollectionChanged
                ),
            ["SceneCollectionListChanged"] = (c, d) =>
                c.TryHandleEvent<
                    SceneCollectionListChangedPayload,
                    SceneCollectionListChangedEventArgs
                >("SceneCollectionListChanged", d, p => new(p), c.OnSceneCollectionListChanged),
            ["CurrentProfileChanging"] = (c, d) =>
                c.TryHandleEvent<CurrentProfileChangingPayload, CurrentProfileChangingEventArgs>(
                    "CurrentProfileChanging",
                    d,
                    p => new(p),
                    c.OnCurrentProfileChanging
                ),
            ["CurrentProfileChanged"] = (c, d) =>
                c.TryHandleEvent<CurrentProfileChangedPayload, CurrentProfileChangedEventArgs>(
                    "CurrentProfileChanged",
                    d,
                    p => new(p),
                    c.OnCurrentProfileChanged
                ),
            ["ProfileListChanged"] = (c, d) =>
                c.TryHandleEvent<ProfileListChangedPayload, ProfileListChangedEventArgs>(
                    "ProfileListChanged",
                    d,
                    p => new(p),
                    c.OnProfileListChanged
                ),
            // Filters
            ["SourceFilterListReindexed"] = (c, d) =>
                c.TryHandleEvent<
                    SourceFilterListReindexedPayload,
                    SourceFilterListReindexedEventArgs
                >("SourceFilterListReindexed", d, p => new(p), c.OnSourceFilterListReindexed),
            ["SourceFilterCreated"] = (c, d) =>
                c.TryHandleEvent<SourceFilterCreatedPayload, SourceFilterCreatedEventArgs>(
                    "SourceFilterCreated",
                    d,
                    p => new(p),
                    c.OnSourceFilterCreated
                ),
            ["SourceFilterRemoved"] = (c, d) =>
                c.TryHandleEvent<SourceFilterRemovedPayload, SourceFilterRemovedEventArgs>(
                    "SourceFilterRemoved",
                    d,
                    p => new(p),
                    c.OnSourceFilterRemoved
                ),
            ["SourceFilterNameChanged"] = (c, d) =>
                c.TryHandleEvent<SourceFilterNameChangedPayload, SourceFilterNameChangedEventArgs>(
                    "SourceFilterNameChanged",
                    d,
                    p => new(p),
                    c.OnSourceFilterNameChanged
                ),
            ["SourceFilterSettingsChanged"] = (c, d) =>
                c.TryHandleEvent<
                    SourceFilterSettingsChangedPayload,
                    SourceFilterSettingsChangedEventArgs
                >("SourceFilterSettingsChanged", d, p => new(p), c.OnSourceFilterSettingsChanged),
            ["SourceFilterEnableStateChanged"] = (c, d) =>
                c.TryHandleEvent<
                    SourceFilterEnableStateChangedPayload,
                    SourceFilterEnableStateChangedEventArgs
                >(
                    "SourceFilterEnableStateChanged",
                    d,
                    p => new(p),
                    c.OnSourceFilterEnableStateChanged
                ),
            // General
            ["ExitStarted"] = (c, d) => c.OnExitStarted(new ExitStartedEventArgs()),
            ["VendorEvent"] = (c, d) =>
                c.TryHandleEvent<VendorEventPayload, VendorEventEventArgs>(
                    "VendorEvent",
                    d,
                    p => new(p),
                    c.OnVendorEvent
                ),
            ["CustomEvent"] = (c, d) =>
                c.TryHandleEvent<CustomEventPayload, CustomEventEventArgs>(
                    "CustomEvent",
                    d,
                    p => new(p),
                    c.OnCustomEvent
                ),
            // Inputs
            ["InputCreated"] = (c, d) =>
                c.TryHandleEvent<InputCreatedPayload, InputCreatedEventArgs>(
                    "InputCreated",
                    d,
                    p => new(p),
                    c.OnInputCreated
                ),
            ["InputRemoved"] = (c, d) =>
                c.TryHandleEvent<InputRemovedPayload, InputRemovedEventArgs>(
                    "InputRemoved",
                    d,
                    p => new(p),
                    c.OnInputRemoved
                ),
            ["InputNameChanged"] = (c, d) =>
                c.TryHandleEvent<InputNameChangedPayload, InputNameChangedEventArgs>(
                    "InputNameChanged",
                    d,
                    p => new(p),
                    c.OnInputNameChanged
                ),
            ["InputSettingsChanged"] = (c, d) =>
                c.TryHandleEvent<InputSettingsChangedPayload, InputSettingsChangedEventArgs>(
                    "InputSettingsChanged",
                    d,
                    p => new(p),
                    c.OnInputSettingsChanged
                ),
            ["InputActiveStateChanged"] = (c, d) =>
                c.TryHandleEvent<InputActiveStateChangedPayload, InputActiveStateChangedEventArgs>(
                    "InputActiveStateChanged",
                    d,
                    p => new(p),
                    c.OnInputActiveStateChanged
                ),
            ["InputShowStateChanged"] = (c, d) =>
                c.TryHandleEvent<InputShowStateChangedPayload, InputShowStateChangedEventArgs>(
                    "InputShowStateChanged",
                    d,
                    p => new(p),
                    c.OnInputShowStateChanged
                ),
            ["InputMuteStateChanged"] = (c, d) =>
                c.TryHandleEvent<InputMuteStateChangedPayload, InputMuteStateChangedEventArgs>(
                    "InputMuteStateChanged",
                    d,
                    p => new(p),
                    c.OnInputMuteStateChanged
                ),
            ["InputVolumeChanged"] = (c, d) =>
                c.TryHandleEvent<InputVolumeChangedPayload, InputVolumeChangedEventArgs>(
                    "InputVolumeChanged",
                    d,
                    p => new(p),
                    c.OnInputVolumeChanged
                ),
            ["InputAudioBalanceChanged"] = (c, d) =>
                c.TryHandleEvent<
                    InputAudioBalanceChangedPayload,
                    InputAudioBalanceChangedEventArgs
                >("InputAudioBalanceChanged", d, p => new(p), c.OnInputAudioBalanceChanged),
            ["InputAudioSyncOffsetChanged"] = (c, d) =>
                c.TryHandleEvent<
                    InputAudioSyncOffsetChangedPayload,
                    InputAudioSyncOffsetChangedEventArgs
                >("InputAudioSyncOffsetChanged", d, p => new(p), c.OnInputAudioSyncOffsetChanged),
            ["InputAudioTracksChanged"] = (c, d) =>
                c.TryHandleEvent<InputAudioTracksChangedPayload, InputAudioTracksChangedEventArgs>(
                    "InputAudioTracksChanged",
                    d,
                    p => new(p),
                    c.OnInputAudioTracksChanged
                ),
            ["InputAudioMonitorTypeChanged"] = (c, d) =>
                c.TryHandleEvent<
                    InputAudioMonitorTypeChangedPayload,
                    InputAudioMonitorTypeChangedEventArgs
                >("InputAudioMonitorTypeChanged", d, p => new(p), c.OnInputAudioMonitorTypeChanged),
            ["InputVolumeMeters"] = (c, d) =>
                c.TryHandleEvent<InputVolumeMetersPayload, InputVolumeMetersEventArgs>(
                    "InputVolumeMeters",
                    d,
                    p => new(p),
                    c.OnInputVolumeMeters
                ),
            // Media Inputs
            ["MediaInputPlaybackStarted"] = (c, d) =>
                c.TryHandleEvent<
                    MediaInputPlaybackStartedPayload,
                    MediaInputPlaybackStartedEventArgs
                >("MediaInputPlaybackStarted", d, p => new(p), c.OnMediaInputPlaybackStarted),
            ["MediaInputPlaybackEnded"] = (c, d) =>
                c.TryHandleEvent<MediaInputPlaybackEndedPayload, MediaInputPlaybackEndedEventArgs>(
                    "MediaInputPlaybackEnded",
                    d,
                    p => new(p),
                    c.OnMediaInputPlaybackEnded
                ),
            ["MediaInputActionTriggered"] = (c, d) =>
                c.TryHandleEvent<
                    MediaInputActionTriggeredPayload,
                    MediaInputActionTriggeredEventArgs
                >("MediaInputActionTriggered", d, p => new(p), c.OnMediaInputActionTriggered),
            // Outputs
            ["StreamStateChanged"] = (c, d) =>
                c.TryHandleEvent<StreamStateChangedPayload, StreamStateChangedEventArgs>(
                    "StreamStateChanged",
                    d,
                    p => new(p),
                    c.OnStreamStateChanged
                ),
            ["RecordStateChanged"] = (c, d) =>
                c.TryHandleEvent<RecordStateChangedPayload, RecordStateChangedEventArgs>(
                    "RecordStateChanged",
                    d,
                    p => new(p),
                    c.OnRecordStateChanged
                ),
            ["RecordFileChanged"] = (c, d) =>
                c.TryHandleEvent<RecordFileChangedPayload, RecordFileChangedEventArgs>(
                    "RecordFileChanged",
                    d,
                    p => new(p),
                    c.OnRecordFileChanged
                ),
            ["ReplayBufferStateChanged"] = (c, d) =>
                c.TryHandleEvent<
                    ReplayBufferStateChangedPayload,
                    ReplayBufferStateChangedEventArgs
                >("ReplayBufferStateChanged", d, p => new(p), c.OnReplayBufferStateChanged),
            ["VirtualcamStateChanged"] = (c, d) =>
                c.TryHandleEvent<VirtualcamStateChangedPayload, VirtualcamStateChangedEventArgs>(
                    "VirtualcamStateChanged",
                    d,
                    p => new(p),
                    c.OnVirtualcamStateChanged
                ),
            ["ReplayBufferSaved"] = (c, d) =>
                c.TryHandleEvent<ReplayBufferSavedPayload, ReplayBufferSavedEventArgs>(
                    "ReplayBufferSaved",
                    d,
                    p => new(p),
                    c.OnReplayBufferSaved
                ),
            // Scene Items
            ["SceneItemCreated"] = (c, d) =>
                c.TryHandleEvent<SceneItemCreatedPayload, SceneItemCreatedEventArgs>(
                    "SceneItemCreated",
                    d,
                    p => new(p),
                    c.OnSceneItemCreated
                ),
            ["SceneItemRemoved"] = (c, d) =>
                c.TryHandleEvent<SceneItemRemovedPayload, SceneItemRemovedEventArgs>(
                    "SceneItemRemoved",
                    d,
                    p => new(p),
                    c.OnSceneItemRemoved
                ),
            ["SceneItemListReindexed"] = (c, d) =>
                c.TryHandleEvent<SceneItemListReindexedPayload, SceneItemListReindexedEventArgs>(
                    "SceneItemListReindexed",
                    d,
                    p => new(p),
                    c.OnSceneItemListReindexed
                ),
            ["SceneItemEnableStateChanged"] = (c, d) =>
                c.TryHandleEvent<
                    SceneItemEnableStateChangedPayload,
                    SceneItemEnableStateChangedEventArgs
                >("SceneItemEnableStateChanged", d, p => new(p), c.OnSceneItemEnableStateChanged),
            ["SceneItemLockStateChanged"] = (c, d) =>
                c.TryHandleEvent<
                    SceneItemLockStateChangedPayload,
                    SceneItemLockStateChangedEventArgs
                >("SceneItemLockStateChanged", d, p => new(p), c.OnSceneItemLockStateChanged),
            ["SceneItemSelected"] = (c, d) =>
                c.TryHandleEvent<SceneItemSelectedPayload, SceneItemSelectedEventArgs>(
                    "SceneItemSelected",
                    d,
                    p => new(p),
                    c.OnSceneItemSelected
                ),
            ["SceneItemTransformChanged"] = (c, d) =>
                c.TryHandleEvent<
                    SceneItemTransformChangedPayload,
                    SceneItemTransformChangedEventArgs
                >("SceneItemTransformChanged", d, p => new(p), c.OnSceneItemTransformChanged),
            // Scenes
            ["SceneCreated"] = (c, d) =>
                c.TryHandleEvent<SceneCreatedPayload, SceneCreatedEventArgs>(
                    "SceneCreated",
                    d,
                    p => new(p),
                    c.OnSceneCreated
                ),
            ["SceneRemoved"] = (c, d) =>
                c.TryHandleEvent<SceneRemovedPayload, SceneRemovedEventArgs>(
                    "SceneRemoved",
                    d,
                    p => new(p),
                    c.OnSceneRemoved
                ),
            ["SceneNameChanged"] = (c, d) =>
                c.TryHandleEvent<SceneNameChangedPayload, SceneNameChangedEventArgs>(
                    "SceneNameChanged",
                    d,
                    p => new(p),
                    c.OnSceneNameChanged
                ),
            ["CurrentProgramSceneChanged"] = (c, d) =>
                c.TryHandleEvent<
                    CurrentProgramSceneChangedPayload,
                    CurrentProgramSceneChangedEventArgs
                >("CurrentProgramSceneChanged", d, p => new(p), c.OnCurrentProgramSceneChanged),
            ["CurrentPreviewSceneChanged"] = (c, d) =>
                c.TryHandleEvent<
                    CurrentPreviewSceneChangedPayload,
                    CurrentPreviewSceneChangedEventArgs
                >("CurrentPreviewSceneChanged", d, p => new(p), c.OnCurrentPreviewSceneChanged),
            ["SceneListChanged"] = (c, d) =>
                c.TryHandleEvent<SceneListChangedPayload, SceneListChangedEventArgs>(
                    "SceneListChanged",
                    d,
                    p => new(p),
                    c.OnSceneListChanged
                ),
            // Transitions
            ["CurrentSceneTransitionChanged"] = (c, d) =>
                c.TryHandleEvent<
                    CurrentSceneTransitionChangedPayload,
                    CurrentSceneTransitionChangedEventArgs
                >(
                    "CurrentSceneTransitionChanged",
                    d,
                    p => new(p),
                    c.OnCurrentSceneTransitionChanged
                ),
            ["CurrentSceneTransitionDurationChanged"] = (c, d) =>
                c.TryHandleEvent<
                    CurrentSceneTransitionDurationChangedPayload,
                    CurrentSceneTransitionDurationChangedEventArgs
                >(
                    "CurrentSceneTransitionDurationChanged",
                    d,
                    p => new(p),
                    c.OnCurrentSceneTransitionDurationChanged
                ),
            ["SceneTransitionStarted"] = (c, d) =>
                c.TryHandleEvent<SceneTransitionStartedPayload, SceneTransitionStartedEventArgs>(
                    "SceneTransitionStarted",
                    d,
                    p => new(p),
                    c.OnSceneTransitionStarted
                ),
            ["SceneTransitionEnded"] = (c, d) =>
                c.TryHandleEvent<SceneTransitionEndedPayload, SceneTransitionEndedEventArgs>(
                    "SceneTransitionEnded",
                    d,
                    p => new(p),
                    c.OnSceneTransitionEnded
                ),
            ["SceneTransitionVideoEnded"] = (c, d) =>
                c.TryHandleEvent<
                    SceneTransitionVideoEndedPayload,
                    SceneTransitionVideoEndedEventArgs
                >("SceneTransitionVideoEnded", d, p => new(p), c.OnSceneTransitionVideoEnded),
            // UI
            ["StudioModeStateChanged"] = (c, d) =>
                c.TryHandleEvent<StudioModeStateChangedPayload, StudioModeStateChangedEventArgs>(
                    "StudioModeStateChanged",
                    d,
                    p => new(p),
                    c.OnStudioModeStateChanged
                ),
            ["ScreenshotSaved"] = (c, d) =>
                c.TryHandleEvent<ScreenshotSavedPayload, ScreenshotSavedEventArgs>(
                    "ScreenshotSaved",
                    d,
                    p => new(p),
                    c.OnScreenshotSaved
                ),
        };
    #endregion

    #region Helper Methods (Static & Instance)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureConnected()
    {
        if (
            _connectionState != ConnectionState.Connected
            || _webSocket?.State != WebSocketState.Open
        )
        {
            throw new InvalidOperationException(
                $"Client is not connected (State: {_connectionState}, Socket: {_webSocket?.State})."
            );
        }
    }

    private TPayload ExtractPayloadFromHandshake<TPayload>(object messageObject, string messageName)
        where TPayload : class
    {
        object? rawPayload = messageObject switch
        {
            IncomingMessage<JsonElement> jsonMsg => jsonMsg.D,
            IncomingMessage<object> msgpackMsg => msgpackMsg.D,
            _ => throw new ObsWebSocketException(
                $"Unexpected message type during {messageName} handshake: {messageObject.GetType().Name}"
            ),
        };
        TPayload? specificPayload = _serializer.DeserializePayload<TPayload>(rawPayload);
        return specificPayload
            ?? throw new ObsWebSocketException($"Received null or invalid {messageName} payload.");
    }

    private static JsonElement? SerializeRequestData(string _, object? requestData)
    {
        if (requestData is null)
        {
            return null;
        }

        try
        {
            return requestData is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(requestData, s_payloadJsonOptions);
        }
        catch (Exception ex)
        {
            throw new ObsWebSocketException($"Failed to serialize request data.", ex);
        }
    }

    private static TPayload CastResponsePayload<TPayload>(
        object responseObj,
        string responseDescription
    )
        where TPayload : class =>
        responseObj is TPayload typedPayload
            ? typedPayload
            : throw new ObsWebSocketException(
                $"Internal error: Unexpected payload type for {responseDescription}. Expected {typeof(TPayload).Name}, Got {responseObj.GetType().Name}"
            );

    private static void ProcessResponseStatus(
        Protocol.RequestStatus status,
        string requestType,
        string requestId
    )
    {
        if (!status.Result)
        {
            throw new ObsWebSocketException(
                $"OBS request '{requestType}' ({requestId}) failed with code {status.Code}: {status.Comment ?? "No comment"}"
            );
        }
    }

    private void LogEventDataDeserializationError(
        string description,
        object? data,
        Exception? ex = null
    )
    {
        string rawDataStr = data is JsonElement je
            ? (
                je.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    ? "[Null/Undefined]"
                    : je.GetRawText()
            )
            : (data?.GetType().Name ?? "[Null]");
        const string logMessage =
            "Failed to deserialize {Description}. Type: {DataType}, Raw: {RawData}";
        if (ex != null)
        {
            _logger.LogError(
                ex,
                logMessage,
                description,
                data?.GetType().Name ?? "null",
                rawDataStr.Length > 512 ? rawDataStr[..512] + "..." : rawDataStr
            );
        }
        else
        {
            _logger.LogWarning(
                logMessage,
                description,
                data?.GetType().Name ?? "null",
                rawDataStr.Length > 512 ? rawDataStr[..512] + "..." : rawDataStr
            );
        }
    }

    private static async Task<object> WaitForHandshakeMessageAsync(
        TaskCompletionSource<object>? tcs,
        int timeoutMs,
        string messageName,
        CancellationToken attemptCancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(tcs);
        using CancellationTokenSource timeoutCts = new(timeoutMs);
        using CancellationTokenSource linkedWaitCts =
            CancellationTokenSource.CreateLinkedTokenSource(
                attemptCancellationToken,
                timeoutCts.Token
            );
        try
        {
            return await tcs.Task.WaitAsync(linkedWaitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            throw new ConnectionAttemptFailedException(
                $"Did not receive {messageName} message within {timeoutMs}ms.",
                ex
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ConnectionAttemptFailedException(
                $"Failed waiting for {messageName}, possibly due to prior error set on TCS.",
                ex
            );
        }
        // If cancelled by attemptCancellationToken, OperationCanceledException is rethrown implicitly
    }

    private async Task<object> WaitForResponseAsync(
        TaskCompletionSource<object> tcs,
        int timeoutMs,
        string requestDescription,
        CancellationToken linkedRequestAndLifetimeToken
    )
    {
        using CancellationTokenSource timeoutCts = new(timeoutMs);
        using CancellationTokenSource linkedWaitCts =
            CancellationTokenSource.CreateLinkedTokenSource(
                linkedRequestAndLifetimeToken,
                timeoutCts.Token
            );
        try
        {
            return await tcs.Task.WaitAsync(linkedWaitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            throw new ObsWebSocketException(
                $"{requestDescription} timed out after {timeoutMs}ms.",
                ex
            );
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("{RequestDescription} canceled.", requestDescription);
            throw;
        }
        catch (Exception ex)
        {
            throw new ObsWebSocketException(
                $"{requestDescription} failed while waiting for response.",
                ex
            );
        }
    }

    private async Task SendMessageAsync<T>(
        WebSocketOpCode opCode,
        T payload,
        CancellationToken linkedToken
    )
    {
        IWebSocketConnection? currentWebSocket = _webSocket;
        if (currentWebSocket is null || currentWebSocket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException(
                $"Cannot send '{opCode}', WebSocket not open or available (State: {currentWebSocket?.State})."
            );
        }

        ArgumentNullException.ThrowIfNull(payload);

        OutgoingMessage<T> message = new(opCode, payload);
        _logger.LogTrace("Sending {OpCode} message...", opCode);

        byte[] messageBytes;
        try
        {
            messageBytes = await _serializer
                .SerializeAsync(message, linkedToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Serialization failed for {OpCode} message.", opCode);
            throw new ObsWebSocketException($"Serialization failed for {opCode}.", ex);
        }

        WebSocketMessageType messageType = _serializer.ProtocolSubProtocol.Contains(
            "json",
            StringComparison.OrdinalIgnoreCase
        )
            ? WebSocketMessageType.Text
            : WebSocketMessageType.Binary;

        try
        {
            await currentWebSocket
                .SendAsync(messageBytes.AsMemory(), messageType, true, linkedToken)
                .ConfigureAwait(false);
            _logger.LogTrace("{OpCode} message sent.", opCode);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Send operation for {OpCode} canceled.", opCode);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send {OpCode} message via WebSocket.", opCode);
            throw;
        }
    }

    private void FailPendingRequests(
        ConcurrentDictionary<string, TaskCompletionSource<object>> pending,
        Exception ex,
        CancellationToken ct
    )
    {
        if (pending.IsEmpty)
        {
            return;
        }

        KeyValuePair<string, TaskCompletionSource<object>>[] requestsToFail = [.. pending];
        pending.Clear();
        _logger.LogDebug(
            "Failing {RequestCount} pending request(s) due to: {ExceptionType}",
            requestsToFail.Length,
            ex.GetType().Name
        );
        bool isCancellation = ex is OperationCanceledException;
        foreach ((string _, TaskCompletionSource<object> tcs) in requestsToFail)
        {
            CancellationToken tokenToUse = isCancellation ? ct : CancellationToken.None;
            if (isCancellation)
            {
                tcs.TrySetCanceled(tokenToUse);
            }
            else
            {
                tcs.TrySetException(ex);
            }
        }
    }

    #endregion

    #region Event Raiser Methods
    private void RaiseConnectingEvent(Uri uri, int attempt)
    {
        try
        {
            Connecting?.Invoke(this, new(uri, attempt));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in user-provided Connecting event handler.");
        }
    }

    private void RaiseConnectedEvent()
    {
        try
        {
            Connected?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in user-provided Connected event handler.");
        }
    }

    private void RaiseDisconnectedEvent(Exception? reason)
    {
        try
        {
            Disconnected?.Invoke(this, new(reason));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in user-provided Disconnected event handler.");
        }
    }

    private void RaiseConnectionFailedEvent(Uri uri, int attempt, Exception error)
    {
        try
        {
            ConnectionFailed?.Invoke(this, new(uri, attempt, error));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in user-provided ConnectionFailed event handler.");
        }
    }

    private void RaiseAuthenticationFailureEvent(Uri uri, int attempt, Exception error)
    {
        try
        {
            AuthenticationFailure?.Invoke(this, new(uri, attempt, error));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in user-provided AuthenticationFailure event handler.");
        }
    }
    #endregion

    #region Finalizer
    ~ObsWebSocketClient()
    {
        if (
            _connectionState != ConnectionState.Disconnected
            || _webSocket != null
            || _clientLifetimeCts != null
        )
        {
            // Minimal cleanup attempts in finalizer (avoid logging)
            try
            {
                _clientLifetimeCts?.Cancel();
            }
            catch { }

            try
            {
                _clientLifetimeCts?.Dispose();
            }
            catch { }

            try
            {
                _webSocket?.Abort();
            }
            catch { }

            try
            {
                _webSocket?.Dispose();
            }
            catch { }
        }
    }
    #endregion

    #region Internal Exception Types
    internal sealed class ConnectionAttemptFailedException(string message, Exception? inner = null)
        : ObsWebSocketException(message, inner);

    internal sealed class AuthenticationFailureException(string message, Exception? inner = null)
        : ObsWebSocketException(message, inner);
    #endregion
}
