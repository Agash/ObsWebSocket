using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Moq;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol.Responses; // Required for DTOs like GetVersionResponseData
using ObsWebSocket.Core.Serialization;
using RequestStatus = ObsWebSocket.Core.Protocol.RequestStatus; // Alias

namespace ObsWebSocket.Tests;

/// <summary>
/// Unit tests focusing on miscellaneous client functionality, primarily batch requests.
/// </summary>
[TestClass]
public partial class ObsWebSocketClientTests
{
    private const int TestTimeout = 5000; // ms
    private const int ShortDelay = 50; // ms

    /// <summary>
    /// Verifies that CallBatchAsync throws ArgumentNullException if the requests collection is null.
    /// </summary>
    [TestMethod]
    public async Task CallBatchAsync_NullRequests_ThrowsArgumentNullException()
    {
        // Arrange
        (ObsWebSocketClient client, _, _) = TestUtils.SetupConnectedClientForceState();

        // Act & Assert
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(async () =>
            await client.CallBatchAsync(null!) // Pass null directly
        );
    }

    /// <summary>
    /// Verifies that CallBatchAsync returns an empty list immediately if the requests collection is empty.
    /// </summary>
    [TestMethod]
    public async Task CallBatchAsync_EmptyRequests_ReturnsEmptyList()
    {
        // Arrange
        (ObsWebSocketClient client, _, _) = TestUtils.SetupConnectedClientForceState();
        IEnumerable<BatchRequestItem> requests = []; // Empty list

        // Act
        List<RequestResponsePayload<object>> results = await client.CallBatchAsync(requests);

        // Assert
        Assert.AreEqual(0, results.Count, "Result list should be empty for an empty batch.");
    }

    /// <summary>
    /// Verifies that CallBatchAsync throws ArgumentException if any item in the batch has an empty RequestType.
    /// </summary>
    [TestMethod]
    public async Task CallBatchAsync_InvalidRequestType_ThrowsArgumentException()
    {
        // Arrange
        (ObsWebSocketClient client, _, _) = TestUtils.SetupConnectedClientForceState();
        List<BatchRequestItem> requests = [new("", null)]; // Item with empty RequestType

        // Act & Assert
        ArgumentException ex = await Assert.ThrowsExceptionAsync<ArgumentException>(async () =>
            await client.CallBatchAsync(requests)
        );
        Assert.IsTrue(
            ex.Message.Contains("index 0"),
            "Exception message should indicate the problematic index."
        );
        Assert.AreEqual(
            "requests",
            ex.ParamName,
            "Exception should target the 'requests' parameter."
        );
    }

    /// <summary>
    /// Verifies that CallBatchAsync correctly serializes and sends a batch request message,
    /// processes the corresponding batch response, and returns the results.
    /// </summary>
    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task CallBatchAsync_ValidRequest_SendsCorrectBatchMessage()
    {
        // Arrange
        (
            ObsWebSocketClient? client,
            Mock<IWebSocketMessageSerializer>? mockSerializer,
            Mock<IWebSocketConnection>? mockWebSocket
        ) = TestUtils.SetupConnectedClientForceState();

        // Define the batch items
        BatchRequestItem request1 = new("GetVersion", null);
        // Ensure request data uses anonymous objects or concrete DTOs that System.Text.Json can serialize
        var request2Data = new { sceneName = "OldScene", newSceneName = "NewScene" };
        BatchRequestItem request2 = new("SetSceneName", request2Data);
        List<BatchRequestItem> requests = [request1, request2];

        // Expected Response Payloads (for simulation)
        GetVersionResponseData response1Data = new(1, "v1", "v5", [], [], "windows", "windows 11");
        RequestResponsePayload<object> response1 = new(
            "GetVersion",
            "batch1_0",
            new RequestStatus(true, 100),
            TestUtils.ToJsonElement(response1Data)
        );
        // SetSceneName has no response data
        RequestResponsePayload<object> response2 = new(
            "SetSceneName",
            "batch1_1",
            new RequestStatus(true, 100),
            null
        );
        // The final batch response payload
        RequestBatchResponsePayload<object> batchResponsePayload = new(
            "batch1",
            [response1, response2]
        ); // Match requestId

        string? capturedBatchRequestId = null;
        byte[]? sentBytes = null; // Capture the serialized batch message bytes

        // Mock SendAsync: Capture the request ID and simulate the batch response
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
                    sentBytes = buffer.ToArray();
                    OutgoingMessage<RequestBatchPayload>? requestMsg = JsonSerializer.Deserialize<
                        OutgoingMessage<RequestBatchPayload>
                    >(buffer.Span, TestUtils.s_jsonSerializerOptions);
                    if (requestMsg?.Op == WebSocketOpCode.RequestBatch)
                    {
                        capturedBatchRequestId = requestMsg.D.RequestId; // Capture the main batch ID
                        Assert.IsNotNull(capturedBatchRequestId);
                        // IMPORTANT: We need to adjust the RequestIDs in the simulated response to match what the client generated.
                        RequestResponsePayload<object> simulatedResponse1 = response1 with
                        {
                            RequestId = $"{capturedBatchRequestId}_0",
                        };
                        RequestResponsePayload<object> simulatedResponse2 = response2 with
                        {
                            RequestId = $"{capturedBatchRequestId}_1",
                        };
                        RequestBatchResponsePayload<object> simulatedBatchResponse = new(
                            capturedBatchRequestId,
                            [simulatedResponse1, simulatedResponse2]
                        );

                        // Simulate the batch response arriving
                        TestUtils.SimulateIncomingResponse(
                            client,
                            capturedBatchRequestId,
                            simulatedBatchResponse
                        );
                    }
                }
            )
            .Returns(ValueTask.CompletedTask);

        // Mock DeserializePayload for the batch response structure
        mockSerializer
            .Setup(s =>
                s.DeserializePayload<RequestBatchResponsePayload<object>>(It.IsAny<object>())
            )
            .Returns(
                (object? data) =>
                {
                    // Simulate the deserialization accurately
                    return data is RequestBatchResponsePayload<object> typedData ? typedData : null;
                }
            );

        // Act
        List<RequestResponsePayload<object>> results = await client.CallBatchAsync(requests);

        // Assert
        // Verify the structure and content of the results list
        Assert.AreEqual(2, results.Count, "Should receive two results.");
        Assert.AreEqual("GetVersion", results[0].RequestType);
        Assert.AreEqual($"{capturedBatchRequestId}_0", results[0].RequestId);
        Assert.IsTrue(results[0].RequestStatus.Result);
        Assert.AreEqual("SetSceneName", results[1].RequestType);
        Assert.AreEqual($"{capturedBatchRequestId}_1", results[1].RequestId);
        Assert.IsTrue(results[1].RequestStatus.Result);
        Assert.IsNull(results[1].ResponseData, "SetSceneName should have null response data.");

        // Verify SendAsync was called once with a Batch request
        mockWebSocket.Verify(
            ws =>
                ws.SendAsync(
                    It.Is<ReadOnlyMemory<byte>>(m =>
                        IsRequestType(m, WebSocketOpCode.RequestBatch) // Helper to check OpCode
                    ),
                    It.IsAny<WebSocketMessageType>(),
                    true,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );

        Assert.IsNotNull(sentBytes, "Sent bytes should have been captured.");
        try
        {
            OutgoingMessage<RequestBatchPayload>? sentMessage = JsonSerializer.Deserialize<
                OutgoingMessage<RequestBatchPayload>
            >(sentBytes, TestUtils.s_jsonSerializerOptions);
            Assert.IsNotNull(sentMessage);
            Assert.AreEqual(WebSocketOpCode.RequestBatch, sentMessage.Op);
            Assert.AreEqual(capturedBatchRequestId, sentMessage.D.RequestId);
            Assert.AreEqual(requests.Count, sentMessage.D.Requests.Count);
            Assert.AreEqual(request1.RequestType, sentMessage.D.Requests[0].RequestType);
            Assert.AreEqual(request2.RequestType, sentMessage.D.Requests[1].RequestType);
            Assert.IsTrue(
                sentMessage
                    .D.Requests[0]
                    .RequestId.StartsWith(capturedBatchRequestId ?? string.Empty)
            ); // Use null check
            Assert.IsTrue(
                sentMessage
                    .D.Requests[1]
                    .RequestId.StartsWith(capturedBatchRequestId ?? string.Empty)
            ); // Use null check

            // Verify request data serialization
            JsonElement req2SentData = sentMessage
                .D.Requests[1]
                .RequestData!.Value.Deserialize<JsonElement>(TestUtils.s_jsonSerializerOptions);

            Assert.IsTrue(
                req2SentData.TryGetProperty("sceneName", out JsonElement sceneNameElement),
                "sceneName property missing"
            );
            Assert.AreEqual(request2Data.sceneName, sceneNameElement.GetString());

            Assert.IsTrue(
                req2SentData.TryGetProperty("newSceneName", out JsonElement newSceneNameElement),
                "newSceneName property missing"
            );
            Assert.AreEqual(request2Data.newSceneName, newSceneNameElement.GetString());
        }
        catch (Exception ex)
        {
            Assert.Fail($"Failed to deserialize or verify sent batch message: {ex}");
        }

        // Verify the pending batch request was removed
        ConcurrentDictionary<string, TaskCompletionSource<object>>? pendingBatches =
            TestUtils.GetPendingBatchRequests(client);
        Assert.IsNotNull(pendingBatches);
        Assert.IsNotNull(capturedBatchRequestId);
        Assert.IsFalse(
            pendingBatches.ContainsKey(capturedBatchRequestId),
            "Pending batch request should have been removed."
        );
    }

    /// <summary>
    /// Verifies that CallBatchAsync throws an ObsWebSocketException (wrapping OperationCanceledException)
    /// if the request times out based on the provided timeoutMs.
    /// </summary>
    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task CallBatchAsync_Timeout_ThrowsObsWebSocketException()
    {
        // Arrange
        (ObsWebSocketClient client, _, Mock<IWebSocketConnection> mockWebSocket) =
            TestUtils.SetupConnectedClientForceState();
        List<BatchRequestItem> requests = [new("GetVersion", null)];
        int timeoutMs = 10; // Very short timeout
        string? capturedBatchRequestId = null;

        // Mock SendAsync: Capture ID but DO NOT simulate a response
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
                    OutgoingMessage<RequestBatchPayload>? requestMsg = JsonSerializer.Deserialize<
                        OutgoingMessage<RequestBatchPayload>
                    >(buffer.Span, TestUtils.s_jsonSerializerOptions);
                    if (requestMsg?.Op == WebSocketOpCode.RequestBatch)
                    {
                        capturedBatchRequestId = requestMsg.D.RequestId;
                    }
                }
            )
            .Returns(ValueTask.CompletedTask);

        // Act & Assert
        ObsWebSocketException ex = await Assert.ThrowsExceptionAsync<ObsWebSocketException>(
            async () => await client.CallBatchAsync(requests, timeoutMs: timeoutMs) // Use the timeout override
        );

        // Verify exception details
        Assert.IsTrue(
            ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase),
            "Exception message should indicate timeout."
        );
        Assert.IsInstanceOfType<OperationCanceledException>(
            ex.InnerException,
            "Inner exception should be OperationCanceledException."
        );

        // Verify SendAsync was called
        mockWebSocket.Verify(
            ws =>
                ws.SendAsync(
                    It.Is<ReadOnlyMemory<byte>>(m =>
                        IsRequestType(m, WebSocketOpCode.RequestBatch)
                    ),
                    It.IsAny<WebSocketMessageType>(),
                    true,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );

        // Verify pending batch request was cleaned up
        await Task.Delay(ShortDelay); // Allow time for cleanup
        ConcurrentDictionary<string, TaskCompletionSource<object>>? pendingBatches =
            TestUtils.GetPendingBatchRequests(client);
        Assert.IsNotNull(pendingBatches);
        Assert.IsNotNull(capturedBatchRequestId, "Batch Request ID should have been captured.");
        Assert.IsFalse(
            pendingBatches.ContainsKey(capturedBatchRequestId),
            "Pending batch request should have been removed after timeout."
        );
    }

    // --- Helper Predicate ---
    /// <summary>
    /// Checks if the serialized message bytes correspond to a specific OpCode.
    /// </summary>
    private static bool IsRequestType(ReadOnlyMemory<byte> buffer, WebSocketOpCode opCode)
    {
        try
        {
            // Minimal deserialization just to check OpCode
            using JsonDocument doc = JsonDocument.Parse(buffer);
            return doc.RootElement.TryGetProperty("op", out JsonElement opElement)
                && opElement.TryGetInt32(out int opVal)
                && opVal == (int)opCode;
        }
        catch
        {
            return false;
        } // Ignore deserialization errors
    }
}
