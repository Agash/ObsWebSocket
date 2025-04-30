using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Moq;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;
using ObsWebSocket.Core.Serialization;
using RequestStatus = ObsWebSocket.Core.Protocol.RequestStatus;

namespace ObsWebSocket.Tests;

/// <summary>
/// Unit tests focusing on the request/response logic of <see cref="ObsWebSocketClient"/>.
/// </summary>
[TestClass]
public class ObsWebSocketClientRequestTests
{
    private const int TestTimeout = 5000; // ms
    private const int ShortDelay = 50; // ms, used only for timeout test cleanup verification

    // --- Test Request WITH Response Data (Class) ---

    /// <summary>
    /// Verifies that GetVersionAsync sends the correct request structure and
    /// correctly deserializes a successful response payload into the s_expectedFailNoRetryLog DTO.
    /// </summary>
    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetVersionAsync_SendsCorrectRequest_ReturnsDeserializedResponse()
    {
        // Arrange
        (
            ObsWebSocketClient? client,
            Mock<IWebSocketMessageSerializer>? mockSerializer,
            Mock<IWebSocketConnection>? mockWebSocket
        ) = TestUtils.SetupConnectedClientForceState(); // Sets up a client in a connected state

        GetVersionResponseData expectedResponsePayload = new(
            obsVersion: "obs-test-version",
            obsWebSocketVersion: "ws-test-version",
            rpcVersion: 1,
            availableRequests: [],
            supportedImageFormats: [],
            platform: "test-platform",
            platformDescription: "test-desc"
        );
        // Prepare the raw response data (as it would be received in the payload object)
        // Using ToJsonElement ensures it's handled correctly by the serializer mock.
        JsonElement? rawResponseData = TestUtils.ToJsonElement(expectedResponsePayload)!;
        string? capturedRequestId = null;

        // Mock the SendAsync call to capture the request ID and simulate a successful response
        mockWebSocket
            .Setup(ws =>
                ws.SendAsync(
                    It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<WebSocketMessageType>(),
                    true,
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(
                (
                    ReadOnlyMemory<byte> buffer,
                    WebSocketMessageType msgType,
                    bool endOfMsg,
                    CancellationToken ct
                ) =>
                {
                    // Deserialize the sent message to extract the request ID
                    OutgoingMessage<RequestPayload>? requestMsg = JsonSerializer.Deserialize<
                        OutgoingMessage<RequestPayload>
                    >(buffer.Span, TestUtils.s_jsonSerializerOptions);
                    if (requestMsg?.D?.RequestType == "GetVersion")
                    {
                        capturedRequestId = requestMsg.D.RequestId;
                        Assert.IsNotNull(
                            capturedRequestId,
                            "Captured Request ID should not be null."
                        );
                        // Simulate the corresponding response arriving
                        RequestResponsePayload<object> response = new( // Use object type matching client's internal handling
                            RequestType: "GetVersion",
                            RequestId: capturedRequestId,
                            RequestStatus: new RequestStatus(
                                Result: true,
                                Code: (int)Core.Protocol.Generated.RequestStatus.Success
                            ),
                            ResponseData: rawResponseData.Value // Pass the JsonElement payload
                        );
                        TestUtils.SimulateIncomingResponse(client, capturedRequestId, response);
                    }
                }
            )
            .Returns(ValueTask.CompletedTask);

        // Mock the DeserializePayload call for the specific response type
        mockSerializer
            .Setup(s =>
                s.DeserializePayload<GetVersionResponseData>(
                    It.Is<object>(o =>
                        // Verify it's called with the correct raw payload data
                        o != null
                        && o.GetType() == typeof(JsonElement)
                        && ((JsonElement)o).GetRawText() == rawResponseData.Value.GetRawText()
                    )
                )
            )
            .Returns(expectedResponsePayload);

        // Act
        GetVersionResponseData? actualResponse = await client.GetVersionAsync();

        // Assert
        // Verify the response DTO is correct
        Assert.IsNotNull(actualResponse, "Response should not be null.");
        Assert.AreEqual(
            expectedResponsePayload.ObsWebSocketVersion,
            actualResponse.ObsWebSocketVersion
        );
        Assert.AreEqual(expectedResponsePayload.ObsVersion, actualResponse.ObsVersion);
        Assert.AreEqual(expectedResponsePayload.RpcVersion, actualResponse.RpcVersion);

        // Verify SendAsync was called once with a GetVersion request
        mockWebSocket.Verify(
            ws =>
                ws.SendAsync(
                    It.Is<ReadOnlyMemory<byte>>(m => IsRequestType(m, "GetVersion")),
                    It.IsAny<WebSocketMessageType>(),
                    true,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        // Verify the specific payload deserialization was called once
        mockSerializer.Verify(
            s => s.DeserializePayload<GetVersionResponseData>(It.Is<object>(o => o is JsonElement)),
            Times.Once
        );

        // Verify the pending request was removed
        ConcurrentDictionary<string, TaskCompletionSource<object>>? pendingRequests =
            TestUtils.GetPendingRequests(client);
        Assert.IsNotNull(pendingRequests);
        Assert.IsNotNull(capturedRequestId);
        Assert.IsFalse(
            pendingRequests.ContainsKey(capturedRequestId),
            "Pending request should have been removed."
        );
    }

    // --- Test Request WITH Request Data and NO Response Data ---

    /// <summary>
    /// Verifies that SetCurrentProgramSceneAsync sends the correct request structure
    /// including the request data and completes successfully when OBS sends a success response.
    /// </summary>
    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SetCurrentProgramSceneAsync_SendsCorrectRequest_CompletesSuccessfully()
    {
        // Arrange
        (
            ObsWebSocketClient? client,
            Mock<IWebSocketMessageSerializer>? mockSerializer,
            Mock<IWebSocketConnection>? mockWebSocket
        ) = TestUtils.SetupConnectedClientForceState();

        SetCurrentProgramSceneRequestData requestData = new("TestScene");
        object? rawResponseData = null; // No response data s_expectedFailNoRetryLog for this request type
        string? capturedRequestId = null;
        byte[]? sentRequestBytes = null; // Capture raw bytes for deeper inspection if needed

        // Mock SendAsync to capture ID and simulate success response
        mockWebSocket
            .Setup(ws =>
                ws.SendAsync(
                    It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<WebSocketMessageType>(),
                    true,
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(
                (
                    ReadOnlyMemory<byte> buffer,
                    WebSocketMessageType msgType,
                    bool endOfMsg,
                    CancellationToken ct
                ) =>
                {
                    sentRequestBytes = buffer.ToArray();
                    OutgoingMessage<RequestPayload>? requestMsg = JsonSerializer.Deserialize<
                        OutgoingMessage<RequestPayload>
                    >(buffer.Span, TestUtils.s_jsonSerializerOptions);
                    if (requestMsg?.D?.RequestType == "SetCurrentProgramScene")
                    {
                        capturedRequestId = requestMsg.D.RequestId;
                        Assert.IsNotNull(capturedRequestId);
                        RequestResponsePayload<object> response = new(
                            RequestType: "SetCurrentProgramScene",
                            RequestId: capturedRequestId,
                            RequestStatus: new RequestStatus(
                                Result: true,
                                Code: (int)Core.Protocol.Generated.RequestStatus.Success
                            ),
                            ResponseData: rawResponseData // null
                        );
                        TestUtils.SimulateIncomingResponse(client, capturedRequestId, response);
                    }
                }
            )
            .Returns(ValueTask.CompletedTask);

        // Mock the DeserializePayload call for the response (expecting null or default for object)
        mockSerializer
            .Setup(s => s.DeserializePayload<object>(It.IsAny<object>()))
            .Returns((object?)null); // Explicitly return null for clarity

        // Act
        // This call should complete without throwing when the simulated response arrives.
        await client.SetCurrentProgramSceneAsync(requestData);

        // Assert
        // Verify SendAsync was called once with the correct request type and data
        mockWebSocket.Verify(
            ws =>
                ws.SendAsync(
                    It.Is<ReadOnlyMemory<byte>>(m =>
                        IsRequestType(m, "SetCurrentProgramScene")
                        && RequestDataMatches(m, requestData) // Ensure data matching helper is used if applicable
                    ),
                    It.IsAny<WebSocketMessageType>(),
                    true,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );

        // Verify the base object deserialization *was* attempted (even with null data)
        mockSerializer.Verify(s => s.DeserializePayload<object>(It.IsAny<object>()), Times.Once());

        // Verify the pending request was removed
        ConcurrentDictionary<string, TaskCompletionSource<object>>? pendingRequests =
            TestUtils.GetPendingRequests(client);
        Assert.IsNotNull(pendingRequests);
        Assert.IsNotNull(capturedRequestId);
        Assert.IsFalse(
            pendingRequests.ContainsKey(capturedRequestId),
            "Pending request should have been removed."
        );
    }

    // --- Test Request WITH Response Data (Value Type Property) ---

    /// <summary>
    /// Verifies that GetInputMuteAsync sends the correct request and correctly
    /// deserializes a response containing a value type property (bool).
    /// </summary>
    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetInputMuteAsync_SendsRequest_ReturnsCorrectDto()
    {
        // Arrange
        (
            ObsWebSocketClient? client,
            Mock<IWebSocketMessageSerializer>? mockSerializer,
            Mock<IWebSocketConnection>? mockWebSocket
        ) = TestUtils.SetupConnectedClientForceState();

        string inputName = "Mic/Aux";
        bool expectedMutedState = true;
        GetInputMuteRequestData requestDto = new(inputName);
        GetInputMuteResponseData responseDto = new(expectedMutedState); // DTO with value type property
        JsonElement? rawResponseData = TestUtils.ToJsonElement(responseDto)!; // Serialize DTO to JsonElement for mocking
        string? capturedRequestId = null;

        // Mock SendAsync
        mockWebSocket
            .Setup(ws =>
                ws.SendAsync(
                    It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<WebSocketMessageType>(),
                    true,
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(
                (
                    ReadOnlyMemory<byte> buffer,
                    WebSocketMessageType msgType,
                    bool endOfMsg,
                    CancellationToken ct
                ) =>
                {
                    OutgoingMessage<RequestPayload>? requestMsg = JsonSerializer.Deserialize<
                        OutgoingMessage<RequestPayload>
                    >(buffer.Span, TestUtils.s_jsonSerializerOptions);
                    if (requestMsg?.D?.RequestType == "GetInputMute")
                    {
                        capturedRequestId = requestMsg.D.RequestId;
                        Assert.IsNotNull(capturedRequestId);
                        RequestResponsePayload<object> response = new(
                            RequestType: "GetInputMute",
                            RequestId: capturedRequestId,
                            RequestStatus: new RequestStatus(
                                Result: true,
                                Code: (int)Core.Protocol.Generated.RequestStatus.Success
                            ),
                            ResponseData: rawResponseData.Value // Pass JsonElement payload
                        );
                        TestUtils.SimulateIncomingResponse(client, capturedRequestId, response);
                    }
                }
            )
            .Returns(ValueTask.CompletedTask);

        // Mock the specific response DTO deserialization
        mockSerializer
            .Setup(s =>
                s.DeserializePayload<GetInputMuteResponseData>(
                    It.Is<object>(o =>
                        o != null
                        && o.GetType() == typeof(JsonElement)
                        && ((JsonElement)o).GetRawText() == rawResponseData.Value.GetRawText()
                    )
                )
            )
            .Returns(responseDto);

        // Act
        GetInputMuteResponseData? actualResponseDto = await client.GetInputMuteAsync(requestDto);

        // Assert
        Assert.IsNotNull(actualResponseDto, "Response DTO should not be null.");
        Assert.AreEqual(expectedMutedState, actualResponseDto.InputMuted);

        // Verify SendAsync was called with correct request
        mockWebSocket.Verify(
            ws =>
                ws.SendAsync(
                    It.Is<ReadOnlyMemory<byte>>(m =>
                        IsRequestType(m, "GetInputMute") && RequestDataMatches(m, requestDto)
                    ),
                    It.IsAny<WebSocketMessageType>(),
                    true,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        // Verify the specific payload deserialization was called
        mockSerializer.Verify(
            s =>
                s.DeserializePayload<GetInputMuteResponseData>(
                    It.Is<object>(o => o is JsonElement)
                ),
            Times.Once
        );

        // Verify the pending request was removed
        ConcurrentDictionary<string, TaskCompletionSource<object>>? pendingRequests =
            TestUtils.GetPendingRequests(client);
        Assert.IsNotNull(pendingRequests);
        Assert.IsNotNull(capturedRequestId);
        Assert.IsFalse(
            pendingRequests.ContainsKey(capturedRequestId),
            "Pending request should have been removed."
        );
    }

    // --- Test Request Failure Propagation ---

    /// <summary>
    /// Verifies that if OBS returns a failure status for a request, the client
    /// throws an ObsWebSocketException containing the error details.
    /// </summary>
    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task RequestExtension_FailureResponse_ThrowsObsWebSocketException()
    {
        // Arrange
        (
            ObsWebSocketClient? client,
            Mock<IWebSocketMessageSerializer>? mockSerializer,
            Mock<IWebSocketConnection>? mockWebSocket
        ) = TestUtils.SetupConnectedClientForceState();

        string requestType = "GetVersion"; // Using GetVersion as an example request
        RequestStatus failureStatus = new(
            Result: false,
            Code: (int)Core.Protocol.Generated.RequestStatus.ResourceNotFound,
            Comment: "ResourceNotFound"
        );
        object? rawResponseData = null; // No data s_expectedFailNoRetryLog on failure
        string? capturedRequestId = null;

        // Mock SendAsync to simulate the failure response
        mockWebSocket
            .Setup(ws =>
                ws.SendAsync(
                    It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<WebSocketMessageType>(),
                    true,
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(
                (
                    ReadOnlyMemory<byte> buffer,
                    WebSocketMessageType msgType,
                    bool endOfMsg,
                    CancellationToken ct
                ) =>
                {
                    OutgoingMessage<RequestPayload>? requestMsg = JsonSerializer.Deserialize<
                        OutgoingMessage<RequestPayload>
                    >(buffer.Span, TestUtils.s_jsonSerializerOptions);
                    if (requestMsg?.D?.RequestType == requestType)
                    {
                        capturedRequestId = requestMsg.D.RequestId;
                        Assert.IsNotNull(capturedRequestId);
                        // Simulate the failure response
                        RequestResponsePayload<object> response = new(
                            RequestType: requestType,
                            RequestId: capturedRequestId,
                            RequestStatus: failureStatus, // The failure status
                            ResponseData: rawResponseData
                        );
                        TestUtils.SimulateIncomingResponse(client, capturedRequestId, response);
                    }
                }
            )
            .Returns(ValueTask.CompletedTask);

        // Mock DeserializePayload for the s_expectedFailNoRetryLog (but not received) success payload type - It should return null/default
        mockSerializer
            .Setup(s => s.DeserializePayload<GetVersionResponseData>(It.IsAny<object>()))
            .Returns((GetVersionResponseData?)null);

        // Act & Assert
        // Verify that calling the client method throws the correct exception
        ObsWebSocketException ex = await Assert.ThrowsExceptionAsync<ObsWebSocketException>(
            async () => await client.GetVersionAsync() // Call the specific extension method
        );

        // Check the exception details
        Assert.IsTrue(
            ex.Message.Contains(requestType),
            "Exception message should contain request type."
        );
        Assert.IsTrue(
            ex.Message.Contains(failureStatus.Code.ToString()),
            "Exception message should contain error code."
        );
        Assert.IsTrue(
            ex.Message.Contains(failureStatus.Comment!),
            "Exception message should contain error comment."
        );

        // Verify SendAsync was still called
        mockWebSocket.Verify(
            ws =>
                ws.SendAsync(
                    It.Is<ReadOnlyMemory<byte>>(m => IsRequestType(m, requestType)),
                    It.IsAny<WebSocketMessageType>(),
                    true,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );

        // Verify the pending request was removed even after failure
        ConcurrentDictionary<string, TaskCompletionSource<object>>? pendingRequests =
            TestUtils.GetPendingRequests(client);
        Assert.IsNotNull(pendingRequests);
        Assert.IsNotNull(capturedRequestId);
        Assert.IsFalse(
            pendingRequests.ContainsKey(capturedRequestId),
            "Pending request should have been removed after failure."
        );
    }

    // --- Test Request Timeout (via Caller CancellationToken) ---

    /// <summary>
    /// Verifies that passing a CancellationToken that gets cancelled before the response arrives
    /// results in a TaskCanceledException being thrown by the client method.
    /// </summary>
    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task RequestExtension_TimeoutViaCallerToken_ThrowsTaskCanceledException()
    {
        // Arrange
        (
            ObsWebSocketClient? client,
            Mock<IWebSocketMessageSerializer>? mockSerializer,
            Mock<IWebSocketConnection>? mockWebSocket
        ) = TestUtils.SetupConnectedClientForceState();

        int timeoutMs = 20; // Very short timeout for the caller's token
        string? capturedRequestId = null;

        // Mock SendAsync: Capture request ID but DO NOT simulate a response coming back
        mockWebSocket
            .Setup(ws =>
                ws.SendAsync(
                    It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<WebSocketMessageType>(),
                    true,
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(
                (
                    ReadOnlyMemory<byte> buffer,
                    WebSocketMessageType msgType,
                    bool endOfMsg,
                    CancellationToken ct
                ) =>
                {
                    OutgoingMessage<RequestPayload>? requestMsg = JsonSerializer.Deserialize<
                        OutgoingMessage<RequestPayload>
                    >(buffer.Span, TestUtils.s_jsonSerializerOptions);
                    if (requestMsg?.D?.RequestType == "GetVersion")
                    {
                        capturedRequestId = requestMsg.D.RequestId;
                    }
                }
            )
            .Returns(ValueTask.CompletedTask); // Send completes successfully

        // Act & Assert
        // Expect TaskCanceledException because the CancellationToken passed to GetVersionAsync will be cancelled.
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(
            async () =>
            {
                using CancellationTokenSource cts = new(timeoutMs); // Create token source with short delay
                await client.GetVersionAsync(cts.Token); // Pass the token
            },
            "Expected TaskCanceledException when caller token times out."
        );

        // Verify SendAsync was called
        mockWebSocket.Verify(
            ws =>
                ws.SendAsync(
                    It.Is<ReadOnlyMemory<byte>>(m => IsRequestType(m, "GetVersion")),
                    It.IsAny<WebSocketMessageType>(),
                    true,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );

        // Verify pending request was cleaned up after cancellation
        await Task.Delay(ShortDelay); // Allow a moment for potential background cleanup
        ConcurrentDictionary<string, TaskCompletionSource<object>>? pendingRequests =
            TestUtils.GetPendingRequests(client);
        Assert.IsNotNull(pendingRequests);
        Assert.IsNotNull(capturedRequestId, "Request ID should have been captured");
        Assert.IsFalse(
            pendingRequests.ContainsKey(capturedRequestId),
            "Pending request should have been removed after cancellation."
        );
    }

    // --- Helper Predicates ---

    /// <summary>
    /// Checks if the serialized message bytes correspond to a specific request type.
    /// </summary>
    private static bool IsRequestType(ReadOnlyMemory<byte> buffer, string requestType)
    {
        try
        {
            // Use ReadOnlySpan<byte> for deserialization
            OutgoingMessage<RequestPayload>? msg = JsonSerializer.Deserialize<
                OutgoingMessage<RequestPayload>
            >(buffer.Span, TestUtils.s_jsonSerializerOptions);
            return msg?.D?.RequestType == requestType;
        }
        catch
        {
            return false;
        } // Ignore deserialization errors
    }

    /// <summary>
    /// Checks if the serialized message bytes contain request data matching the provided DTO.
    /// </summary>
    private static bool RequestDataMatches<TRequestData>(
        ReadOnlyMemory<byte> buffer,
        TRequestData expectedData
    )
        where TRequestData : class
    {
        try
        {
            OutgoingMessage<RequestPayload>? msg = JsonSerializer.Deserialize<
                OutgoingMessage<RequestPayload>
            >(buffer.Span, TestUtils.s_jsonSerializerOptions);
            if (msg?.D?.RequestData == null)
            {
                return expectedData == null; // True if both are null
            }

            if (expectedData == null)
            {
                return false; // False if s_expectedFailNoRetryLog is null but actual isn't
            }

            // Deserialize the actual request data and the s_expectedFailNoRetryLog data to compare
            TRequestData? actualData = msg.D.RequestData.Value.Deserialize<TRequestData>(
                TestUtils.s_jsonSerializerOptions
            );

            // Use record equality (if TRequestData is a record) or deep comparison logic if needed
            return Equals(actualData, expectedData);
        }
        catch
        {
            return false;
        } // Ignore errors
    }
}
