using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Common;
using ObsWebSocket.Core.Protocol.Events;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Tests;

/// <summary>
/// Unit tests focusing on the event handling logic of <see cref="ObsWebSocketClient"/>.
/// Uses ReceiveAsync simulation for testing event processing.
/// </summary>
[TestClass]
public class ObsWebSocketClientEventTests
{
    // Helper needed for invoking private methods like ReceiveLoopAsync
    internal static TDelegate? GetPrivateMethodDelegate<TDelegate>(object obj, string methodName)
        where TDelegate : Delegate
    {
        MethodInfo? method = obj.GetType()
            .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        return method != null
            ? (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), obj, method)
            : null;
    }

    /// <summary>
    /// Helper method to mock IWebSocketConnection.ReceiveAsync to return a single message
    /// specified by messageBytes and then block subsequent calls using a TaskCompletionSource.
    /// </summary>
    /// <param name="mockWebSocket">The mocked WebSocket connection.</param>
    /// <param name="messageBytes">The bytes of the message to simulate receiving.</param>
    /// <param name="messageType">The WebSocket message type (default: Text).</param>
    /// <returns>A TaskCompletionSource that blocks the receive loop after the first message.</returns>
    private static TaskCompletionSource<ValueWebSocketReceiveResult> SetupSingleReceiveAndBlock(
        Mock<IWebSocketConnection> mockWebSocket,
        byte[] messageBytes,
        WebSocketMessageType messageType = WebSocketMessageType.Text
    )
    {
        int receiveCallCount = 0;
        // TCS to block the receive loop after the first message is processed
        TaskCompletionSource<ValueWebSocketReceiveResult> blockTcs = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        mockWebSocket
            .Setup(ws => ws.ReceiveAsync(It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(
                (Memory<byte> buffer, CancellationToken ct) =>
                {
                    receiveCallCount++;
                    if (receiveCallCount == 1) // First call: Return the message data
                    {
                        Debug.WriteLine(
                            $"--> ReceiveAsync Mock: Call {receiveCallCount}, Copying {messageBytes.Length} bytes."
                        );
                        if (buffer.Length >= messageBytes.Length)
                        {
                            messageBytes.CopyTo(buffer);
                        }
                        else
                        {
                            Assert.Fail(
                                $"Test buffer too small ({buffer.Length}) for message ({messageBytes.Length})"
                            );
                        }

                        return ValueTask.FromResult(
                            new ValueWebSocketReceiveResult(messageBytes.Length, messageType, true)
                        );
                    }
                    else // Subsequent calls: Block using the TCS
                    {
                        Debug.WriteLine(
                            $"--> ReceiveAsync Mock: Blocking call {receiveCallCount}."
                        );
                        // Register cancellation to ensure the TCS doesn't block indefinitely if the test is cancelled
                        ct.Register(() =>
                        {
                            Debug.WriteLine("--> ReceiveAsync block cancelled by token.");
                            blockTcs.TrySetCanceled(ct);
                        });
                        // Return the TCS task, which will complete only when set (or cancelled)
                        return new ValueTask<ValueWebSocketReceiveResult>(
                            blockTcs.Task.ContinueWith(
                                _ =>
                                {
                                    Debug.WriteLine(
                                        "--> ReceiveAsync block TCS completed. Returning Close."
                                    );
                                    // Return Close on completion to ensure the loop eventually terminates gracefully if cancelled
                                    return new ValueWebSocketReceiveResult(
                                        0,
                                        WebSocketMessageType.Close,
                                        true
                                    );
                                },
                                TaskContinuationOptions.ExecuteSynchronously
                            )
                        );
                    }
                }
            );
        return blockTcs; // Return the TCS in case the test needs to interact with it (though typically not needed for blocking)
    }

    /// <summary>
    /// Starts the client's internal ReceiveLoopAsync via reflection for testing purposes.
    /// </summary>
    /// <param name="client">The client instance.</param>
    /// <returns>The Task representing the running receive loop.</returns>
    private static Task StartReceiveLoopAsync(ObsWebSocketClient client)
    {
        CancellationTokenSource? clientLifetimeCts =
            TestUtils.GetPrivateField<CancellationTokenSource>(client, "_clientLifetimeCts");
        Assert.IsNotNull(clientLifetimeCts, "Client lifetime CTS not found.");
        CancellationToken clientLifetimeToken = clientLifetimeCts.Token;

        Func<CancellationToken, Task>? receiveLoopDelegate = GetPrivateMethodDelegate<
            Func<CancellationToken, Task>
        >(client, "ReceiveLoopAsync");
        Assert.IsNotNull(receiveLoopDelegate, "ReceiveLoopAsync delegate not found.");

        // Start the loop on a background thread, passing the client's lifetime token
        return Task.Run(() => receiveLoopDelegate(clientLifetimeToken), clientLifetimeToken);
    }

    /// <summary>
    /// Gracefully stops the receive loop task by cancelling the client's lifetime token
    /// and waits briefly for the task to complete.
    /// </summary>
    /// <param name="client">The client instance.</param>
    /// <param name="receiveLoopTask">The task representing the receive loop.</param>
    private static async Task StopReceiveLoopAsync(ObsWebSocketClient client, Task receiveLoopTask)
    {
        CancellationTokenSource? clientLifetimeCts =
            TestUtils.GetPrivateField<CancellationTokenSource>(client, "_clientLifetimeCts");
        if (clientLifetimeCts != null && !clientLifetimeCts.IsCancellationRequested)
        {
            Debug.WriteLine("--> Stopping receive loop via cancellation token...");
            clientLifetimeCts.Cancel();
        }

        try
        {
            // Wait for the loop task to finish after cancellation
            await receiveLoopTask.WaitAsync(TimeSpan.FromMilliseconds(500));
            Debug.WriteLine(
                $"--> Receive loop task completed with status: {receiveLoopTask.Status}"
            );
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("--> Receive loop task cancelled as s_expectedFailNoRetryLog."); /* Expected */
        }
        catch (TimeoutException)
        {
            Debug.WriteLine("Warning: Receive loop did not complete within cleanup timeout.");
        }
        catch (AggregateException ae)
            when (ae.InnerExceptions.All(e =>
                    e is OperationCanceledException or TaskCanceledException
                )
            )
        {
            Debug.WriteLine(
                "--> Receive loop task cancelled as s_expectedFailNoRetryLog (AggregateException)."
            ); /* Expected */
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--> Unexpected exception waiting for receive loop: {ex}");
        } // Log other unexpected exceptions
    }

    /// <summary>
    /// Verifies that receiving an event with a complex payload (like SceneListChanged containing a list)
    /// raises the corresponding C# event with correctly deserialized data.
    /// </summary>
    [TestMethod]
    public async Task HandleEventMessage_SceneListChanged_RaisesCorrectEvent()
    {
        // Arrange
        (
            ObsWebSocketClient? client,
            Mock<IWebSocketMessageSerializer>? mockSerializer,
            Mock<IWebSocketConnection>? mockWebSocket
        ) = TestUtils.SetupConnectedClientForceState();

        SceneStub scene1Data = new("Scene 1", Guid.NewGuid().ToString(), 1);
        SceneStub scene2Data = new("Scene 2", Guid.NewGuid().ToString(), 2);

        List<SceneStub> sceneListElements = [scene1Data, scene2Data];

        SceneListChangedPayload expectedPayloadDto = new([.. sceneListElements]);
        JsonElement innerEventDataJsonElement = TestUtils
            .ToJsonElement(new { scenes = sceneListElements })!
            .Value;
        EventPayloadBase<JsonElement> eventPayloadBaseForSerialization = new(
            EventType: "SceneListChanged",
            EventIntent: (int)EventSubscription.Scenes,
            EventData: innerEventDataJsonElement
        );
        IncomingMessage<JsonElement> incomingMessage = new(
            WebSocketOpCode.Event,
            TestUtils.ToJsonElement(eventPayloadBaseForSerialization)!.Value
        );
        byte[] messageBytes = JsonSerializer.SerializeToUtf8Bytes(
            incomingMessage,
            TestUtils.s_jsonSerializerOptions
        );

        SceneListChangedEventArgs? receivedArgs = null;
        TaskCompletionSource eventReceivedSignal = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        client.SceneListChanged += (_, args) =>
        {
            receivedArgs = args;
            eventReceivedSignal.TrySetResult();
        };

        // Mock Serializer
        mockSerializer
            .Setup(s => s.DeserializeAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(incomingMessage);
        mockSerializer
            .Setup(s =>
                s.DeserializePayload<EventPayloadBase<object>>(It.Is<object>(o => o is JsonElement))
            )
            .Returns(
                new EventPayloadBase<object>(
                    "SceneListChanged",
                    (int)EventSubscription.Scenes,
                    innerEventDataJsonElement
                )
            );
        mockSerializer
            .Setup(s =>
                s.DeserializePayload<SceneListChangedPayload>(
                    It.Is<object>(o =>
                        o is JsonElement
                        && ((JsonElement)o).GetRawText() == innerEventDataJsonElement.GetRawText()
                    )
                )
            )
            .Returns(expectedPayloadDto);

        // Mock WebSocket
        mockWebSocket.Reset(); // Reset setups from helper
        mockWebSocket.SetupGet(c => c.State).Returns(WebSocketState.Open);
        SetupSingleReceiveAndBlock(mockWebSocket, messageBytes); // Setup receive sequence

        // Act
        Task receiveLoopTask = StartReceiveLoopAsync(client);
        bool eventSignalled =
            await Task.WhenAny(eventReceivedSignal.Task, Task.Delay(TimeSpan.FromSeconds(3)))
            == eventReceivedSignal.Task;

        // Assert
        Assert.IsTrue(eventSignalled, "Event signal timed out.");
        Assert.IsNotNull(receivedArgs, "Received event arguments should not be null.");
        Assert.IsNotNull(receivedArgs.EventData.Scenes, "Scenes list should not be null.");
        Assert.AreEqual(2, receivedArgs.EventData.Scenes!.Count);
        Assert.AreEqual(scene1Data.SceneName, receivedArgs.EventData.Scenes[0].SceneName);
        Assert.AreEqual(scene2Data.SceneName, receivedArgs.EventData.Scenes[1].SceneName);

        // Verify mocks
        mockSerializer.Verify(
            s => s.DeserializeAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
        mockSerializer.Verify(
            s =>
                s.DeserializePayload<EventPayloadBase<object>>(
                    It.Is<object>(o => o is JsonElement)
                ),
            Times.Once
        );
        mockSerializer.Verify(
            s =>
                s.DeserializePayload<SceneListChangedPayload>(It.Is<object>(o => o is JsonElement)),
            Times.Once
        );
        mockWebSocket.Verify(
            ws => ws.ReceiveAsync(It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce
        ); // Should be called at least once (maybe twice if block gets cancelled)

        // Cleanup
        await StopReceiveLoopAsync(client, receiveLoopTask);
    }

    /// <summary>
    /// Verifies that receiving a StudioModeStateChanged event raises the corresponding C# event.
    /// Simulates using ReceiveAsync.
    /// </summary>
    [TestMethod]
    public async Task HandleEventMessage_StudioModeStateChanged_RaisesCorrectEvent()
    {
        // Arrange
        (
            ObsWebSocketClient? client,
            Mock<IWebSocketMessageSerializer>? mockSerializer,
            Mock<IWebSocketConnection>? mockWebSocket
        ) = TestUtils.SetupConnectedClientForceState();

        StudioModeStateChangedPayload expectedPayloadDto = new(true);
        JsonElement innerEventDataJsonElement = TestUtils.ToJsonElement(expectedPayloadDto)!.Value;
        EventPayloadBase<JsonElement> eventPayloadBaseForSerialization = new(
            EventType: "StudioModeStateChanged",
            EventIntent: (int)EventSubscription.Ui,
            EventData: innerEventDataJsonElement
        );
        IncomingMessage<JsonElement> incomingMessage = new(
            WebSocketOpCode.Event,
            TestUtils.ToJsonElement(eventPayloadBaseForSerialization)!.Value
        );
        byte[] messageBytes = JsonSerializer.SerializeToUtf8Bytes(
            incomingMessage,
            TestUtils.s_jsonSerializerOptions
        );

        StudioModeStateChangedEventArgs? receivedArgs = null;
        TaskCompletionSource eventReceivedSignal = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        client.StudioModeStateChanged += (_, args) =>
        {
            receivedArgs = args;
            eventReceivedSignal.TrySetResult();
        };

        // Mock Serializer
        mockSerializer
            .Setup(s => s.DeserializeAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(incomingMessage);
        mockSerializer
            .Setup(s =>
                s.DeserializePayload<EventPayloadBase<object>>(It.Is<object>(o => o is JsonElement))
            )
            .Returns(
                new EventPayloadBase<object>(
                    "StudioModeStateChanged",
                    (int)EventSubscription.Ui,
                    innerEventDataJsonElement
                )
            );
        mockSerializer
            .Setup(s =>
                s.DeserializePayload<StudioModeStateChangedPayload>(
                    It.Is<object>(o =>
                        o is JsonElement
                        && ((JsonElement)o).GetRawText() == innerEventDataJsonElement.GetRawText()
                    )
                )
            )
            .Returns(expectedPayloadDto);

        // Mock WebSocket
        mockWebSocket.Reset();
        mockWebSocket.SetupGet(c => c.State).Returns(WebSocketState.Open);
        SetupSingleReceiveAndBlock(mockWebSocket, messageBytes);

        // Act
        Task receiveLoopTask = StartReceiveLoopAsync(client);
        bool eventSignalled =
            await Task.WhenAny(eventReceivedSignal.Task, Task.Delay(TimeSpan.FromSeconds(3)))
            == eventReceivedSignal.Task;

        // Assert
        Assert.IsTrue(eventSignalled, "Event signal timed out.");
        Assert.IsNotNull(receivedArgs, "Received event arguments should not be null.");
        Assert.AreEqual(
            expectedPayloadDto.StudioModeEnabled,
            receivedArgs.EventData.StudioModeEnabled
        );

        // Verify mocks
        mockWebSocket.Verify(
            ws => ws.ReceiveAsync(It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce
        );
        mockSerializer.Verify(
            s => s.DeserializeAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
        mockSerializer.Verify(
            s =>
                s.DeserializePayload<EventPayloadBase<object>>(
                    It.Is<object>(o => o is JsonElement)
                ),
            Times.Once
        );
        mockSerializer.Verify(
            s =>
                s.DeserializePayload<StudioModeStateChangedPayload>(
                    It.Is<object>(o => o is JsonElement)
                ),
            Times.Once
        );

        // Cleanup
        await StopReceiveLoopAsync(client, receiveLoopTask);
    }

    /// <summary>
    /// Verifies that receiving an event without a data payload (like ExitStarted)
    /// raises the corresponding C# event correctly.
    /// Simulates using ReceiveAsync.
    /// </summary>
    [TestMethod]
    public async Task HandleEventMessage_ExitStarted_RaisesCorrectEvent()
    {
        // Arrange
        (
            ObsWebSocketClient? client,
            Mock<IWebSocketMessageSerializer>? mockSerializer,
            Mock<IWebSocketConnection>? mockWebSocket
        ) = TestUtils.SetupConnectedClientForceState();

        EventPayloadBase<JsonElement?> eventPayloadBaseForSerialization = new(
            EventType: "ExitStarted",
            EventIntent: (int)EventSubscription.General,
            EventData: null
        );
        IncomingMessage<JsonElement> incomingMessage = new(
            WebSocketOpCode.Event,
            TestUtils.ToJsonElement(eventPayloadBaseForSerialization)!.Value
        );
        byte[] messageBytes = JsonSerializer.SerializeToUtf8Bytes(
            incomingMessage,
            TestUtils.s_jsonSerializerOptions
        );

        ExitStartedEventArgs? receivedArgs = null;
        TaskCompletionSource eventReceivedSignal = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        client.ExitStarted += (_, args) =>
        {
            receivedArgs = args;
            eventReceivedSignal.TrySetResult();
        };

        // Mock Serializer
        mockSerializer
            .Setup(s => s.DeserializeAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(incomingMessage);
        // Mock only the base deserialization, as EventData is null
        mockSerializer
            .Setup(s =>
                s.DeserializePayload<EventPayloadBase<object>>(It.Is<object>(o => o is JsonElement))
            )
            .Returns(
                new EventPayloadBase<object>("ExitStarted", (int)EventSubscription.General, null)
            );

        // Mock WebSocket
        mockWebSocket.Reset();
        mockWebSocket.SetupGet(c => c.State).Returns(WebSocketState.Open);
        SetupSingleReceiveAndBlock(mockWebSocket, messageBytes);

        // Act
        Task receiveLoopTask = StartReceiveLoopAsync(client);
        bool eventSignalled =
            await Task.WhenAny(eventReceivedSignal.Task, Task.Delay(TimeSpan.FromSeconds(3)))
            == eventReceivedSignal.Task;

        // Assert
        Assert.IsTrue(eventSignalled, "Event signal timed out.");
        Assert.IsNotNull(receivedArgs, "Received event arguments should not be null.");

        // Verify mocks
        mockWebSocket.Verify(
            ws => ws.ReceiveAsync(It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce
        );
        mockSerializer.Verify(
            s => s.DeserializeAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
        mockSerializer.Verify(
            s =>
                s.DeserializePayload<EventPayloadBase<object>>(
                    It.Is<object>(o => o is JsonElement)
                ),
            Times.Once
        );
        // Verify no *specific* payload deserialization happened
        mockSerializer.Verify(
            s => s.DeserializePayload<It.IsSubtype<object>>(It.IsAny<object>()),
            Times.Exactly(1)
        ); // Only the base deserialize was called

        // Cleanup
        await StopReceiveLoopAsync(client, receiveLoopTask);
    }

    /// <summary>
    /// Verifies that receiving an event type for which no specific handler is registered
    /// does not throw an exception and logs a warning.
    /// Simulates using ReceiveAsync.
    /// </summary>
    [TestMethod]
    public async Task HandleEventMessage_UnhandledEventType_DoesNotThrowAndLogsWarning()
    {
        // Arrange
        Mock<ILogger<ObsWebSocketClient>> mockLogger = new();
        Mock<IWebSocketMessageSerializer> mockSerializer = new(MockBehavior.Strict); // Use Strict for serializer too
        (ObsWebSocketClient? client, Mock<IWebSocketConnection>? mockWebSocket, _, _) =
            TestUtils.BuildMockedClientInfrastructure(existingSerializerMock: mockSerializer);
        TestUtils.SetPrivateField(client, "_logger", mockLogger.Object);
        TestUtils.SetPrivateField(client, "_connectionState", ConnectionState.Connected);
        TestUtils.SetPrivateProperty(client, "IsConnected", true);
        TestUtils.SetPrivateField(client, "_webSocket", mockWebSocket.Object);
        TestUtils.SetPrivateField(client, "_clientLifetimeCts", new CancellationTokenSource());

        string unhandledEventType = "ThisEventDoesNotExist";
        JsonElement innerEventData = TestUtils.ToJsonElement(new { some = "data" })!.Value;
        EventPayloadBase<JsonElement> eventPayloadBaseForSerialization = new(
            EventType: unhandledEventType,
            EventIntent: 1,
            EventData: innerEventData
        );
        IncomingMessage<JsonElement> incomingMessage = new(
            WebSocketOpCode.Event,
            TestUtils.ToJsonElement(eventPayloadBaseForSerialization)!.Value
        );
        byte[] messageBytes = JsonSerializer.SerializeToUtf8Bytes(
            incomingMessage,
            TestUtils.s_jsonSerializerOptions
        );

        // --- Mock Serializer ---
        mockSerializer
            .Setup(s => s.DeserializeAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(incomingMessage);
        // Mock ONLY the base deserialization
        mockSerializer
            .Setup(s =>
                s.DeserializePayload<EventPayloadBase<object>>(It.Is<object>(o => o is JsonElement))
            )
            .Returns(new EventPayloadBase<object>(unhandledEventType, 1, innerEventData));

        // --- Mock WebSocket ---
        mockWebSocket.Reset();
        mockWebSocket.SetupGet(c => c.State).Returns(WebSocketState.Open);
        TaskCompletionSource<ValueWebSocketReceiveResult> blockTcs = SetupSingleReceiveAndBlock(
            mockWebSocket,
            messageBytes
        );

        // --- Act ---
        Task receiveLoopTask = StartReceiveLoopAsync(client);
        await Task.Delay(150); // Give time for the loop to process the single message

        // --- Assert ---
        // Verify the warning log IS generated
        mockLogger.Verify(
            logger =>
                logger.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>(
                        (state, _) =>
                            state != null
                            && state
                                .ToString()!
                                .Contains(
                                    $"Received event with unhandled type: {unhandledEventType}"
                                )
                    ),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            Times.Once,
            "Expected warning log for unhandled event type was not found or message format differs."
        );

        // Verify mocks
        mockWebSocket.Verify(
            ws => ws.ReceiveAsync(It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce
        );
        mockSerializer.Verify(
            s => s.DeserializeAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
        mockSerializer.Verify(
            s =>
                s.DeserializePayload<EventPayloadBase<object>>(
                    It.Is<object>(o => o is JsonElement)
                ),
            Times.Once
        );
        // Ensure NO specific payload deserialization was attempted
        mockSerializer.Verify(
            s => s.DeserializePayload<It.IsSubtype<object>>(It.IsAny<object>()),
            Times.Exactly(1)
        );

        // --- Cleanup ---
        await StopReceiveLoopAsync(client, receiveLoopTask);
    }

    /// <summary>
    /// Verifies that if deserializing the specific event payload throws an exception,
    /// an error is logged, indicating the exception was caught.
    /// Simulates using ReceiveAsync.
    /// </summary>
    [TestMethod]
    public async Task HandleEventMessage_PayloadDeserializationThrows_LogsError()
    {
        // Arrange
        Mock<ILogger<ObsWebSocketClient>> mockLogger = new();
        Mock<IWebSocketMessageSerializer> mockSerializer = new(MockBehavior.Strict);
        (ObsWebSocketClient? client, Mock<IWebSocketConnection>? mockWebSocket, _, _) =
            TestUtils.BuildMockedClientInfrastructure(existingSerializerMock: mockSerializer);
        TestUtils.SetPrivateField(client, "_logger", mockLogger.Object);
        TestUtils.SetPrivateField(client, "_connectionState", ConnectionState.Connected);
        TestUtils.SetPrivateProperty(client, "IsConnected", true);
        TestUtils.SetPrivateField(client, "_webSocket", mockWebSocket.Object);
        TestUtils.SetPrivateField(client, "_clientLifetimeCts", new CancellationTokenSource());

        string eventType = "StudioModeStateChanged";
        JsonElement innerEventDataJsonElement = TestUtils
            .ToJsonElement(new { invalid = "structure" })!
            .Value;
        EventPayloadBase<JsonElement> eventPayloadBaseForSerialization = new(
            eventType,
            (int)EventSubscription.Ui,
            innerEventDataJsonElement
        );
        IncomingMessage<JsonElement> incomingMessage = new(
            WebSocketOpCode.Event,
            TestUtils.ToJsonElement(eventPayloadBaseForSerialization)!.Value
        );
        byte[] messageBytes = JsonSerializer.SerializeToUtf8Bytes(
            incomingMessage,
            TestUtils.s_jsonSerializerOptions
        );

        JsonException simulatedException = new(
            "Simulated deserialization failure for specific payload"
        );

        // --- Mock Serializer ---
        mockSerializer
            .Setup(s => s.DeserializeAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(incomingMessage);
        // Base deserialization succeeds
        mockSerializer
            .Setup(s =>
                s.DeserializePayload<EventPayloadBase<object>>(It.Is<object>(o => o is JsonElement))
            )
            .Returns(
                new EventPayloadBase<object>(
                    eventType,
                    (int)EventSubscription.Ui,
                    innerEventDataJsonElement
                )
            );
        // Specific payload deserialization *throws*
        mockSerializer
            .Setup(s =>
                s.DeserializePayload<StudioModeStateChangedPayload>(
                    It.Is<object>(o => o is JsonElement)
                )
            )
            .Throws(simulatedException);

        // --- Mock WebSocket ---
        mockWebSocket.Reset();
        mockWebSocket.SetupGet(c => c.State).Returns(WebSocketState.Open);
        TaskCompletionSource<ValueWebSocketReceiveResult> blockTcs = SetupSingleReceiveAndBlock(
            mockWebSocket,
            messageBytes
        );

        // --- Act ---
        Task receiveLoopTask = StartReceiveLoopAsync(client);
        await Task.Delay(150); // Give time for processing

        // --- Assert ---
        // Verify the error log IS generated
        mockLogger.Verify(
            logger =>
                logger.Log(
                    LogLevel.Error, // Expect Error level
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>(
                        (state, _) =>
                            state != null
                            && state
                                .ToString()!
                                .Contains($"Exception while trying to handle event {eventType}")
                    ),
                    It.Is<JsonException>(ex => ex.Message == simulatedException.Message), // Match the specific exception
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                ),
            Times.Once,
            "Expected error log for exception during payload deserialization was not found, had wrong level/exception, or message format differs."
        );

        // Verify mocks were called
        mockWebSocket.Verify(
            ws => ws.ReceiveAsync(It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce
        );
        mockSerializer.Verify(
            s => s.DeserializeAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
        mockSerializer.Verify(
            s =>
                s.DeserializePayload<EventPayloadBase<object>>(
                    It.Is<object>(o => o is JsonElement)
                ),
            Times.Once
        );
        // Verify the failing call was made
        mockSerializer.Verify(
            s =>
                s.DeserializePayload<StudioModeStateChangedPayload>(
                    It.Is<object>(o => o is JsonElement)
                ),
            Times.Once
        );

        // --- Cleanup ---
        await StopReceiveLoopAsync(client, receiveLoopTask);
    }
}
