#nullable enable

using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using ObsWebSocket.Core.Protocol.Generated;

namespace ObsWebSocket.Core;

/// <summary>Source-generated log messages.</summary>
internal static partial class ObsWebSocketClientLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Starting connection sequence for {Uri}..."
    )]
    public static partial void LogStartingConnectionSequenceFor(this ILogger logger, Uri? uri);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "ConnectAsync initial connection confirmed successfully."
    )]
    public static partial void LogConnectasyncInitialConnectionConfirmedSuccessfully(
        this ILogger logger
    );

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Error,
        Message = "ConnectAsync failed to establish initial connection."
    )]
    public static partial void LogConnectasyncFailedToEstablishInitialConnection(
        this ILogger logger,
        Exception exception
    );

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Debug,
        Message = "Waiting for Identified after Reidentify (Timeout: {TimeoutMs}ms)..."
    )]
    public static partial void LogWaitingForIdentifiedAfterReidentifyTimeoutMs(
        this ILogger logger,
        int timeoutMs
    );

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Information,
        Message = "Re-identification successful. RPC Version: {RpcVersion}"
    )]
    public static partial void LogReIdentificationSuccessfulRpcVersion(
        this ILogger logger,
        int rpcVersion
    );

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "ReidentifyAsync failed.")]
    public static partial void LogReidentifyasyncFailed(this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Warning,
        Message = "ReidentifyAsync canceled due to client shutdown."
    )]
    public static partial void LogReidentifyasyncCanceledDueToClientShutdown(this ILogger logger);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Debug,
        Message = "Waiting for Response {RequestId} ({RequestType}, Timeout: {TimeoutMs}ms)..."
    )]
    public static partial void LogWaitingForResponseTimeoutMs(
        this ILogger logger,
        string requestId,
        string requestType,
        int timeoutMs
    );

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Error,
        Message = "CallAsync failed for {RequestType} ({RequestId})"
    )]
    public static partial void LogCallasyncFailedFor(
        this ILogger logger,
        Exception exception,
        string requestType,
        string requestId
    );

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Warning,
        Message = "CallAsync for {RequestType} ({RequestId}) canceled."
    )]
    public static partial void LogCallasyncForCanceled(
        this ILogger logger,
        string requestType,
        string requestId
    );

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Error,
        Message = "CallAsyncValue failed for {RequestType} ({RequestId})"
    )]
    public static partial void LogCallasyncvalueFailedFor(
        this ILogger logger,
        Exception exception,
        string requestType,
        string requestId
    );

    [LoggerMessage(
        EventId = 12,
        Level = LogLevel.Warning,
        Message = "CallAsyncValue for {RequestType} ({RequestId}) canceled."
    )]
    public static partial void LogCallasyncvalueForCanceled(
        this ILogger logger,
        string requestType,
        string requestId
    );

    [LoggerMessage(EventId = 13, Level = LogLevel.Warning, Message = "Empty batch request.")]
    public static partial void LogEmptyBatchRequest(this ILogger logger);

    [LoggerMessage(
        EventId = 14,
        Level = LogLevel.Debug,
        Message = "Waiting for Batch Response {BatchRequestId} ({RequestCount}, Timeout: {TimeoutMs}ms)..."
    )]
    public static partial void LogWaitingForBatchResponseTimeoutMs(
        this ILogger logger,
        string batchRequestId,
        int requestCount,
        int timeoutMs
    );

    [LoggerMessage(
        EventId = 15,
        Level = LogLevel.Debug,
        Message = "Received Batch Response {BatchRequestId} ({ResultCount} results)."
    )]
    public static partial void LogReceivedBatchResponseResults(
        this ILogger logger,
        string batchRequestId,
        int resultCount
    );

    [LoggerMessage(
        EventId = 16,
        Level = LogLevel.Error,
        Message = "CallBatchAsync failed for batch {BatchRequestId}"
    )]
    public static partial void LogCallbatchasyncFailedForBatch(
        this ILogger logger,
        Exception exception,
        string batchRequestId
    );

    [LoggerMessage(
        EventId = 17,
        Level = LogLevel.Warning,
        Message = "CallBatchAsync for {BatchRequestId} canceled."
    )]
    public static partial void LogCallbatchasyncForCanceled(
        this ILogger logger,
        string batchRequestId
    );

    [LoggerMessage(
        EventId = 18,
        Level = LogLevel.Debug,
        Message = "DisconnectAsync ignored, already {ConnectionState}."
    )]
    public static partial void LogDisconnectasyncIgnoredAlready(
        this ILogger logger,
        ConnectionState connectionState
    );

    [LoggerMessage(
        EventId = 19,
        Level = LogLevel.Information,
        Message = "DisconnectAsync initiating graceful shutdown..."
    )]
    public static partial void LogDisconnectasyncInitiatingGracefulShutdown(this ILogger logger);

    [LoggerMessage(
        EventId = 20,
        Level = LogLevel.Debug,
        Message = "Waiting for connection loop task to complete during disconnect..."
    )]
    public static partial void LogWaitingForConnectionLoopTaskToComplete(this ILogger logger);

    [LoggerMessage(
        EventId = 21,
        Level = LogLevel.Debug,
        Message = "Connection loop task completed or wait timed out/canceled during DisconnectAsync."
    )]
    public static partial void LogConnectionLoopTaskCompletedOrWaitTimed(this ILogger logger);

    [LoggerMessage(
        EventId = 22,
        Level = LogLevel.Warning,
        Message = "Wait for connection loop task timed out or canceled during DisconnectAsync."
    )]
    public static partial void LogWaitForConnectionLoopTaskTimedOut(this ILogger logger);

    [LoggerMessage(
        EventId = 23,
        Level = LogLevel.Error,
        Message = "Exception from connection loop task during DisconnectAsync."
    )]
    public static partial void LogExceptionFromConnectionLoopTaskDuringDisconnectasync(
        this ILogger logger,
        Exception exception
    );

    [LoggerMessage(EventId = 24, Level = LogLevel.Debug, Message = "DisposeAsync called.")]
    public static partial void LogDisposeasyncCalled(this ILogger logger);

    [LoggerMessage(EventId = 25, Level = LogLevel.Debug, Message = "DisposeAsync completed.")]
    public static partial void LogDisposeasyncCompleted(this ILogger logger);

    [LoggerMessage(
        EventId = 26,
        Level = LogLevel.Warning,
        Message = "Max reconnect attempts ({MaxAttempts}) reached or auto-reconnect disabled. Stopping."
    )]
    public static partial void LogMaxReconnectAttemptsReachedOrAutoReconnect(
        this ILogger logger,
        int maxAttempts
    );

    [LoggerMessage(
        EventId = 27,
        Level = LogLevel.Information,
        Message = "Reconnecting attempt {AttemptNumber}/{MaxAttempts} after {DelayMs}ms..."
    )]
    public static partial void LogReconnectingAttemptAfterMs(
        this ILogger logger,
        int attemptNumber,
        string maxAttempts,
        int delayMs
    );

    [LoggerMessage(
        EventId = 28,
        Level = LogLevel.Information,
        Message = "Connected (Attempt {AttemptNumber}), but disconnect requested. Aborting."
    )]
    public static partial void LogConnectedAttemptButDisconnectRequestedAborting(
        this ILogger logger,
        int attemptNumber
    );

    [LoggerMessage(
        EventId = 29,
        Level = LogLevel.Debug,
        Message = "[Attempt:{AttemptNumber}] Handshake complete, receive loop is running."
    )]
    public static partial void LogAttemptHandshakeCompleteReceiveLoopIsRunning(
        this ILogger logger,
        int attemptNumber
    );

    [LoggerMessage(
        EventId = 30,
        Level = LogLevel.Information,
        Message = "Successfully connected and identified (Attempt {AttemptNumber})."
    )]
    public static partial void LogSuccessfullyConnectedAndIdentifiedAttempt(
        this ILogger logger,
        int attemptNumber
    );

    [LoggerMessage(
        EventId = 31,
        Level = LogLevel.Debug,
        Message = "Connection established. Waiting for receive loop completion or cancellation..."
    )]
    public static partial void LogConnectionEstablishedWaitingForReceiveLoopCompletion(
        this ILogger logger
    );

    [LoggerMessage(
        EventId = 32,
        Level = LogLevel.Debug,
        Message = "Receive loop task completed while connected."
    )]
    public static partial void LogReceiveLoopTaskCompletedWhileConnected(this ILogger logger);

    [LoggerMessage(
        EventId = 33,
        Level = LogLevel.Information,
        Message = "Connection loop canceled (Attempt {AttemptNumber})."
    )]
    public static partial void LogConnectionLoopCanceledAttempt(
        this ILogger logger,
        int attemptNumber
    );

    [LoggerMessage(
        EventId = 34,
        Level = LogLevel.Error,
        Message = "Authentication failed (Attempt {AttemptNumber}). Stopping."
    )]
    public static partial void LogAuthenticationFailedAttemptStopping(
        this ILogger logger,
        Exception exception,
        int attemptNumber
    );

    [LoggerMessage(
        EventId = 35,
        Level = LogLevel.Warning,
        Message = "Connect attempt {AttemptNumber} failed. Retrying..."
    )]
    public static partial void LogConnectAttemptFailedRetrying(
        this ILogger logger,
        Exception exception,
        int attemptNumber
    );

    [LoggerMessage(
        EventId = 36,
        Level = LogLevel.Warning,
        Message = "WebSocketException during connection/receive (Attempt {AttemptNumber}). Retrying..."
    )]
    public static partial void LogWebsocketexceptionDuringConnectionReceiveAttemptRetrying(
        this ILogger logger,
        Exception exception,
        int attemptNumber
    );

    [LoggerMessage(
        EventId = 37,
        Level = LogLevel.Error,
        Message = "Unexpected error in connection loop (Attempt {AttemptNumber}). Retrying..."
    )]
    public static partial void LogUnexpectedErrorInConnectionLoopAttemptRetrying(
        this ILogger logger,
        Exception exception,
        int attemptNumber
    );

    [LoggerMessage(
        EventId = 38,
        Level = LogLevel.Warning,
        Message = "Connection lost during connected state due to: {ErrorType}"
    )]
    public static partial void LogConnectionLostDuringConnectedStateDueTo(
        this ILogger logger,
        string errorType
    );

    [LoggerMessage(
        EventId = 39,
        Level = LogLevel.Critical,
        Message = "Catastrophic error in ConnectionLoopAsync."
    )]
    public static partial void LogCatastrophicErrorInConnectionloopasync(
        this ILogger logger,
        Exception exception
    );

    [LoggerMessage(
        EventId = 40,
        Level = LogLevel.Information,
        Message = "Exited connection loop. Finalizing state."
    )]
    public static partial void LogExitedConnectionLoopFinalizingState(this ILogger logger);

    [LoggerMessage(
        EventId = 41,
        Level = LogLevel.Warning,
        Message = "Attempted duplicate subprotocol add: {SubProtocol}"
    )]
    public static partial void LogAttemptedDuplicateSubprotocolAdd(
        this ILogger logger,
        Exception exception,
        string subProtocol
    );

    [LoggerMessage(
        EventId = 42,
        Level = LogLevel.Debug,
        Message = "[Attempt:{AttemptNumber}] Connecting..."
    )]
    public static partial void LogAttemptConnecting(this ILogger logger, int attemptNumber);

    [LoggerMessage(
        EventId = 43,
        Level = LogLevel.Information,
        Message = "[Attempt:{AttemptNumber}] WebSocket connection established. Protocol: {SubProtocol}"
    )]
    public static partial void LogAttemptWebsocketConnectionEstablishedProtocol(
        this ILogger logger,
        int attemptNumber,
        string subProtocol
    );

    [LoggerMessage(
        EventId = 44,
        Level = LogLevel.Debug,
        Message = "[Attempt:{AttemptNumber}] Receive loop started for handshake."
    )]
    public static partial void LogAttemptReceiveLoopStartedForHandshake(
        this ILogger logger,
        int attemptNumber
    );

    [LoggerMessage(
        EventId = 45,
        Level = LogLevel.Debug,
        Message = "[Attempt:{AttemptNumber}] Waiting for Hello..."
    )]
    public static partial void LogAttemptWaitingForHello(this ILogger logger, int attemptNumber);

    [LoggerMessage(
        EventId = 46,
        Level = LogLevel.Debug,
        Message = "[Attempt:{AttemptNumber}] Received Hello. RPC Version: {RpcVersion}"
    )]
    public static partial void LogAttemptReceivedHelloRpcVersion(
        this ILogger logger,
        int attemptNumber,
        int rpcVersion
    );

    [LoggerMessage(
        EventId = 47,
        Level = LogLevel.Debug,
        Message = "[Attempt:{AttemptNumber}] Authentication required."
    )]
    public static partial void LogAttemptAuthenticationRequired(
        this ILogger logger,
        int attemptNumber
    );

    [LoggerMessage(
        EventId = 48,
        Level = LogLevel.Debug,
        Message = "[Attempt:{AttemptNumber}] Waiting for Identified..."
    )]
    public static partial void LogAttemptWaitingForIdentified(
        this ILogger logger,
        int attemptNumber
    );

    [LoggerMessage(
        EventId = 49,
        Level = LogLevel.Debug,
        Message = "[Attempt:{AttemptNumber}] Received Identified. Negotiated RPC Version: {NegotiatedRpcVersion}"
    )]
    public static partial void LogAttemptReceivedIdentifiedNegotiatedRpcVersion(
        this ILogger logger,
        int attemptNumber,
        int negotiatedRpcVersion
    );

    [LoggerMessage(
        EventId = 50,
        Level = LogLevel.Debug,
        Message = "Receive loop starting for WebSocket {HashCode}."
    )]
    public static partial void LogReceiveLoopStartingForWebsocket(
        this ILogger logger,
        int hashCode
    );

    [LoggerMessage(
        EventId = 51,
        Level = LogLevel.Warning,
        Message = "WebSocket state changed to {WebSocketState} during receive loop."
    )]
    public static partial void LogWebsocketStateChangedToDuringReceiveLoop(
        this ILogger logger,
        WebSocketState webSocketState
    );

    [LoggerMessage(EventId = 52, Level = LogLevel.Trace, Message = "Received empty message.")]
    public static partial void LogReceivedEmptyMessage(this ILogger logger);

    [LoggerMessage(
        EventId = 100,
        Level = LogLevel.Error,
        Message = "An incoming message exceeded the {MaxBytes} byte limit; closing the connection."
    )]
    public static partial void LogIncomingMessageExceededLimit(this ILogger logger, int maxBytes);

    [LoggerMessage(
        EventId = 53,
        Level = LogLevel.Warning,
        Message = "Deserialization returned null (Length: {BufferLength})."
    )]
    public static partial void LogDeserializationReturnedNullLength(
        this ILogger logger,
        long bufferLength
    );

    [LoggerMessage(
        EventId = 54,
        Level = LogLevel.Information,
        Message = "Receive loop exiting: cancellation requested."
    )]
    public static partial void LogReceiveLoopExitingCancellationRequested(this ILogger logger);

    [LoggerMessage(
        EventId = 55,
        Level = LogLevel.Information,
        Message = "Receive loop cancelled gracefully via token."
    )]
    public static partial void LogReceiveLoopCancelledGracefullyViaToken(this ILogger logger);

    [LoggerMessage(
        EventId = 56,
        Level = LogLevel.Warning,
        Message = "WebSocketException in receive loop (Code: {WebSocketErrorCode})."
    )]
    public static partial void LogWebsocketexceptionInReceiveLoopCode(
        this ILogger logger,
        Exception exception,
        WebSocketError webSocketErrorCode
    );

    [LoggerMessage(
        EventId = 57,
        Level = LogLevel.Error,
        Message = "Unexpected exception in receive loop."
    )]
    public static partial void LogUnexpectedExceptionInReceiveLoop(
        this ILogger logger,
        Exception exception
    );

    [LoggerMessage(
        EventId = 58,
        Level = LogLevel.Debug,
        Message = "Receive loop finished for WebSocket {HashCode}."
    )]
    public static partial void LogReceiveLoopFinishedForWebsocket(
        this ILogger logger,
        int hashCode
    );

    [LoggerMessage(
        EventId = 59,
        Level = LogLevel.Information,
        Message = "Server acknowledged client closure. Status: {WebSocketCloseStatus}, Desc: {Description}"
    )]
    public static partial void LogServerAcknowledgedClientClosureStatusDesc(
        this ILogger logger,
        WebSocketCloseStatus? webSocketCloseStatus,
        string? description
    );

    [LoggerMessage(
        EventId = 60,
        Level = LogLevel.Warning,
        Message = "Server initiated unexpected close. Status: {WebSocketCloseStatus}, Desc: {Description}"
    )]
    public static partial void LogServerInitiatedUnexpectedCloseStatusDesc(
        this ILogger logger,
        WebSocketCloseStatus? webSocketCloseStatus,
        string? description
    );

    [LoggerMessage(
        EventId = 61,
        Level = LogLevel.Debug,
        Message = "Acknowledging server close frame..."
    )]
    public static partial void LogAcknowledgingServerCloseFrame(this ILogger logger);

    [LoggerMessage(
        EventId = 62,
        Level = LogLevel.Debug,
        Message = "Server close frame acknowledged."
    )]
    public static partial void LogServerCloseFrameAcknowledged(this ILogger logger);

    [LoggerMessage(
        EventId = 63,
        Level = LogLevel.Warning,
        Message = "Failed to acknowledge server close frame."
    )]
    public static partial void LogFailedToAcknowledgeServerCloseFrame(
        this ILogger logger,
        Exception exception
    );

    [LoggerMessage(
        EventId = 64,
        Level = LogLevel.Trace,
        Message = "CleanupConnectionOnly due to: {ExceptionType} - {ExceptionMessage}"
    )]
    public static partial void LogCleanupconnectiononlyDueTo(
        this ILogger logger,
        string exceptionType,
        string exceptionMessage
    );

    [LoggerMessage(
        EventId = 65,
        Level = LogLevel.Warning,
        Message = "Exception cancelling receive CTS during cleanup."
    )]
    public static partial void LogExceptionCancellingReceiveCtsDuringCleanup(
        this ILogger logger,
        Exception exception
    );

    [LoggerMessage(
        EventId = 66,
        Level = LogLevel.Warning,
        Message = "Exception disposing receive CTS during cleanup."
    )]
    public static partial void LogExceptionDisposingReceiveCtsDuringCleanup(
        this ILogger logger,
        Exception exception
    );

    [LoggerMessage(
        EventId = 67,
        Level = LogLevel.Debug,
        Message = "Disposing WebSocket instance {HashCode}"
    )]
    public static partial void LogDisposingWebsocketInstance(this ILogger logger, int hashCode);

    [LoggerMessage(
        EventId = 68,
        Level = LogLevel.Debug,
        Message = "Finalizing disconnection... Reason: {ReasonType}"
    )]
    public static partial void LogFinalizingDisconnectionReason(
        this ILogger logger,
        string reasonType
    );

    [LoggerMessage(
        EventId = 69,
        Level = LogLevel.Information,
        Message = "Client definitively disconnected. Reason: {DisconnectionReason}"
    )]
    public static partial void LogClientDefinitivelyDisconnectedReason(
        this ILogger logger,
        string disconnectionReason
    );

    [LoggerMessage(
        EventId = 70,
        Level = LogLevel.Error,
        Message = "Unexpected incoming message type encountered: {MessageType}"
    )]
    public static partial void LogUnexpectedIncomingMessageTypeEncountered(
        this ILogger logger,
        string? messageType
    );

    [LoggerMessage(EventId = 71, Level = LogLevel.Trace, Message = "Processing Hello message.")]
    public static partial void LogProcessingHelloMessage(this ILogger logger);

    [LoggerMessage(
        EventId = 72,
        Level = LogLevel.Trace,
        Message = "Processing Identified message."
    )]
    public static partial void LogProcessingIdentifiedMessage(this ILogger logger);

    [LoggerMessage(
        EventId = 73,
        Level = LogLevel.Warning,
        Message = "Received message with unhandled OpCode: {OpCode}"
    )]
    public static partial void LogReceivedMessageWithUnhandledOpcode(
        this ILogger logger,
        WebSocketOpCode opCode
    );

    [LoggerMessage(
        EventId = 74,
        Level = LogLevel.Warning,
        Message = "Received null payload for RequestResponse."
    )]
    public static partial void LogReceivedNullPayloadForRequestresponse(this ILogger logger);

    [LoggerMessage(
        EventId = 75,
        Level = LogLevel.Trace,
        Message = "Processing RequestResponse for RequestId: {RequestId}, Status: {StatusResult}"
    )]
    public static partial void LogProcessingRequestresponseForRequestidStatus(
        this ILogger logger,
        string requestId,
        bool statusResult
    );

    [LoggerMessage(
        EventId = 76,
        Level = LogLevel.Warning,
        Message = "Received response for unknown or timed out RequestId: {RequestId}"
    )]
    public static partial void LogReceivedResponseForUnknownOrTimedOut(
        this ILogger logger,
        string requestId
    );

    [LoggerMessage(
        EventId = 77,
        Level = LogLevel.Error,
        Message = "Exception during processing of RequestResponse payload: {Payload}"
    )]
    public static partial void LogExceptionDuringProcessingOfRequestresponsePayload(
        this ILogger logger,
        Exception exception,
        string? payload
    );

    [LoggerMessage(
        EventId = 78,
        Level = LogLevel.Warning,
        Message = "Received null payload for RequestBatchResponse."
    )]
    public static partial void LogReceivedNullPayloadForRequestbatchresponse(this ILogger logger);

    [LoggerMessage(
        EventId = 79,
        Level = LogLevel.Trace,
        Message = "Processing RequestBatchResponse for RequestId: {RequestId} ({ResultCount} results)"
    )]
    public static partial void LogProcessingRequestbatchresponseForRequestidResults(
        this ILogger logger,
        string requestId,
        int resultCount
    );

    [LoggerMessage(
        EventId = 80,
        Level = LogLevel.Warning,
        Message = "Received response for unknown or timed out BatchRequestId: {RequestId}"
    )]
    public static partial void LogReceivedResponseForUnknownOrTimedOut2(
        this ILogger logger,
        string requestId
    );

    [LoggerMessage(
        EventId = 81,
        Level = LogLevel.Error,
        Message = "Exception during processing of RequestBatchResponse payload: {Payload}"
    )]
    public static partial void LogExceptionDuringProcessingOfRequestbatchresponsePayload(
        this ILogger logger,
        Exception exception,
        string? payload
    );

    [LoggerMessage(
        EventId = 82,
        Level = LogLevel.Warning,
        Message = "Received null payload for Event."
    )]
    public static partial void LogReceivedNullPayloadForEvent(this ILogger logger);

    [LoggerMessage(
        EventId = 83,
        Level = LogLevel.Trace,
        Message = "Handling incoming event: {EventType}"
    )]
    public static partial void LogHandlingIncomingEvent(this ILogger logger, string eventType);

    [LoggerMessage(
        EventId = 84,
        Level = LogLevel.Error,
        Message = "Exception occurred within the event handler for {EventType}."
    )]
    public static partial void LogExceptionOccurredWithinTheEventHandlerFor(
        this ILogger logger,
        Exception exception,
        string eventType
    );

    [LoggerMessage(
        EventId = 85,
        Level = LogLevel.Warning,
        Message = "Received event with unhandled type: {EventType}"
    )]
    public static partial void LogReceivedEventWithUnhandledType(
        this ILogger logger,
        string eventType
    );

    [LoggerMessage(
        EventId = 86,
        Level = LogLevel.Error,
        Message = "Critical exception during event handling for '{EventType}': {Payload}"
    )]
    public static partial void LogCriticalExceptionDuringEventHandlingFor(
        this ILogger logger,
        Exception exception,
        string eventType,
        string? payload
    );

    [LoggerMessage(
        EventId = 87,
        Level = LogLevel.Error,
        Message = "Exception while trying to handle event {EventType}."
    )]
    public static partial void LogExceptionWhileTryingToHandleEvent(
        this ILogger logger,
        Exception exception,
        string eventType
    );

    [LoggerMessage(
        EventId = 88,
        Level = LogLevel.Warning,
        Message = "{RequestDescription} canceled."
    )]
    public static partial void LogCanceled(this ILogger logger, string requestDescription);

    [LoggerMessage(EventId = 89, Level = LogLevel.Trace, Message = "Sending {OpCode} message...")]
    public static partial void LogSendingMessage(this ILogger logger, WebSocketOpCode opCode);

    [LoggerMessage(
        EventId = 90,
        Level = LogLevel.Error,
        Message = "Serialization failed for {OpCode} message."
    )]
    public static partial void LogSerializationFailedForMessage(
        this ILogger logger,
        Exception exception,
        WebSocketOpCode opCode
    );

    [LoggerMessage(EventId = 91, Level = LogLevel.Trace, Message = "{OpCode} message sent.")]
    public static partial void LogMessageSent(this ILogger logger, WebSocketOpCode opCode);

    [LoggerMessage(
        EventId = 92,
        Level = LogLevel.Warning,
        Message = "Send operation for {OpCode} canceled."
    )]
    public static partial void LogSendOperationForCanceled(
        this ILogger logger,
        WebSocketOpCode opCode
    );

    [LoggerMessage(
        EventId = 93,
        Level = LogLevel.Error,
        Message = "Failed to send {OpCode} message via WebSocket."
    )]
    public static partial void LogFailedToSendMessageViaWebsocket(
        this ILogger logger,
        Exception exception,
        WebSocketOpCode opCode
    );

    [LoggerMessage(
        EventId = 94,
        Level = LogLevel.Debug,
        Message = "Failing {RequestCount} pending request(s) due to: {ExceptionType}"
    )]
    public static partial void LogFailingPendingRequestSDueTo(
        this ILogger logger,
        int requestCount,
        string exceptionType
    );

    [LoggerMessage(
        EventId = 95,
        Level = LogLevel.Error,
        Message = "Exception in user-provided Connecting event handler."
    )]
    public static partial void LogExceptionInUserProvidedConnectingEventHandler(
        this ILogger logger,
        Exception exception
    );

    [LoggerMessage(
        EventId = 96,
        Level = LogLevel.Error,
        Message = "Exception in user-provided Connected event handler."
    )]
    public static partial void LogExceptionInUserProvidedConnectedEventHandler(
        this ILogger logger,
        Exception exception
    );

    [LoggerMessage(
        EventId = 97,
        Level = LogLevel.Error,
        Message = "Exception in user-provided Disconnected event handler."
    )]
    public static partial void LogExceptionInUserProvidedDisconnectedEventHandler(
        this ILogger logger,
        Exception exception
    );

    [LoggerMessage(
        EventId = 98,
        Level = LogLevel.Error,
        Message = "Exception in user-provided ConnectionFailed event handler."
    )]
    public static partial void LogExceptionInUserProvidedConnectionfailedEventHandler(
        this ILogger logger,
        Exception exception
    );

    [LoggerMessage(
        EventId = 99,
        Level = LogLevel.Error,
        Message = "Exception in user-provided AuthenticationFailure event handler."
    )]
    public static partial void LogExceptionInUserProvidedAuthenticationfailureEventHandler(
        this ILogger logger,
        Exception exception
    );

    [LoggerMessage(
        EventId = 101,
        Level = LogLevel.Warning,
        Message = "Could not switch to scene collection '{TargetName}': OBS does not know it."
    )]
    public static partial void LogSceneCollectionNotFound(this ILogger logger, string targetName);

    [LoggerMessage(
        EventId = 102,
        Level = LogLevel.Warning,
        Message = "Could not switch to profile '{TargetName}': OBS does not know it."
    )]
    public static partial void LogProfileNotFound(this ILogger logger, string targetName);

    [LoggerMessage(
        EventId = 103,
        Level = LogLevel.Error,
        Message = "Could not read the settings of filter '{FilterName}' on '{SourceName}' as {TypeName}."
    )]
    public static partial void LogFilterSettingsUnreadable(
        this ILogger logger,
        Exception exception,
        string filterName,
        string sourceName,
        string typeName
    );

    [LoggerMessage(
        EventId = 104,
        Level = LogLevel.Debug,
        Message = "SetInputMutesAsync was given no inputs, so nothing was sent."
    )]
    public static partial void LogNoInputMutesToSet(this ILogger logger);

    [LoggerMessage(
        EventId = 105,
        Level = LogLevel.Warning,
        Message = "OBS rejected the mute state for input '{InputName}': code {Code}, {Comment}"
    )]
    public static partial void LogInputMuteRejected(
        this ILogger logger,
        string inputName,
        int code,
        string comment
    );

    [LoggerMessage(
        EventId = 106,
        Level = LogLevel.Error,
        Message = "Could not read the settings of input '{InputName}' as {TypeName}."
    )]
    public static partial void LogInputSettingsUnreadable(
        this ILogger logger,
        Exception exception,
        string inputName,
        string typeName
    );

    [LoggerMessage(
        EventId = 107,
        Level = LogLevel.Debug,
        Message = "Switch to '{SceneName}' sent, waiting for {EventDescription}."
    )]
    public static partial void LogSceneSwitchSent(
        this ILogger logger,
        string sceneName,
        string eventDescription
    );

    [LoggerMessage(
        EventId = 108,
        Level = LogLevel.Information,
        Message = "Switch confirmed by {EventDescription}."
    )]
    public static partial void LogSceneSwitchConfirmed(
        this ILogger logger,
        string eventDescription
    );

    [LoggerMessage(
        EventId = 109,
        Level = LogLevel.Information,
        Message = "Waiting for the switch to '{SceneName}' was cancelled."
    )]
    public static partial void LogSceneSwitchCancelled(this ILogger logger, string sceneName);

    [LoggerMessage(
        EventId = 110,
        Level = LogLevel.Error,
        Message = "Switching to '{SceneName}' and waiting for confirmation failed."
    )]
    public static partial void LogSceneSwitchFailed(
        this ILogger logger,
        Exception exception,
        string sceneName
    );

    [LoggerMessage(
        EventId = 111,
        Level = LogLevel.Warning,
        Message = "OBS refused the lookup for source '{SourceName}', so it is reported as absent."
    )]
    public static partial void LogSourceLookupRefused(
        this ILogger logger,
        Exception exception,
        string sourceName
    );

    [LoggerMessage(
        EventId = 112,
        Level = LogLevel.Warning,
        Message = "No source named '{SourceName}' to take a screenshot of."
    )]
    public static partial void LogScreenshotSourceNotFound(this ILogger logger, string sourceName);

    [LoggerMessage(
        EventId = 113,
        Level = LogLevel.Warning,
        Message = "OBS returned no image data for the screenshot of '{SourceName}'."
    )]
    public static partial void LogScreenshotEmpty(this ILogger logger, string sourceName);

    [LoggerMessage(
        EventId = 114,
        Level = LogLevel.Error,
        Message = "The screenshot of '{SourceName}' was not valid Base64."
    )]
    public static partial void LogScreenshotUndecodable(
        this ILogger logger,
        Exception exception,
        string sourceName
    );

    [LoggerMessage(
        EventId = 115,
        Level = LogLevel.Warning,
        Message = "Could not reach OBS during startup. The client will keep trying."
    )]
    public static partial void LogObsUnreachableAtStartup(this ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 116,
        Level = LogLevel.Information,
        Message = "OBS connection settings changed, reconnecting to {ServerUri}."
    )]
    public static partial void LogConnectionSettingsChanged(this ILogger logger, Uri serverUri);

    [LoggerMessage(
        EventId = 117,
        Level = LogLevel.Warning,
        Message = "Reconnect after a settings change did not succeed."
    )]
    public static partial void LogReconnectAfterSettingsChangeFailed(
        this ILogger logger,
        Exception exception
    );

    [LoggerMessage(
        EventId = 118,
        Level = LogLevel.Warning,
        Message = "Could not read {Description}. Type: {DataType}, Raw: {RawData}"
    )]
    public static partial void LogPayloadUnreadable(
        this ILogger logger,
        string description,
        string dataType,
        string rawData
    );
}
