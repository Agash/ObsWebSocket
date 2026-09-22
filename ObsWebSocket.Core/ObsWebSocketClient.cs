using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ObsWebSocket.Core.Events;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Events;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Serialization;
using Polly;
using Polly.Registry;

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
public sealed partial class ObsWebSocketClient : IAsyncDisposable
{
    #region Construction

    /// <summary>
    /// Initializes a client that selects its serializer per connection.
    /// </summary>
    /// <remarks>
    /// To fix a client to one serializer, pass a factory that ignores the format:
    /// <c>_ => serializer</c>.
    /// </remarks>
    /// <param name="logger">Logger for connection and protocol activity.</param>
    /// <param name="serializerFactory">Supplies the serializer for a format.</param>
    /// <param name="options">The client's options, read live.</param>
    /// <param name="connectionFactory">Creates the underlying sockets.</param>
    /// <param name="timeProvider">Source of time for timeouts and backoff.</param>
    /// <param name="metrics">The instruments to record to.</param>
    /// <param name="pipelines">Resolves a pipeline registered under <see cref="ObsWebSocketResilience.NotReadyPipelineKey"/>.</param>
    /// <param name="reconnectDelays">Supplies the reconnect backoff curve.</param>
    public ObsWebSocketClient(
        ILogger<ObsWebSocketClient> logger,
        ObsSerializerFactory serializerFactory,
        IOptions<ObsWebSocketClientOptions> options,
        IWebSocketConnectionFactory? connectionFactory = null,
        TimeProvider? timeProvider = null,
        ObsWebSocketMetrics? metrics = null,
        ResiliencePipelineProvider<string>? pipelines = null,
        IObsReconnectDelays? reconnectDelays = null
    )
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serializerFactory =
            serializerFactory ?? throw new ArgumentNullException(nameof(serializerFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _connectionFactory = connectionFactory ?? new WebSocketConnectionFactory();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _metrics = metrics ?? ObsWebSocketMetrics.Shared;
        _pipelines = pipelines;
        _reconnectDelays = reconnectDelays;
    }

    /// <summary>
    /// Runs <paramref name="operation"/> through the NotReady pipeline, which retries only the
    /// status OBS documents as retryable. Each attempt sends a fresh request, because OBS pairs a
    /// response to the id it was sent with.
    /// </summary>
    private async Task<T> ThroughNotReadyPipelineAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken
    )
    {
        // Only a pipeline the application registered wins. The fallback is built from this
        // client's own options, which for a named client are its own.
        ResiliencePipeline pipeline =
            _pipelines?.TryGetPipeline(
                ObsWebSocketResilience.NotReadyPipelineKey,
                out ResiliencePipeline? registered
            ) == true
                ? registered!
                : _notReadyFallback ?? BuildNotReadyFallback();

        return await pipeline
            .ExecuteAsync(async ct => await operation(ct).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
    }

    private ResiliencePipeline? _notReadyFallback;

    private ResiliencePipeline BuildNotReadyFallback()
    {
        NotReadyRetryOptions retry = _options.Value.NotReadyRetry;
        if (!retry.Enabled)
        {
            return _notReadyFallback = ResiliencePipeline.Empty;
        }

        ResiliencePipelineBuilder builder = new() { TimeProvider = _timeProvider };
        _ = builder.AddRetry(ObsWebSocketResilience.CreateNotReadyRetryOptions(retry));
        return _notReadyFallback = builder.Build();
    }

    #endregion

    #region Fields
    internal readonly ILogger _logger;
    private readonly ResiliencePipelineProvider<string>? _pipelines;
    private readonly IObsReconnectDelays? _reconnectDelays;
    private readonly ObsSerializerFactory _serializerFactory;
    internal readonly IOptions<ObsWebSocketClientOptions> _options;
    private readonly IWebSocketConnectionFactory _connectionFactory;

    /// <summary>Source of time for all timeouts and reconnect delays.</summary>
    internal readonly TimeProvider _timeProvider;

    private readonly ObsWebSocketMetrics _metrics;

    /// <summary>
    /// Default size of the receive buffer for WebSocket messages.
    /// </summary>
    public const int ReceiveBufferSize = 8192;

    /// <summary>
    /// Default timeout in milliseconds for the initial handshake phase (Hello/Identified).
    /// </summary>
    public const int DefaultHandshakeTimeoutMs = 5000;

    /// <summary>
    /// Default timeout in milliseconds for awaiting individual request responses.
    /// </summary>
    public const int DefaultRequestTimeoutMs = 10000;

    /// <summary>
    /// Default multiplier for the timeout of batch requests.
    /// </summary>
    public const int DefaultBatchTimeoutMultiplier = 2;

    /// <summary>
    /// Default ceiling on the size of a single inbound message, in bytes (64 MiB).
    /// </summary>
    /// <remarks>
    /// Chosen to sit well above the largest response OBS realistically sends - a 4K
    /// <c>GetSourceScreenshot</c> data URI runs to single-digit megabytes - while still bounding
    /// what a peer can make this process allocate.
    /// </remarks>
    public const int DefaultMaxIncomingMessageBytes = 64 * 1024 * 1024;

    /// <summary>
    /// The live connection, or <see langword="null"/> when there is none.
    /// </summary>
    private volatile ObsConnectionContext? _connection;

    private volatile ConnectionState _connectionState = ConnectionState.Disconnected;
    private Task? _connectionLoopTask;
    private CancellationTokenSource? _clientLifetimeCts;
    private readonly Lock _connectionLock = new();
    private Exception? _completionException;

    /// <summary>
    /// Serializes re-identification, which the protocol cannot correlate on its own.
    /// </summary>
    /// <remarks>
    /// <c>Identified</c> carries no request id, so concurrent operations cannot be correlated to
    /// their replies.
    /// </remarks>
    private readonly SemaphoreSlim _reidentifyGate = new(1, 1);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<object>> _pendingRequests =
        new();
    private readonly ConcurrentDictionary<
        string,
        TaskCompletionSource<object>
    > _pendingBatchRequests = new();

    private TaskCompletionSource _initialConnectionTcs = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private static readonly JsonSerializerOptions s_payloadJsonOptions = ObsWebSocket
        .Core
        .Serialization
        .ObsWebSocketJsonContext
        .Default
        .Options;
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
    public EventSubscription? CurrentEventSubscriptions { get; private set; }
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
        // Captured once so a reload mid-sequence cannot change what is being connected to.
        ObsConnectionSettings settings = ObsConnectionSettings.Capture(_options.Value);

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
                () => ConnectionLoopAsync(settings, cancellationToken),
                CancellationToken.None
            );
        }

        _logger.LogStartingConnectionSequenceFor(settings.ServerUri);

        try
        {
            // Await the initial connection TCS, not the whole loop task
            using CancellationTokenSource linkedTimeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            int overallTimeout = Math.Max(settings.HandshakeTimeoutMs * 2, DefaultRequestTimeoutMs); // Be generous
            linkedTimeoutCts.CancelAfterUsing(
                _timeProvider,
                TimeSpan.FromMilliseconds(overallTimeout)
            );

            await currentInitialConnectionTcs
                .Task.WaitAsync(linkedTimeoutCts.Token)
                .ConfigureAwait(false);

            // If TCS completed successfully, connection is established.
            _logger.LogConnectasyncInitialConnectionConfirmedSuccessfully();
        }
        catch (Exception ex) // Catches exceptions set on the TCS or cancellation
        {
            _logger.LogConnectasyncFailedToEstablishInitialConnection(ex);
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

        // Held across send/wait/publish: holding it only for the send would leave two callers
        // waiting on the same uncorrelated reply.
        await _reidentifyGate.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        try
        {
            // Re-read after the gate: waiting for it may have outlasted the connection.
            ObsConnectionContext connection = RequireConnection();
            TaskCompletionSource<object> identified = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            connection.Identified = identified;
            int effectiveTimeout = timeoutMs ?? _options.Value.RequestTimeoutMs;

            try
            {
                await SendMessageAsync(
                        WebSocketOpCode.Reidentify,
                        new ReidentifyPayload(eventSubscriptions),
                        linkedCts.Token
                    )
                    .ConfigureAwait(false);

                _logger.LogWaitingForIdentifiedAfterReidentifyTimeoutMs(effectiveTimeout);
                object identifiedMessageObj = await WaitForHandshakeMessageAsync(
                        identified,
                        effectiveTimeout,
                        "Identified (after Reidentify)",
                        _timeProvider,
                        linkedCts.Token
                    )
                    .ConfigureAwait(false);
                IdentifiedPayload identifiedPayload =
                    ExtractPayloadFromHandshake<IdentifiedPayload>(
                        connection,
                        identifiedMessageObj,
                        "Identified (after Reidentify)"
                    );

                _logger.LogReIdentificationSuccessfulRpcVersion(
                    identifiedPayload.NegotiatedRpcVersion
                );

                NegotiatedRpcVersion = identifiedPayload.NegotiatedRpcVersion;
                CurrentEventSubscriptions = eventSubscriptions is null
                    ? null
                    : (EventSubscription)eventSubscriptions.Value;
            }
            catch (Exception ex)
                when (ex is not OperationCanceledException
                    || cancellationToken.IsCancellationRequested
                )
            {
                _logger.LogReidentifyasyncFailed(ex);
                _ = identified.TrySetException(ex);
                throw ex is ObsWebSocketException or OperationCanceledException
                    ? ex
                    : new ObsWebSocketException($"Reidentify failed: {ex.Message}", ex);
            }
            catch (OperationCanceledException) when (_clientLifetimeCts.IsCancellationRequested)
            {
                _logger.LogReidentifyasyncCanceledDueToClientShutdown();
                _ = identified.TrySetCanceled(_clientLifetimeCts.Token);
                throw;
            }
        }
        finally
        {
            _ = _reidentifyGate.Release();
        }
    }

    /// <summary>
    /// Sends a request whose successful response is expected to carry data, and returns it.
    /// </summary>
    /// <typeparam name="TResponse">The expected response data type.</typeparam>
    /// <param name="requestType">The OBS request type string.</param>
    /// <param name="requestData">The request payload, or <see langword="null"/>.</param>
    /// <param name="requestTypeInfo">
    /// Metadata for <paramref name="requestData"/>, for a type this library does not know.
    /// Supplying it from your own <c>JsonSerializerContext</c> keeps the call AOT safe and
    /// avoids hand building a <see cref="System.Text.Json.JsonElement"/>.
    /// </param>
    /// <param name="responseTypeInfo">
    /// Metadata for <typeparamref name="TResponse"/>, for a type this library does not know.
    /// Without it the response is resolved from this library's context, which has no entry for a
    /// type it did not generate.
    /// </param>
    /// <param name="timeoutMs">Optional override for the request timeout.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The response data.</returns>
    /// <exception cref="ObsWebSocketException">
    /// Thrown if the request fails, or if OBS reports success without the expected data.
    /// </exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    public async Task<TResponse> CallRequiredAsync<TResponse>(
        string requestType,
        object? requestData = null,
        JsonTypeInfo? requestTypeInfo = null,
        JsonTypeInfo<TResponse>? responseTypeInfo = null,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default
    )
        where TResponse : class =>
        await CallAsync(
                requestType,
                requestData,
                requestTypeInfo,
                responseTypeInfo,
                timeoutMs,
                cancellationToken
            )
            .ConfigureAwait(false)
        ?? throw new ObsWebSocketException(
            $"OBS reported success for '{requestType}' but returned no {typeof(TResponse).Name} payload."
        );

    /// <summary>
    /// Sends a request to the OBS WebSocket server and awaits its response.
    /// Use this for requests expected to return complex objects (classes/records).
    /// </summary>
    /// <typeparam name="TResponse">The expected type of the response data payload (must be a reference type).</typeparam>
    /// <param name="requestType">The OBS WebSocket request type string.</param>
    /// <param name="requestData">Optional data payload for the request. Should be serializable to the format expected by OBS for the request type.</param>
    /// <param name="requestTypeInfo">
    /// Metadata for <paramref name="requestData"/>, for a type this library does not know.
    /// Supplying it from your own <c>JsonSerializerContext</c> keeps the call AOT safe and
    /// avoids hand building a <see cref="System.Text.Json.JsonElement"/>.
    /// </param>
    /// <param name="responseTypeInfo">
    /// Metadata for <typeparamref name="TResponse"/>, for a type this library does not know.
    /// Without it the response is resolved from this library's context, which has no entry for a
    /// type it did not generate.
    /// </param>
    /// <param name="timeoutMs">Optional timeout in milliseconds to wait for the response. Defaults to <see cref="ObsWebSocketClientOptions.RequestTimeoutMs"/>.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>
    /// A task representing the asynchronous operation. Yields the deserialized response data payload
    /// (<typeparamref name="TResponse"/>), or <c>null</c> if the successful response does not contain data.
    /// </returns>
    /// <exception cref="ObsWebSocketException">Thrown if the request fails on the OBS side (indicated by the response status) or if serialization/deserialization fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    /// <exception cref="OperationCanceledException">Thrown if the operation is cancelled via the <paramref name="cancellationToken"/> or the request times out.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="requestType"/> is null or empty.</exception>
    public Task<TResponse?> CallAsync<TResponse>(
        string requestType,
        object? requestData = null,
        JsonTypeInfo? requestTypeInfo = null,
        JsonTypeInfo<TResponse>? responseTypeInfo = null,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default
    )
        where TResponse : class =>
        ThroughNotReadyPipelineAsync(
            ct =>
                CallOnceAsync(
                    requestType,
                    requestData,
                    requestTypeInfo,
                    responseTypeInfo,
                    timeoutMs,
                    ct
                ),
            cancellationToken
        );

    private async Task<TResponse?> CallOnceAsync<TResponse>(
        string requestType,
        object? requestData,
        JsonTypeInfo? requestTypeInfo,
        JsonTypeInfo<TResponse>? responseTypeInfo,
        int? timeoutMs,
        CancellationToken cancellationToken
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

        using Activity? activity = ObsWebSocketDiagnostics.ActivitySource.StartActivity(
            $"obsws {requestType}",
            ActivityKind.Client
        );
        _ = activity?.SetTag("obsws.request_type", requestType);
        _ = activity?.SetTag("obsws.request_id", requestId);

        long startedAt = _timeProvider.GetTimestamp();
        TagList requestTags = new() { { "obsws.request_type", requestType } };

        try
        {
            await SendMessageAsync(
                    WebSocketOpCode.Request,
                    new RequestPayload(
                        requestType,
                        requestId,
                        SerializeRequestData(requestType, requestData, requestTypeInfo)
                    ),
                    linkedCts.Token
                )
                .ConfigureAwait(false);

            _metrics.RequestsSent.Add(1, requestTags);

            int effectiveTimeout = timeoutMs ?? _options.Value.RequestTimeoutMs;

            _logger.LogWaitingForResponseTimeoutMs(requestId, requestType, effectiveTimeout);

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

            _metrics.RequestDuration.Record(
                _timeProvider.GetElapsedTime(startedAt).TotalMilliseconds,
                requestTags
            );

            // `object` is what the generated methods ask for when the request declares no response
            // payload. There is nothing to deserialize into, and OBS sends one anyway for some of
            // them, so attempting it fails on a request that actually succeeded.
            return typeof(TResponse) == typeof(object)
                ? null
                : RequireConnection()
                    .Serializer.DeserializePayload(response.ResponseData, responseTypeInfo);
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            _metrics.RequestsFailed.Add(1, requestTags);
            _ = activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            _logger.LogCallasyncFailedFor(ex, requestType, requestId);

            _ = tcs.TrySetException(ex); // Ensure TCS is completed on failure

            throw;
        }
        catch (OperationCanceledException) when (_clientLifetimeCts.IsCancellationRequested)
        {
            _logger.LogCallasyncForCanceled(requestType, requestId);

            _ = tcs.TrySetCanceled(_clientLifetimeCts.Token);

            throw;
        }
        finally
        {
            _ = _pendingRequests.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Sends a request to the OBS WebSocket server and awaits its response.
    /// Use this for requests expected to return value types (structs).
    /// </summary>
    /// <typeparam name="TResponse">The expected type of the response data payload (must be a value type).</typeparam>
    /// <param name="requestType">The OBS WebSocket request type string.</param>
    /// <param name="requestData">Optional data payload for the request. Should be serializable to the format expected by OBS for the request type.</param>
    /// <param name="requestTypeInfo">
    /// Metadata for <paramref name="requestData"/>, for a type this library does not know.
    /// Supplying it from your own <c>JsonSerializerContext</c> keeps the call AOT safe and
    /// avoids hand building a <see cref="System.Text.Json.JsonElement"/>.
    /// </param>
    /// <param name="responseTypeInfo">
    /// Metadata for <typeparamref name="TResponse"/>, for a type this library does not know.
    /// Without it the response is resolved from this library's context, which has no entry for a
    /// type it did not generate.
    /// </param>
    /// <param name="timeoutMs">Optional timeout in milliseconds to wait for the response. Defaults to <see cref="ObsWebSocketClientOptions.RequestTimeoutMs"/>.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>
    /// A task representing the asynchronous operation. Yields a nullable <typeparamref name="TResponse"/> containing the
    /// deserialized response data payload, or <c>null</c> if the successful response does not contain data.
    /// </returns>
    /// <exception cref="ObsWebSocketException">Thrown if the request fails on the OBS side (indicated by the response status), if serialization/deserialization fails, or if a null value is deserialized for a non-nullable struct.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the client is not connected.</exception>
    /// <exception cref="OperationCanceledException">Thrown if the operation is cancelled via the <paramref name="cancellationToken"/> or the request times out.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="requestType"/> is null or empty.</exception>
    public Task<TResponse?> CallAsyncValue<TResponse>(
        string requestType,
        object? requestData = null,
        JsonTypeInfo? requestTypeInfo = null,
        JsonTypeInfo<TResponse>? responseTypeInfo = null,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default
    )
        where TResponse : struct =>
        ThroughNotReadyPipelineAsync(
            ct =>
                CallValueOnceAsync(
                    requestType,
                    requestData,
                    requestTypeInfo,
                    responseTypeInfo,
                    timeoutMs,
                    ct
                ),
            cancellationToken
        );

    private async Task<TResponse?> CallValueOnceAsync<TResponse>(
        string requestType,
        object? requestData,
        JsonTypeInfo? requestTypeInfo,
        JsonTypeInfo<TResponse>? responseTypeInfo,
        int? timeoutMs,
        CancellationToken cancellationToken
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
                        SerializeRequestData(requestType, requestData, requestTypeInfo)
                    ),
                    linkedCts.Token
                )
                .ConfigureAwait(false);

            int effectiveTimeout = timeoutMs ?? _options.Value.RequestTimeoutMs;
            _logger.LogWaitingForResponseTimeoutMs(requestId, requestType, effectiveTimeout);

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

            TResponse? result = RequireConnection()
                .Serializer.DeserializeValuePayload(response.ResponseData, responseTypeInfo);
            return (!result.HasValue && Nullable.GetUnderlyingType(typeof(TResponse)) == null)
                ? throw new ObsWebSocketException(
                    $"Null deserialization for non-nullable value type '{typeof(TResponse).Name}'."
                )
                : result;
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            _logger.LogCallasyncvalueFailedFor(ex, requestType, requestId);
            _ = tcs.TrySetException(ex);
            throw ex is ObsWebSocketException
                ? ex
                : new ObsWebSocketException($"Error in CallAsyncValue for '{requestType}'.", ex);
        }
        catch (OperationCanceledException) when (_clientLifetimeCts.IsCancellationRequested)
        {
            _logger.LogCallasyncvalueForCanceled(requestType, requestId);
            _ = tcs.TrySetCanceled(_clientLifetimeCts.Token);
            throw;
        }
        finally
        {
            _ = _pendingRequests.TryRemove(requestId, out _);
        }
    }

    /// <inheritdoc/>
    public Task<List<RequestResponsePayload<object>>> CallBatchAsync(
        IEnumerable<BatchRequestItem> requests,
        RequestBatchExecutionType? executionType = null,
        bool? haltOnFailure = null,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default
    ) =>
        ThroughNotReadyPipelineAsync(
            async ct =>
            {
                List<RequestResponsePayload<object>> results = await CallBatchOnceAsync(
                        requests,
                        executionType,
                        haltOnFailure,
                        timeoutMs,
                        ct
                    )
                    .ConfigureAwait(false);

                // A batch OBS was not ready for comes back as results, not as a failed call: every
                // entry carries NotReady. Raising it is what lets the pipeline see it.
                if (
                    _options.Value.NotReadyRetry.Enabled
                    && results.Count > 0
                    && results.TrueForAll(result =>
                        result.RequestStatus.Code == (int)RequestStatusCode.NotReady
                    )
                )
                {
                    throw new ObsWebSocketRequestException(
                        "OBS is not ready to perform the request.",
                        "RequestBatch",
                        string.Empty,
                        new RequestStatus(false, (int)RequestStatusCode.NotReady, null),
                        null
                    );
                }

                return results;
            },
            cancellationToken
        );

    private async Task<List<RequestResponsePayload<object>>> CallBatchOnceAsync(
        IEnumerable<BatchRequestItem> requests,
        RequestBatchExecutionType? executionType,
        bool? haltOnFailure,
        int? timeoutMs,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(requests);
        EnsureConnected();
        Debug.Assert(_clientLifetimeCts != null);

        string batchRequestId = Guid.NewGuid().ToString();

        using Activity? activity = ObsWebSocketDiagnostics.ActivitySource.StartActivity(
            "obsws batch",
            ActivityKind.Client
        );
        _ = activity?.SetTag("obsws.request_id", batchRequestId);

        long startedAt = _timeProvider.GetTimestamp();
        TagList batchTags = new() { { "obsws.request_type", "RequestBatch" } };

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
            _logger.LogEmptyBatchRequest();
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

            // A batch is one round trip but OBS executes each item in turn, so allow the base
            // request timeout for the round trip plus a share of it per item.
            int baseTimeout = _options.Value.RequestTimeoutMs;
            int effectiveTimeout =
                timeoutMs
                ?? (
                    (baseTimeout * DefaultBatchTimeoutMultiplier)
                    + (baseTimeout * requestPayloads.Count / 2)
                );
            _logger.LogWaitingForBatchResponseTimeoutMs(
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

            _logger.LogReceivedBatchResponseResults(batchRequestId, response.Results.Count);

            _ = activity?.SetTag("obsws.batch.size", requestPayloads.Count);
            _metrics.RequestsSent.Add(1, batchTags);
            _metrics.RequestDuration.Record(
                _timeProvider.GetElapsedTime(startedAt).TotalMilliseconds,
                batchTags
            );

            return OrderBatchResults(response.Results, requestPayloads);
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            _metrics.RequestsFailed.Add(1, batchTags);
            _ = activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            _logger.LogCallbatchasyncFailedForBatch(ex, batchRequestId);
            _ = tcs.TrySetException(ex);
            throw ex is ObsWebSocketException
                ? ex
                : new ObsWebSocketException($"Error in CallBatchAsync for '{batchRequestId}'.", ex);
        }
        catch (OperationCanceledException) when (_clientLifetimeCts.IsCancellationRequested)
        {
            _logger.LogCallbatchasyncForCanceled(batchRequestId);
            _ = tcs.TrySetCanceled(_clientLifetimeCts.Token);
            throw;
        }
        finally
        {
            _ = _pendingBatchRequests.TryRemove(batchRequestId, out _);
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
                _logger.LogDisconnectasyncIgnoredAlready(_connectionState);
                return;
            }

            startedDisconnect = true;
            _logger.LogDisconnectasyncInitiatingGracefulShutdown();
            _connectionState = ConnectionState.Disconnecting;
            IsConnected = false;
            _clientLifetimeCts?.Cancel();
            loopTaskToWait = _connectionLoopTask;
        }

        if (startedDisconnect && loopTaskToWait != null)
        {
            _logger.LogWaitingForConnectionLoopTaskToComplete();
            try
            {
                using CancellationTokenSource combinedWaitCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                combinedWaitCts.CancelAfterUsing(_timeProvider, TimeSpan.FromSeconds(5));
                await loopTaskToWait
                    .WaitAsync(combinedWaitCts.Token)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                _logger.LogConnectionLoopTaskCompletedOrWaitTimed();
            }
            catch (OperationCanceledException)
            {
                _logger.LogWaitForConnectionLoopTaskTimedOut();
            }
            catch (Exception ex)
            {
                _logger.LogExceptionFromConnectionLoopTaskDuringDisconnectasync(ex);
            }
        }

        // Finalize with a null reason because DisconnectAsync initiated it.
        await FinalizeDisconnectionAsync(closeStatus, statusDescription, reasonException: null)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _logger.LogDisposeasyncCalled();
        await DisconnectAsync(
                WebSocketCloseStatus.NormalClosure,
                "Client disposing",
                CancellationToken.None
            )
            .ConfigureAwait(false);
        GC.SuppressFinalize(this);
        _logger.LogDisposeasyncCompleted();
    }

    #endregion

    #region Connection Loop and Internal Logic

    private async Task ConnectionLoopAsync(
        ObsConnectionSettings settings,
        CancellationToken externalCancellationToken
    )
    {
        int attempt = 0;
        IObsReconnectDelays reconnectDelays = _reconnectDelays ?? new ReconnectDelays(settings);
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

        bool connectionWasLost = false;

        try
        {
            while (!loopToken.IsCancellationRequested)
            {
                // A live connection resets the attempt count, so the retry gate below never sees
                // the first attempt after a drop. Without this, a client told not to reconnect
                // would reconnect once after every lost connection.
                if (connectionWasLost && !settings.AutoReconnectEnabled)
                {
                    _logger.LogMaxReconnectAttemptsReachedOrAutoReconnect(
                        settings.MaxReconnectAttempts
                    );
                    break;
                }

                attempt++;
                bool isConnectedThisAttempt = false;
                Exception? attemptException = null;
                ObsConnectionContext? connection = null;

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
                        int maxAttempts = settings.MaxReconnectAttempts;
                        if (
                            !settings.AutoReconnectEnabled
                            || (maxAttempts >= 0 && (attempt - 1) >= maxAttempts)
                        )
                        {
                            _logger.LogMaxReconnectAttemptsReachedOrAutoReconnect(maxAttempts);
                            _completionException = new ObsWebSocketException(
                                $"Failed to connect after {attempt - 1} attempts.",
                                _completionException
                            );
                            _ = initialTcs.TrySetException(_completionException); // Fail the initial connect TCS
                            break;
                        }

                        TimeSpan backoff = await reconnectDelays
                            .GetDelayAsync(attempt - 2, loopToken)
                            .ConfigureAwait(false);
                        _logger.LogReconnectingAttemptAfterMs(
                            attempt,
                            maxAttempts < 0 ? "Infinite" : maxAttempts.ToString(),
                            (int)backoff.TotalMilliseconds
                        );
                        _metrics.Reconnects.Add(1);
                        await Task.Delay(backoff, _timeProvider, loopToken).ConfigureAwait(false);
                    }

                    RaiseConnectingEvent(settings.ServerUri, attempt);

                    connection = await TryConnectAndIdentifyAsync(settings, attempt, loopToken)
                        .ConfigureAwait(false);

                    isConnectedThisAttempt = true;
                    using (_connectionLock.EnterScope())
                    {
                        if (_connectionState == ConnectionState.Disconnecting)
                        {
                            _logger.LogConnectedAttemptButDisconnectRequestedAborting(attempt);
                            _completionException = new TaskCanceledException(
                                "Disconnect requested during connect."
                            );
                            _ = initialTcs.TrySetCanceled(loopToken); // Signal cancellation
                            break;
                        }

                        _connectionState = ConnectionState.Connected;
                        IsConnected = true;
                        _logger.LogAttemptHandshakeCompleteReceiveLoopIsRunning(attempt);
                    }

                    RaiseConnectedEvent();
                    _logger.LogSuccessfullyConnectedAndIdentifiedAttempt(attempt);
                    _completionException = null;
                    _ = initialTcs.TrySetResult(); // Signal successful initial connection
                    attempt = 0; // Reset attempt count only on full success

                    Debug.Assert(connection.ReceiveTask != null);
                    _logger.LogConnectionEstablishedWaitingForReceiveLoopCompletion();
                    await connection.ReceiveTask!.WaitAsync(loopToken).ConfigureAwait(false); // Wait for disconnect/shutdown
                    _logger.LogReceiveLoopTaskCompletedWhileConnected();
                }
                catch (OperationCanceledException ex) when (loopToken.IsCancellationRequested)
                {
                    _logger.LogConnectionLoopCanceledAttempt(attempt);
                    attemptException = ex;
                    _ = initialTcs.TrySetCanceled(loopToken); // Signal cancellation
                    break;
                }
                catch (AuthenticationFailureException authEx)
                {
                    _logger.LogAuthenticationFailedAttemptStopping(authEx, attempt);
                    attemptException = authEx;
                    RaiseAuthenticationFailureEvent(settings.ServerUri, attempt, authEx);
                    _ = initialTcs.TrySetException(authEx); // Signal auth failure
                    break; // Auth failure is fatal
                }
                catch (ConnectionAttemptFailedException connEx)
                {
                    _logger.LogConnectAttemptFailedRetrying(
                        connEx.InnerException ?? connEx,
                        attempt
                    );
                    attemptException = connEx;
                    RaiseConnectionFailedEvent(settings.ServerUri, attempt, connEx);
                    // Only fail initial TCS if retries are disabled or exhausted on the *first* attempt
                    if (
                        attempt == 1
                        && (!settings.AutoReconnectEnabled || settings.MaxReconnectAttempts == 0)
                    )
                    {
                        _ = initialTcs.TrySetException(connEx);
                    }
                }
                catch (WebSocketException wsEx)
                {
                    _logger.LogWebsocketexceptionDuringConnectionReceiveAttemptRetrying(
                        wsEx,
                        attempt
                    );
                    attemptException = wsEx;
                    RaiseConnectionFailedEvent(settings.ServerUri, attempt, wsEx);
                    if (
                        attempt == 1
                        && (!settings.AutoReconnectEnabled || settings.MaxReconnectAttempts == 0)
                    )
                    {
                        _ = initialTcs.TrySetException(
                            new ConnectionAttemptFailedException(wsEx.Message, wsEx)
                        );
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogUnexpectedErrorInConnectionLoopAttemptRetrying(ex, attempt);
                    attemptException = ex;
                    RaiseConnectionFailedEvent(settings.ServerUri, attempt, ex);
                    if (
                        attempt == 1
                        && (!settings.AutoReconnectEnabled || settings.MaxReconnectAttempts == 0)
                    )
                    {
                        _ = initialTcs.TrySetException(
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

                    connectionWasLost = isConnectedThisAttempt;
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
                            }

                            // A later attempt may already have published its own.
                            if (ReferenceEquals(_connection, connection))
                            {
                                _connection = null;
                            }
                        }

                        if (connection is not null)
                        {
                            // Awaited so two receive loops never overlap across attempts.
                            await connection.DisposeAsync().ConfigureAwait(false);
                        }

                        if (isConnectedThisAttempt && attemptException != null)
                        {
                            _logger.LogConnectionLostDuringConnectedStateDueTo(
                                attemptException.GetType().Name
                            );
                        }
                    }
                }
            } // End while
        }
        catch (Exception ex)
        {
            _logger.LogCatastrophicErrorInConnectionloopasync(ex);
            _completionException = ex;
            _ = initialTcs.TrySetException(ex); // Ensure TCS is faulted
        }
        finally
        {
            _logger.LogExitedConnectionLoopFinalizingState();
            // If the loop exited without success/explicit failure setting the initial TCS, fail it now.
            _ = initialTcs.TrySetException(
                _completionException
                    ?? new ObsWebSocketException(
                        "Connection loop exited unexpectedly before initial connection completed."
                    )
            );
            await FinalizeDisconnectionAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Connection loop ended.",
                    _completionException
                )
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Makes one connection attempt and, if it succeeds, publishes it as the live connection.
    /// </summary>
    /// <remarks>
    /// The serializer comes from this attempt's settings, so the sub-protocol offered during
    /// the handshake and the serializer used afterwards cannot drift apart.
    /// </remarks>
    /// <param name="settings">The settings this attempt connects with.</param>
    /// <param name="attempt">Attempt number, for logging.</param>
    /// <param name="ct">Cancels the attempt.</param>
    /// <returns>The established connection.</returns>
    private async Task<ObsConnectionContext> TryConnectAndIdentifyAsync(
        ObsConnectionSettings settings,
        int attempt,
        CancellationToken ct
    )
    {
        Debug.Assert(_clientLifetimeCts != null);

        IWebSocketConnection ws = _connectionFactory.CreateConnection();
        IWebSocketMessageSerializer serializer = _serializerFactory(settings.Format);
        ObsConnectionContext connection = new(ws, serializer, settings, _clientLifetimeCts.Token);

        try
        {
            // Add SubProtocol only if needed
            if (
                string.IsNullOrEmpty(ws.SubProtocol)
                || ws.SubProtocol != serializer.ProtocolSubProtocol
            )
            {
                try
                {
                    ws.Options.AddSubProtocol(serializer.ProtocolSubProtocol);
                }
                catch (ArgumentException ex)
                {
                    _logger.LogAttemptedDuplicateSubprotocolAdd(ex, serializer.ProtocolSubProtocol);
                }
            }

            // Connect
            using CancellationTokenSource connectTimeoutCts = new(settings.HandshakeTimeoutMs);
            using CancellationTokenSource linkedConnectCts =
                CancellationTokenSource.CreateLinkedTokenSource(ct, connectTimeoutCts.Token);
            _logger.LogAttemptConnecting(attempt);
            try
            {
                await ws.ConnectAsync(settings.ServerUri, linkedConnectCts.Token)
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

            _logger.LogAttemptWebsocketConnectionEstablishedProtocol(
                attempt,
                ws.SubProtocol ?? "(None)"
            );

            // --- Start Receive Loop *before* Handshake ---
            // Published first: the loop reads the connection, and Hello can arrive immediately.
            _connection = connection;
            connection.ReceiveTask = Task.Run(
                () => ReceiveLoopAsync(connection),
                connection.ConnectionClosed
            );
            _logger.LogAttemptReceiveLoopStartedForHandshake(attempt);

            // --- Handshake ---
            _logger.LogAttemptWaitingForHello(attempt);
            object helloMsgObj = await WaitForHandshakeMessageAsync(
                    connection.Hello,
                    settings.HandshakeTimeoutMs,
                    "Hello",
                    _timeProvider,
                    ct
                )
                .ConfigureAwait(false);
            HelloPayload helloPayload = ExtractPayloadFromHandshake<HelloPayload>(
                connection,
                helloMsgObj,
                "Hello"
            );
            _logger.LogAttemptReceivedHelloRpcVersion(attempt, helloPayload.RpcVersion);
            string? authResponse = null;
            if (helloPayload.Authentication != null)
            {
                _logger.LogAttemptAuthenticationRequired(attempt);
                if (string.IsNullOrEmpty(settings.Password))
                {
                    throw new AuthenticationFailureException(
                        "Authentication required by server, but no password was provided."
                    );
                }

                authResponse = AuthenticationHelper.GenerateAuthenticationString(
                    helloPayload.Authentication.Salt,
                    helloPayload.Authentication.Challenge,
                    settings.Password
                );
            }

            EventSubscription requestedEventSubs = settings.EventSubscriptions;

            await SendMessageAsync(
                    WebSocketOpCode.Identify,
                    new IdentifyPayload(
                        helloPayload.RpcVersion,
                        authResponse,
                        (uint)requestedEventSubs
                    ),
                    ct
                )
                .ConfigureAwait(false);
            _logger.LogAttemptWaitingForIdentified(attempt);
            object identifiedMsgObj = await WaitForHandshakeMessageAsync(
                    connection.Identified,
                    settings.HandshakeTimeoutMs,
                    "Identified",
                    _timeProvider,
                    ct
                )
                .ConfigureAwait(false);

            IdentifiedPayload identifiedPayload = ExtractPayloadFromHandshake<IdentifiedPayload>(
                connection,
                identifiedMsgObj,
                "Identified"
            );

            _logger.LogAttemptReceivedIdentifiedNegotiatedRpcVersion(
                attempt,
                identifiedPayload.NegotiatedRpcVersion
            );

            NegotiatedRpcVersion = identifiedPayload.NegotiatedRpcVersion;
            CurrentEventSubscriptions = requestedEventSubs;

            return connection;
        }
        catch (Exception ex)
        {
            NegotiatedRpcVersion = null;
            CurrentEventSubscriptions = null;

            _ = connection.Hello.TrySetException(ex);
            _ = connection.Identified.TrySetException(ex);

            using (_connectionLock.EnterScope())
            {
                if (ReferenceEquals(_connection, connection))
                {
                    _connection = null;
                }
            }

            await connection.DisposeAsync().ConfigureAwait(false);

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

            throw new ConnectionAttemptFailedException(
                $"Connection attempt {attempt} failed during handshake: {ex.Message}",
                ex
            );
        }
    }

    /// <summary> Receives messages from the WebSocket. </summary>
    private async Task ReceiveLoopAsync(ObsConnectionContext connection)
    {
        CancellationToken cancellationToken = connection.ConnectionClosed;
        using MemoryStream bufferStream = new();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        IWebSocketConnection currentWebSocket = connection.Transport;

        // Per connection, so a reload cannot move the ceiling mid-message.
        int maxMessageBytes = Math.Max(1, _options.Value.MaxIncomingMessageBytes);

        try
        {
            if (currentWebSocket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException(
                    $"Receive loop started with WebSocket not open. State: {currentWebSocket.State}"
                );
            }

            _logger.LogReceiveLoopStartingForWebsocket(
                RuntimeHelpers.GetHashCode(currentWebSocket)
            );

            while (!cancellationToken.IsCancellationRequested)
            {
                if (currentWebSocket.State != WebSocketState.Open)
                {
                    _logger.LogWebsocketStateChangedToDuringReceiveLoop(currentWebSocket.State);
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
                        HandleServerClose(connection);
                        return;
                    }

                    // Before the write, so the growth the limit prevents never happens.
                    // Subtraction because the sum can overflow int.
                    if (result.Count > maxMessageBytes - bufferStream.Length)
                    {
                        throw new ObsWebSocketMessageTooLargeException(
                            maxMessageBytes,
                            bufferStream.Length + result.Count
                        );
                    }

                    await bufferStream
                        .WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken)
                        .ConfigureAwait(false);
                } while (!result.EndOfMessage);

                if (bufferStream.Length == 0)
                {
                    _logger.LogReceivedEmptyMessage();
                    continue;
                }

                // Deserialize from a copy. The payload is parsed later, off this thread, and
                // the assembly buffer is reused by the next message, so anything pointing into
                // it would be reading the following message by then.
                ReadOnlyMemory<byte> message = bufferStream.ToArray();
                object? incomingMsgObj = await connection
                    .Serializer.DeserializeAsync(message, cancellationToken)
                    .ConfigureAwait(false);
                if (incomingMsgObj is null)
                {
                    _logger.LogDeserializationReturnedNullLength(message.Length);
                    _metrics.MessagesDropped.Add(
                        1,
                        new TagList { { "obsws.drop_reason", "undeserializable" } }
                    );
                    continue;
                }

                ProcessIncomingMessage(connection, incomingMsgObj);
            }

            _logger.LogReceiveLoopExitingCancellationRequested();
        }
        catch (ObsWebSocketMessageTooLargeException ex)
        {
            // Unskippable: framing is past the point where the rest could be discarded safely.
            _logger.LogIncomingMessageExceededLimit(ex.MaxBytes);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogReceiveLoopCancelledGracefullyViaToken();
        }
        catch (WebSocketException ex)
        {
            _logger.LogWebsocketexceptionInReceiveLoopCode(ex, ex.WebSocketErrorCode);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogUnexpectedExceptionInReceiveLoop(ex);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _logger.LogReceiveLoopFinishedForWebsocket(
                RuntimeHelpers.GetHashCode(currentWebSocket)
            );
        }
    }

    /// <summary> Handles server-initiated close frame. </summary>
    private void HandleServerClose(ObsConnectionContext connection)
    {
        IWebSocketConnection ws = connection.Transport;
        bool expectedClosure = _clientLifetimeCts?.IsCancellationRequested ?? false;
        string? desc = ws.CloseStatusDescription;
        WebSocketCloseStatus? status = ws.CloseStatus;

        if (expectedClosure)
        {
            _logger.LogServerAcknowledgedClientClosureStatusDesc(status, desc);
        }
        else
        {
            _logger.LogServerInitiatedUnexpectedCloseStatusDesc(status, desc);
        }

        string closeMessage = $"Connection closed by server. Status: {status}, Description: {desc}";

        // OBS closes with 4009 when the identify payload does not authenticate. That is fatal:
        // retrying the same credentials only repeats it, so it has to be distinguishable from a
        // connection that merely dropped.
        ObsWebSocketException closeEx =
            (int?)status == (int)WebSocketCloseCode.AuthenticationFailed
                ? new AuthenticationFailureException(closeMessage)
                : new ObsWebSocketException(closeMessage);
        CleanupConnectionOnly(closeEx);

        if (ws.State == WebSocketState.CloseReceived)
        {
            _logger.LogAcknowledgingServerCloseFrame();
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
                    _logger.LogServerCloseFrameAcknowledged();
                }
                catch (Exception ex)
                {
                    _logger.LogFailedToAcknowledgeServerCloseFrame(ex);
                }
            });
        }
    }

    /// <summary> Cleans up current connection resources and fails pending tasks. </summary>
    private void CleanupConnectionOnly(Exception reasonException)
    {
        _logger.LogCleanupconnectiononlyDueTo(
            reasonException.GetType().Name,
            reasonException.Message
        );
        IsConnected = false;

        NegotiatedRpcVersion = null;
        CurrentEventSubscriptions = null;

        ObsConnectionContext? connection;
        using (_connectionLock.EnterScope())
        {
            connection = _connection;
            _connection = null;
        }

        CancellationToken tokenForFailure =
            connection?.ConnectionClosed ?? _clientLifetimeCts?.Token ?? CancellationToken.None;

        if (connection is not null)
        {
            _ = connection.Hello.TrySetException(reasonException);
            _ = connection.Identified.TrySetException(reasonException);
        }

        FailPendingRequests(_pendingRequests, reasonException, tokenForFailure);
        FailPendingRequests(_pendingBatchRequests, reasonException, tokenForFailure);

        if (connection is not null)
        {
            _logger.LogDisposingWebsocketInstance(RuntimeHelpers.GetHashCode(connection.Transport));

            // Not DisposeAsync: a server-initiated close runs this on the receive loop itself.
            connection.Close();
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
            _logger.LogFinalizingDisconnectionReason(reasonException?.GetType().Name ?? "Graceful");
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

        _logger.LogClientDefinitivelyDisconnectedReason(eventReason?.Message ?? statusDescription);
        return Task.CompletedTask;
    }

    #endregion

    #region Message Processing
    private static readonly FrozenDictionary<
        string,
        Action<ObsWebSocketClient, IWebSocketMessageSerializer, object?>
    > s_eventHandlers = InitializeEventHandlers().ToFrozenDictionary();

    /// <summary>
    /// Dispatches one message, using the serializer belonging to the connection it arrived on.
    /// </summary>
    /// <remarks>
    /// Threaded through rather than read from a field, so a reconnect onto a different format
    /// cannot decode a message with the wrong serializer.
    /// </remarks>
    private void ProcessIncomingMessage(ObsConnectionContext connection, object messageObject)
    {
        IWebSocketMessageSerializer serializer = connection.Serializer;
        WebSocketOpCode opCode;
        object? payloadData;
        switch (messageObject)
        {
            case IncomingMessage<JsonElement> jsonMsg:
                (opCode, payloadData) = (jsonMsg.Op, jsonMsg.D);
                break;
            case IncomingMessage<ReadOnlyMemory<byte>> msgpackMsg:
                (opCode, payloadData) = (msgpackMsg.Op, msgpackMsg.D);
                break;
            default:
                _logger.LogUnexpectedIncomingMessageTypeEncountered(
                    messageObject.GetType().FullName
                );
                return;
        }

        switch (opCode)
        {
            case WebSocketOpCode.Hello:
                _logger.LogProcessingHelloMessage();
                _ = connection.Hello.TrySetResult(messageObject);
                break;
            case WebSocketOpCode.Identified:
                _logger.LogProcessingIdentifiedMessage();
                _ = connection.Identified.TrySetResult(messageObject);
                break;
            case WebSocketOpCode.Event:
                HandleEventMessage(serializer, payloadData);
                break;
            case WebSocketOpCode.RequestResponse:
                HandleRequestResponseMessage(serializer, payloadData);
                break;
            case WebSocketOpCode.RequestBatchResponse:
                HandleRequestBatchResponseMessage(serializer, payloadData);
                break;
            default:
                _logger.LogReceivedMessageWithUnhandledOpcode(opCode);
                break;
        }
    }

    private void HandleRequestResponseMessage(
        IWebSocketMessageSerializer serializer,
        object? payloadData
    )
    {
        if (payloadData == null)
        {
            _logger.LogReceivedNullPayloadForRequestresponse();
            return;
        }

        try
        {
            // Tolerant on purpose: the requestId lives inside the payload that failed to parse,
            // so there is no pending request to fault. The awaiting caller times out instead,
            // and this log is the only record of why.
            if (
                !serializer.TryDeserializePayload(
                    payloadData,
                    out RequestResponsePayload<object>? response
                ) || response is null
            )
            {
                LogEventDataDeserializationError("RequestResponse wrapper", payloadData);
                return;
            }

            _logger.LogProcessingRequestresponseForRequestidStatus(
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
                _ = tcs.TrySetResult(response);
            }
            else
            {
                _logger.LogReceivedResponseForUnknownOrTimedOut(response.RequestId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogExceptionDuringProcessingOfRequestresponsePayload(
                ex,
                payloadData?.ToString()
            );
        }
    }

    private void HandleRequestBatchResponseMessage(
        IWebSocketMessageSerializer serializer,
        object? payloadData
    )
    {
        if (payloadData == null)
        {
            _logger.LogReceivedNullPayloadForRequestbatchresponse();
            return;
        }

        try
        {
            if (
                !serializer.TryDeserializePayload(
                    payloadData,
                    out RequestBatchResponsePayload<object>? response
                ) || response is null
            )
            {
                LogEventDataDeserializationError("RequestBatchResponse wrapper", payloadData);
                return;
            }

            _logger.LogProcessingRequestbatchresponseForRequestidResults(
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
                _ = tcs.TrySetResult(response);
            }
            else
            {
                _logger.LogReceivedResponseForUnknownOrTimedOut2(response.RequestId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogExceptionDuringProcessingOfRequestbatchresponsePayload(
                ex,
                payloadData?.ToString()
            );
        }
    }

    private void HandleEventMessage(IWebSocketMessageSerializer serializer, object? payloadData)
    {
        if (payloadData == null)
        {
            _logger.LogReceivedNullPayloadForEvent();
            return;
        }

        EventPayloadBase<object>? eventPayloadBase = null;
        try
        {
            // Tolerant on purpose: a newer OBS sending an event this build cannot model must not
            // tear the connection down.
            if (
                !serializer.TryDeserializePayload(payloadData, out eventPayloadBase)
                || eventPayloadBase is null
            )
            {
                LogEventDataDeserializationError("base event structure", payloadData);
                return;
            }

            _logger.LogHandlingIncomingEvent(eventPayloadBase.EventType);
            if (
                s_eventHandlers.TryGetValue(
                    eventPayloadBase.EventType,
                    out Action<
                        ObsWebSocketClient,
                        IWebSocketMessageSerializer,
                        object?
                    >? handlerAction
                )
            )
            {
                try
                {
                    handlerAction(this, serializer, eventPayloadBase.EventData);
                }
                catch (Exception ex)
                {
                    _logger.LogExceptionOccurredWithinTheEventHandlerFor(
                        ex,
                        eventPayloadBase.EventType
                    );
                }
            }
            else
            {
                _logger.LogReceivedEventWithUnhandledType(eventPayloadBase.EventType);
            }
        }
        catch (Exception ex)
        {
            _logger.LogCriticalExceptionDuringEventHandlingFor(
                ex,
                eventPayloadBase?.EventType ?? "Unknown Type",
                payloadData?.ToString()
            );
        }
    }

    /// <summary>
    /// Handles <c>CustomEvent</c>, whose payload does not follow the shape the protocol
    /// definition implies.
    /// </summary>
    /// <remarks>
    /// Every other event's data fields are properties inside the event data object, so the
    /// generated payload record maps them directly. CustomEvent declares a single field named
    /// <c>eventData</c>, but OBS relays the broadcast object as the event data itself rather
    /// than nesting it under that name. Deserializing into the generated record therefore looks
    /// one level too deep and always yields null, so the raw element is taken as the payload.
    /// </remarks>
    /// <param name="serializer">The serializer belonging to the connection this arrived on.</param>
    /// <param name="rawData">The raw event data payload, as produced by the active serializer.</param>
    private void HandleCustomEvent(IWebSocketMessageSerializer serializer, object? rawData)
    {
        try
        {
            if (
                !serializer.TryDeserializeValuePayload(rawData, out JsonElement? broadcastData)
                || broadcastData is null
            )
            {
                LogEventDataDeserializationError("CustomEvent payload", rawData);
                return;
            }

            OnCustomEvent(new CustomEventEventArgs(new CustomEventPayload(broadcastData)));
        }
        catch (Exception ex)
        {
            _logger.LogExceptionWhileTryingToHandleEvent(ex, "CustomEvent");
        }
    }

    private void TryHandleEvent<TPayload, TEventArgs>(
        IWebSocketMessageSerializer serializer,
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
            _ = serializer.TryDeserializePayload(rawData, out TPayload? payload);
            if (payload is not null)
            {
                _metrics.EventsReceived.Add(1, new TagList { { "obsws.event_type", eventType } });
                invoker(argsFactory(payload));
            }
            else
            {
                LogEventDataDeserializationError($"{eventType} payload", rawData);
            }
        }
        catch (Exception ex)
        {
            _logger.LogExceptionWhileTryingToHandleEvent(ex, eventType);
        }
    }

    /// <summary>
    /// The generated dispatch table, with the one event whose payload the protocol does not
    /// describe replaced by its own handler.
    /// </summary>
    private static Dictionary<
        string,
        Action<ObsWebSocketClient, IWebSocketMessageSerializer, object?>
    > InitializeEventHandlers()
    {
        Dictionary<string, Action<ObsWebSocketClient, IWebSocketMessageSerializer, object?>> table =
            CreateEventDispatchTable();
        table["CustomEvent"] = static (c, s, d) => c.HandleCustomEvent(s, d);
        return table;
    }
    #endregion

    #region Helper Methods (Static & Instance)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnsureConnected() => _ = RequireConnection();

    /// <summary>
    /// Returns the live connection, or explains that there is not one.
    /// </summary>
    /// <remarks>
    /// Returned so a caller acts on one connection for the whole operation.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the client is not connected.</exception>
    private ObsConnectionContext RequireConnection()
    {
        ObsConnectionContext? connection = _connection;
        return
            _connectionState != ConnectionState.Connected
            || connection is null
            || connection.Transport.State != WebSocketState.Open
            ? throw new InvalidOperationException(
                $"Client is not connected (State: {_connectionState}, "
                    + $"Socket: {connection?.Transport.State.ToString() ?? "none"})."
            )
            : connection;
    }

    private static TPayload ExtractPayloadFromHandshake<TPayload>(
        ObsConnectionContext connection,
        object messageObject,
        string messageName
    )
        where TPayload : class
    {
        object? rawPayload = messageObject switch
        {
            IncomingMessage<JsonElement> jsonMsg => jsonMsg.D,
            IncomingMessage<ReadOnlyMemory<byte>> msgpackMsg => msgpackMsg.D,
            _ => throw new ObsWebSocketException(
                $"Unexpected message type during {messageName} handshake: {messageObject.GetType().Name}"
            ),
        };
        TPayload? specificPayload = connection.Serializer.DeserializePayload<TPayload>(rawPayload);
        return specificPayload
            ?? throw new ObsWebSocketException($"Received null or invalid {messageName} payload.");
    }

    /// <summary>
    /// Serializes request payload data into a <see cref="JsonElement"/> for embedding in <see cref="RequestPayload"/>.
    /// </summary>
    /// <param name="requestContext">Request context used for exception messages.</param>
    /// <param name="requestData">The request payload object.</param>
    /// <remarks>
    /// In Native AOT, arbitrary objects passed through the batch API path may fail if they are not registered
    /// in <see cref="ObsWebSocket.Core.Serialization.ObsWebSocketJsonContext"/>.
    /// </remarks>
    /// <param name="requestTypeInfo">
    /// Metadata for <paramref name="requestData"/>, for a type this library does not know.
    /// Supplying it from your own <c>JsonSerializerContext</c> keeps the call AOT safe and
    /// avoids hand building a <see cref="System.Text.Json.JsonElement"/>.
    /// </param>
    private static JsonElement? SerializeRequestData(
        string requestContext,
        object? requestData,
        JsonTypeInfo? requestTypeInfo = null
    )
    {
        if (requestData is null)
        {
            return null;
        }

        try
        {
            if (requestData is JsonElement element)
            {
                return element;
            }

            // A caller passing metadata from their own context can send a type this library has
            // never heard of, without hand rolling a JsonDocument, and stays AOT safe doing it.
            return JsonSerializer.SerializeToElement(
                requestData,
                requestTypeInfo ?? s_payloadJsonOptions.GetTypeInfo(requestData.GetType())
            );
        }
        catch (InvalidOperationException ex)
        {
            throw new ObsWebSocketSerializationException(
                $"Failed to serialize request data for {requestContext}. In Native AOT builds, request data must be null, JsonElement, or a generated *RequestData record registered in ObsWebSocketJsonContext.",
                ex
            );
        }
        catch (Exception ex)
        {
            throw new ObsWebSocketSerializationException("Failed to serialize request data.", ex);
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
            throw new ObsWebSocketRequestException(
                $"OBS request '{requestType}' ({requestId}) failed with code {status.Code}: {status.Comment ?? "No comment"}",
                requestType,
                requestId,
                status,
                status.Comment
            );
        }
    }

    /// <summary>Logs a payload that could not be read, keeping only the start of large ones.</summary>
    private void LogEventDataDeserializationError(string description, object? data)
    {
        string raw = data switch
        {
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } =>
                "[Null/Undefined]",
            JsonElement element => element.GetRawText(),
            null => "[Null]",
            _ => data.GetType().Name,
        };

        _logger.LogPayloadUnreadable(
            description,
            data?.GetType().Name ?? "null",
            raw.Length > 512 ? string.Concat(raw.AsSpan(0, 512), "...") : raw
        );
    }

    private static async Task<object> WaitForHandshakeMessageAsync(
        TaskCompletionSource<object>? tcs,
        int timeoutMs,
        string messageName,
        TimeProvider timeProvider,
        CancellationToken attemptCancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(tcs);
        using CancellationTokenSource timeoutCts = new(
            TimeSpan.FromMilliseconds(timeoutMs),
            timeProvider
        );
        using CancellationTokenSource linkedWaitCts =
            CancellationTokenSource.CreateLinkedTokenSource(
                attemptCancellationToken,
                timeoutCts.Token
            );
        try
        {
            return await tcs.Task.WaitAsync(linkedWaitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (tcs.Task.IsFaulted)
        {
            // Same race as a request: the handshake fault, such as a rejected password, arrives
            // after the cancellation of the attempt it ended.
            Exception cause = tcs.Task.Exception!.InnerException!;
            if (cause is AuthenticationFailureException)
            {
                throw cause;
            }

            throw new ConnectionAttemptFailedException(
                $"Failed waiting for {messageName}, possibly due to prior error set on TCS.",
                cause
            );
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            throw new ConnectionAttemptFailedException(
                $"Did not receive {messageName} message within {timeoutMs}ms.",
                ex
            );
        }
        catch (AuthenticationFailureException)
        {
            // Fatal, so it travels as itself rather than as one more failed attempt.
            throw;
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

    /// <summary>
    /// Restores submission order to a batch response.
    /// </summary>
    /// <remarks>
    /// Parallel execution returns results in completion order rather than the order the requests
    /// were sent, so callers could not rely on position to identify a result. Each request is
    /// sent with an id of the form <c>{batchId}_{index}</c> which OBS echoes back, so that index
    /// is used to restore the original order. Results whose id does not carry a usable index are
    /// appended in the order OBS returned them.
    /// </remarks>
    /// <param name="results">The results as returned by OBS.</param>
    /// <param name="requestPayloads">The requests as sent, in submission order.</param>
    private static List<RequestResponsePayload<object>> OrderBatchResults(
        List<RequestResponsePayload<object>> results,
        List<RequestPayload> requestPayloads
    )
    {
        if (results.Count <= 1)
        {
            return results;
        }

        Dictionary<string, int> orderById = new(requestPayloads.Count, StringComparer.Ordinal);
        for (int i = 0; i < requestPayloads.Count; i++)
        {
            orderById[requestPayloads[i].RequestId] = i;
        }

        List<RequestResponsePayload<object>> ordered = new(results.Count);
        List<RequestResponsePayload<object>> unmatched = [];
        RequestResponsePayload<object>?[] slots = new RequestResponsePayload<object>?[
            requestPayloads.Count
        ];

        foreach (RequestResponsePayload<object> result in results)
        {
            if (orderById.TryGetValue(result.RequestId, out int index) && slots[index] is null)
            {
                slots[index] = result;
            }
            else
            {
                unmatched.Add(result);
            }
        }

        foreach (RequestResponsePayload<object>? slot in slots)
        {
            if (slot is not null)
            {
                ordered.Add(slot);
            }
        }

        ordered.AddRange(unmatched);
        return ordered;
    }

    private async Task<object> WaitForResponseAsync(
        TaskCompletionSource<object> tcs,
        int timeoutMs,
        string requestDescription,
        CancellationToken linkedRequestAndLifetimeToken
    )
    {
        using CancellationTokenSource timeoutCts = new(
            TimeSpan.FromMilliseconds(timeoutMs),
            _timeProvider
        );
        using CancellationTokenSource linkedWaitCts =
            CancellationTokenSource.CreateLinkedTokenSource(
                linkedRequestAndLifetimeToken,
                timeoutCts.Token
            );
        try
        {
            return await tcs.Task.WaitAsync(linkedWaitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (tcs.Task.IsFaulted)
        {
            // A lost connection faults the request and then cancels the client lifetime. The
            // fault reaches this wait a thread-pool turn later than the cancellation does, and it
            // is the one that says what happened.
            throw new ObsWebSocketException(
                $"{requestDescription} failed while waiting for response.",
                tcs.Task.Exception!.InnerException
            );
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            throw new ObsWebSocketTimeoutException(
                $"{requestDescription} timed out after {timeoutMs}ms.",
                ex
            );
        }
        catch (OperationCanceledException)
        {
            _logger.LogCanceled(requestDescription);
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
        // Encoded for the socket it goes out on, not the most recently configured format.
        ObsConnectionContext? connection = _connection;
        IWebSocketConnection? currentWebSocket = connection?.Transport;
        if (
            connection is null
            || currentWebSocket is null
            || currentWebSocket.State != WebSocketState.Open
        )
        {
            throw new InvalidOperationException(
                $"Cannot send '{opCode}', WebSocket not open or available (State: {currentWebSocket?.State})."
            );
        }

        ArgumentNullException.ThrowIfNull(payload);

        OutgoingMessage<T> message = new(opCode, payload);
        _logger.LogSendingMessage(opCode);

        byte[] messageBytes;
        try
        {
            messageBytes = await connection
                .Serializer.SerializeAsync(message, linkedToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogSerializationFailedForMessage(ex, opCode);
            throw new ObsWebSocketException($"Serialization failed for {opCode}.", ex);
        }

        WebSocketMessageType messageType = connection.Serializer.ProtocolSubProtocol.Contains(
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
            _logger.LogMessageSent(opCode);
        }
        catch (OperationCanceledException)
        {
            _logger.LogSendOperationForCanceled(opCode);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogFailedToSendMessageViaWebsocket(ex, opCode);
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
        _logger.LogFailingPendingRequestSDueTo(requestsToFail.Length, ex.GetType().Name);
        bool isCancellation = ex is OperationCanceledException;
        foreach ((_, TaskCompletionSource<object> tcs) in requestsToFail)
        {
            CancellationToken tokenToUse = isCancellation ? ct : CancellationToken.None;
            _ = isCancellation ? tcs.TrySetCanceled(tokenToUse) : tcs.TrySetException(ex);
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
            _logger.LogExceptionInUserProvidedConnectingEventHandler(ex);
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
            _logger.LogExceptionInUserProvidedConnectedEventHandler(ex);
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
            _logger.LogExceptionInUserProvidedDisconnectedEventHandler(ex);
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
            _logger.LogExceptionInUserProvidedConnectionfailedEventHandler(ex);
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
            _logger.LogExceptionInUserProvidedAuthenticationfailureEventHandler(ex);
        }
    }
    #endregion

    #region Finalizer

    /// <summary>
    /// Finalizer for the ObsWebSocketClient class.
    /// </summary>
    ~ObsWebSocketClient()
    {
        if (
            _connectionState != ConnectionState.Disconnected
            || _connection != null
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
                _connection?.Close();
            }
            catch { }
        }
    }
    #endregion

    // ConnectionAttemptFailedException and AuthenticationFailureException were promoted to
    // public top-level types in v0.3.1-dev3; their definitions now live in their own files.
}
