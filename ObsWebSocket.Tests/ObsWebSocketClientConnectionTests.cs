using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using Moq;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Serialization;
using static ObsWebSocket.Core.ObsWebSocketClient;

namespace ObsWebSocket.Tests;

[TestClass]
public class ObsWebSocketClientConnectionTests
{
    private static readonly Uri s_testServerUri = new("ws://testhost:4455");
    private const int TestTimeout = 6000;
    private const int ShortDelay = 100;

    private static readonly string[] s_expectedSuccessLog = ["Connecting_1", "Connected"];
    private static readonly string[] s_expectedAuthFailLog =
    [
        "Connecting_1",
        "AuthFailure_1",
        "Disconnected",
    ];
    private static readonly string[] s_expectedFailNoRetryLog =
    [
        "Connecting_1",
        "Failed_1",
        "Disconnected",
    ];

    private static (
        ObsWebSocketClient client,
        Mock<IWebSocketConnection> mockConnection,
        Mock<IWebSocketMessageSerializer> mockSerializer,
        Mock<IWebSocketConnectionFactory> mockFactory
    ) BuildMockedInfrastructure(Action<ObsWebSocketClientOptions>? configureOptions = null) =>
        TestUtils.BuildMockedClientInfrastructure(configureOptions);

    private static void SetupHandshakePayloadDeserialization(
        Mock<IWebSocketMessageSerializer> mockSerializer,
        HelloPayload hello,
        IdentifiedPayload identified
    )
    {
        JsonElement rawHelloData = TestUtils.ToJsonElement(hello)!.Value;
        JsonElement rawIdentifiedData = TestUtils.ToJsonElement(identified)!.Value;

        mockSerializer
            .Setup(s =>
                s.DeserializePayload<HelloPayload>(
                    It.Is<object>(o =>
                        o != null
                        && o.GetType() == typeof(JsonElement)
                        && ((JsonElement)o).GetRawText() == rawHelloData.GetRawText()
                    )
                )
            )
            .Returns(hello)
            .Callback(() =>
                Debug.WriteLine(
                    "--> DeserializePayload<HelloPayload> mock invoked with matching element."
                )
            );

        mockSerializer
            .Setup(s =>
                s.DeserializePayload<IdentifiedPayload>(
                    It.Is<object>(o =>
                        o != null
                        && o.GetType() == typeof(JsonElement)
                        && ((JsonElement)o).GetRawText() == rawIdentifiedData.GetRawText()
                    )
                )
            )
            .Returns(identified)
            .Callback(() =>
                Debug.WriteLine(
                    "--> DeserializePayload<IdentifiedPayload> mock invoked with matching element."
                )
            );
    }

    // --- Connection Tests ---

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ConnectAsync_SuccessfulFirstAttempt_RaisesCorrectEvents()
    {
        // Arrange
        (
            ObsWebSocketClient? client,
            Mock<IWebSocketConnection>? mockConnection,
            Mock<IWebSocketMessageSerializer>? mockSerializer,
            Mock<IWebSocketConnectionFactory>? mockFactory
        ) = BuildMockedInfrastructure(opts =>
        {
            opts.AutoReconnectEnabled = false;
        });

        List<string> eventLog = [];
        TaskCompletionSource connectedSignal = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        TaskCompletionSource identifySentSignal = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        client.Connecting += (_, e) => eventLog.Add($"Connecting_{e.AttemptNumber}");
        client.Connected += (_, _) =>
        {
            Debug.WriteLine("--> Connected event raised");
            eventLog.Add("Connected");
            connectedSignal.TrySetResult();
        };
        client.Disconnected += (_, _) => eventLog.Add("Disconnected");

        // Payloads & Messages
        HelloPayload helloPayload = new("test-ver", 1, null);
        IdentifiedPayload identifiedPayload = new(1);
        IncomingMessage<JsonElement> helloMsg = new(
            WebSocketOpCode.Hello,
            TestUtils.ToJsonElement(helloPayload)!.Value
        );
        IncomingMessage<JsonElement> identifiedMsg = new(
            WebSocketOpCode.Identified,
            TestUtils.ToJsonElement(identifiedPayload)!.Value
        );

        byte[] helloBytes = JsonSerializer.SerializeToUtf8Bytes(
            helloMsg,
            TestUtils.s_jsonSerializerOptions
        );
        byte[] identifiedBytes = JsonSerializer.SerializeToUtf8Bytes(
            identifiedMsg,
            TestUtils.s_jsonSerializerOptions
        );

        IdentifyPayload expectedIdentifyPayload = new(1, null, 0);

        // Mock Serializer
        mockSerializer
            .Setup(s => s.DeserializeAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns(
                (Stream stream, CancellationToken ct) =>
                {
                    byte[] receivedBytes;
                    using (MemoryStream ms = new())
                    {
                        stream.CopyTo(ms);
                        receivedBytes = ms.ToArray();
                    }

                    stream.Position = 0; // Reset stream for potential re-read by logger etc.
                    return receivedBytes.SequenceEqual(helloBytes)
                            ? Task.FromResult<object?>(helloMsg)
                        : receivedBytes.SequenceEqual(identifiedBytes)
                            ? Task.FromResult<object?>(identifiedMsg)
                        : Task.FromResult<object?>(null);
                }
            );
        SetupHandshakePayloadDeserialization(mockSerializer, helloPayload, identifiedPayload);

        byte[] serializedIdentifyBytes = JsonSerializer.SerializeToUtf8Bytes(
            new OutgoingMessage<IdentifyPayload>(WebSocketOpCode.Identify, expectedIdentifyPayload),
            TestUtils.s_jsonSerializerOptions
        );
        mockSerializer
            .Setup(s =>
                s.SerializeAsync(
                    It.Is<OutgoingMessage<IdentifyPayload>>(m =>
                        m.Op == WebSocketOpCode.Identify
                        && m.D.RpcVersion == 1
                        && m.D.Authentication == null
                    ),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(serializedIdentifyBytes);

        // Mock WebSocket Connection
        mockConnection
            .Setup(c => c.ConnectAsync(s_testServerUri, It.IsAny<CancellationToken>()))
            .Callback(() => mockConnection.SetupGet(conn => conn.State).Returns(WebSocketState.Open)
            )
            .Returns(Task.CompletedTask);

        // Setup ReceiveAsync sequence using a counter and helper methods
        int receiveCallCount = 0;
        TaskCompletionSource blockTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        ValueTask<ValueWebSocketReceiveResult> ReceiveBytesAsync(
            Memory<byte> buffer,
            byte[] bytesToCopy
        )
        {
            Debug.WriteLine(
                $"--> ReceiveAsync Mock: Call {receiveCallCount}, Copying {bytesToCopy.Length} bytes."
            );
            bytesToCopy.CopyTo(buffer);
            return ValueTask.FromResult(
                new ValueWebSocketReceiveResult(bytesToCopy.Length, WebSocketMessageType.Text, true)
            );
        }

        ValueTask<ValueWebSocketReceiveResult> BlockReceiveAsync(CancellationToken ct)
        {
            Debug.WriteLine($"--> ReceiveAsync Mock: Blocking call {receiveCallCount}.");
            ct.Register(() => blockTcs.TrySetCanceled(ct));
            return new ValueTask<ValueWebSocketReceiveResult>(
                blockTcs.Task.ContinueWith(
                    _ => new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true), // Return Close on completion
                    TaskContinuationOptions.ExecuteSynchronously
                )
            );
        }

        mockConnection
            .Setup(ws => ws.ReceiveAsync(It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(
                (Memory<byte> buffer, CancellationToken token) =>
                {
                    receiveCallCount++;
                    return receiveCallCount switch
                    {
                        1 => ReceiveBytesAsync(buffer, helloBytes),
                        2 => ReceiveBytesAsync(buffer, identifiedBytes),
                        _ => BlockReceiveAsync(token), // Pass token for cancellation registration
                    };
                }
            );

        // Setup SendAsync
        mockConnection
            .Setup(c =>
                c.SendAsync(
                    It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<WebSocketMessageType>(),
                    true,
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(() => identifySentSignal.TrySetResult())
            .Returns(ValueTask.CompletedTask);

        // Mock Factory
        mockFactory.Setup(f => f.CreateConnection()).Returns(mockConnection.Object);

        // Act
        Task connectTask = client.ConnectAsync();

        // Wait for completion
        bool completedSuccessfully = await Task.WhenAll(connectTask, connectedSignal.Task)
            .WaitAsync(TimeSpan.FromSeconds(TestTimeout / 2.0))
            .ContinueWith(t => t.IsCompletedSuccessfully);

        // Assert
        Assert.IsTrue(
            completedSuccessfully,
            "ConnectAsync task or Connected event timed out or failed."
        );
        Assert.IsTrue(client.IsConnected, "Client IsConnected property should be true.");
        CollectionAssert.AreEqual(s_expectedSuccessLog, eventLog.ToArray(), "Event log mismatch.");

        // Verify mocks
        mockConnection.Verify(
            c => c.ConnectAsync(s_testServerUri, It.IsAny<CancellationToken>()),
            Times.Once
        );
        mockConnection.Verify(
            c =>
                c.SendAsync(
                    It.Is<ReadOnlyMemory<byte>>(m => m.Length > 0), // Verify some data was sent
                    It.IsAny<WebSocketMessageType>(), // Allow Text or Binary
                    true,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        mockConnection.Verify(
            c => c.ReceiveAsync(It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(2) // Expect Hello, Identified, then block
        );
        mockSerializer.Verify(
            s => s.DeserializeAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2) // Hello, Identified
        );

        JsonElement rawHelloD = TestUtils.ToJsonElement(helloPayload)!.Value;
        JsonElement rawIdentifiedD = TestUtils.ToJsonElement(identifiedPayload)!.Value;
        mockSerializer.Verify(
            s =>
                s.DeserializePayload<HelloPayload>(
                    It.Is<object>(o =>
                        o != null
                        && o.GetType() == typeof(JsonElement)
                        && ((JsonElement)o).GetRawText() == rawHelloD.GetRawText()
                    )
                ),
            Times.Once
        );
        mockSerializer.Verify(
            s =>
                s.DeserializePayload<IdentifiedPayload>(
                    It.Is<object>(o =>
                        o != null
                        && o.GetType() == typeof(JsonElement)
                        && ((JsonElement)o).GetRawText() == rawIdentifiedD.GetRawText()
                    )
                ),
            Times.Once
        );
        mockFactory.Verify(f => f.CreateConnection(), Times.Once);
        Assert.IsTrue(
            identifySentSignal.Task.IsCompletedSuccessfully,
            "Identify message was not sent."
        );

        // Cleanup
        await client.DisconnectAsync();
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ConnectAsync_AuthFailure_NoRetry_RaisesCorrectEvents()
    {
        // Arrange
        (
            ObsWebSocketClient? client,
            _, // Ignore the initial mock connection from BuildMockedInfrastructure
            Mock<IWebSocketMessageSerializer>? mockSerializer,
            Mock<IWebSocketConnectionFactory>? mockFactory
        ) = BuildMockedInfrastructure(opts =>
        {
            opts.AutoReconnectEnabled = false;
            opts.Password = null; // Ensure no password is set for this test
        });

        // Create the specific mock connection that will be returned by the factory
        Mock<IWebSocketConnection> mockFailingConnection = new(MockBehavior.Strict);

        List<string> eventLog = [];
        Exception? disconnectedReason = null; // Declare disconnectedReason here
        TaskCompletionSource disconnectedSignal = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        client.Connecting += (_, e) => eventLog.Add($"Connecting_{e.AttemptNumber}");
        client.AuthenticationFailure += (_, e) => eventLog.Add($"AuthFailure_{e.AttemptNumber}");
        client.Connected += (_, _) => eventLog.Add("Connected");
        client.Disconnected += (_, e) =>
        {
            eventLog.Add("Disconnected");
            disconnectedReason = e.ReasonException; // Assign to variable in scope
            disconnectedSignal.TrySetResult();
        };

        // Payloads & Messages
        HelloPayload helloPayload = new("test-ver", 1, new AuthenticationData("challenge", "salt")); // Auth needed
        IncomingMessage<JsonElement> helloMsg = new(
            WebSocketOpCode.Hello,
            TestUtils.ToJsonElement(helloPayload)!.Value
        );
        byte[] helloMessageBytes = JsonSerializer.SerializeToUtf8Bytes(
            helloMsg,
            TestUtils.s_jsonSerializerOptions
        );

        // Mock WebSocket Connection (the one the factory will return)
        mockFailingConnection
            .Setup(c => c.ConnectAsync(s_testServerUri, It.IsAny<CancellationToken>()))
            .Callback(() =>
                mockFailingConnection.SetupGet(conn => conn.State).Returns(WebSocketState.Open)
            )
            .Returns(Task.CompletedTask);
        mockFailingConnection.SetupGet(c => c.Options).Returns(new ClientWebSocket().Options); // Setup required by Strict behavior
        mockFailingConnection.SetupGet(c => c.SubProtocol).Returns("obswebsocket.json"); // Setup required by Strict behavior
        mockFailingConnection.Setup(c => c.Abort()); // Needed for cleanup path
        mockFailingConnection.Setup(c => c.Dispose()); // Needed for cleanup path

        // Initial state is None, transitions to Open after ConnectAsync callback
        mockFailingConnection
            .SetupSequence(c => c.State)
            .Returns(WebSocketState.None) // Before ConnectAsync
            .Returns(WebSocketState.Open) // After ConnectAsync callback
            .Returns(WebSocketState.Open); // During Receive loop

        // Setup ReceiveAsync using counter
        int receiveCallCount = 0;
        mockFailingConnection
            .Setup(ws => ws.ReceiveAsync(It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(
                (Memory<byte> buffer, CancellationToken token) =>
                {
                    receiveCallCount++;
                    if (receiveCallCount == 1) // Only return the Hello message
                    {
                        helloMessageBytes.CopyTo(buffer);
                        return ValueTask.FromResult(
                            new ValueWebSocketReceiveResult(
                                helloMessageBytes.Length,
                                WebSocketMessageType.Text,
                                true
                            )
                        );
                    }
                    else // Block subsequent calls
                    {
                        TaskCompletionSource<ValueWebSocketReceiveResult> blockReceiveTcs = new();
                        token.Register(() => blockReceiveTcs.TrySetCanceled(token));
                        return new ValueTask<ValueWebSocketReceiveResult>(blockReceiveTcs.Task);
                    }
                }
            );
        // SendAsync should NOT be called
        mockFailingConnection
            .Setup(c =>
                c.SendAsync(
                    It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<WebSocketMessageType>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(
                new InvalidOperationException(
                    "SendAsync should not be called in auth failure test before Identify."
                )
            );

        // Mock Serializer
        mockSerializer
            .Setup(s => s.DeserializeAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                (Stream stream, CancellationToken ct) =>
                {
                    if (receiveCallCount == 1)
                    {
                        stream.Position = 0;
                        return JsonSerializer.Deserialize<IncomingMessage<JsonElement>>(
                            stream,
                            TestUtils.s_jsonSerializerOptions
                        );
                    }

                    return null;
                }
            );
        SetupHandshakePayloadDeserialization(
            mockSerializer,
            helloPayload,
            new IdentifiedPayload(0)
        );

        // Mock Factory - Crucially, return the mock we created and setup above
        mockFactory.Setup(f => f.CreateConnection()).Returns(mockFailingConnection.Object);

        // Act & Assert
        AuthenticationFailureException thrownException =
            await Assert.ThrowsExactlyAsync<AuthenticationFailureException>(() =>
                client.ConnectAsync()
            );

        Assert.IsTrue(thrownException.Message.Contains("password was provided"));

        bool disconnectedEventFired =
            await Task.WhenAny(disconnectedSignal.Task, Task.Delay(1000))
            == disconnectedSignal.Task;
        Assert.IsTrue(disconnectedEventFired, "Disconnected event did not fire.");

        CollectionAssert.AreEqual(
            s_expectedAuthFailLog, // Use static field
            eventLog.ToArray()
        );

        Assert.IsFalse(client.IsConnected);
        Assert.IsNotNull(disconnectedReason, "DisconnectedReason should not be null.");
        Assert.IsInstanceOfType<AuthenticationFailureException>(disconnectedReason);

        // Verify mocks against the actual instance used (mockFailingConnection)
        mockFailingConnection.Verify(
            c => c.ConnectAsync(s_testServerUri, It.IsAny<CancellationToken>()),
            Times.Once()
        );
        mockFailingConnection.Verify(
            c => c.ReceiveAsync(It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce() // Receive loop runs briefly
        );
        mockFailingConnection.Verify(
            c =>
                c.SendAsync(
                    It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<WebSocketMessageType>(),
                    true,
                    It.IsAny<CancellationToken>()
                ),
            Times.Never() // Should not send Identify
        );
        mockFactory.Verify(f => f.CreateConnection(), Times.Once());
        mockSerializer.Verify(
            s => s.DeserializeAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Once() // Only called for the Hello message
        );
        mockSerializer.Verify(
            s => s.DeserializePayload<HelloPayload>(It.IsAny<object>()),
            Times.Once // Hello payload was deserialized
        );
        mockSerializer.Verify(
            s => s.DeserializePayload<IdentifiedPayload>(It.IsAny<object>()),
            Times.Never() // Identified deserialization should not happen
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ConnectAsync_ConnectionFailedFirstAttempt_NoRetry_RaisesCorrectEvents()
    {
        // Arrange
        WebSocketException connectException = new("Network Error");
        // Don't capture the mockConnection from BuildMockedInfrastructure here
        (ObsWebSocketClient? client, _, _, Mock<IWebSocketConnectionFactory>? mockFactory) =
            BuildMockedInfrastructure(opts =>
            {
                opts.AutoReconnectEnabled = false;
            });

        // Create the specific mock connection that will fail
        Mock<IWebSocketConnection> mockFailingConnection = new(MockBehavior.Strict);

        List<string> eventLog = [];
        Exception? disconnectedReason = null; // Declare disconnectedReason here
        TaskCompletionSource disconnectedSignal = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        client.Connecting += (_, e) => eventLog.Add($"Connecting_{e.AttemptNumber}");
        client.ConnectionFailed += (_, e) => eventLog.Add($"Failed_{e.AttemptNumber}");
        client.Disconnected += (_, e) =>
        {
            eventLog.Add("Disconnected");
            disconnectedReason = e.ReasonException; // Assign to variable in scope
            disconnectedSignal.TrySetResult();
        };

        // Setup the failing connection mock
        mockFailingConnection.SetupGet(c => c.State).Returns(WebSocketState.None);
        mockFailingConnection.SetupGet(c => c.Options).Returns(new ClientWebSocket().Options);
        mockFailingConnection.SetupGet(c => c.SubProtocol).Returns("obswebsocket.json");
        mockFailingConnection.SetupGet(c => c.CloseStatus).Returns((WebSocketCloseStatus?)null);
        mockFailingConnection.SetupGet(c => c.CloseStatusDescription).Returns((string?)null);
        mockFailingConnection
            .Setup(c => c.ConnectAsync(s_testServerUri, It.IsAny<CancellationToken>()))
            .ThrowsAsync(connectException); // Setup ConnectAsync to throw
        mockFailingConnection.Setup(c => c.Abort()); // Needed for cleanup
        mockFailingConnection.Setup(c => c.Dispose()); // Needed for cleanup

        // Setup the factory to return *this specific* failing connection mock
        mockFactory.Setup(f => f.CreateConnection()).Returns(mockFailingConnection.Object);

        // Act & Assert
        ConnectionAttemptFailedException thrownException =
            await Assert.ThrowsExactlyAsync<ConnectionAttemptFailedException>(() =>
                client.ConnectAsync()
            );

        await Task.Delay(50); // Allow brief moment for background event processing

        Assert.AreEqual(connectException, thrownException.InnerException);
        await Task.WhenAny(disconnectedSignal.Task, Task.Delay(1000))
            .WaitAsync(TimeSpan.FromSeconds(2));

        CollectionAssert.AreEqual(
            s_expectedFailNoRetryLog, // Use the static readonly field
            eventLog.ToArray()
        );

        Assert.IsFalse(client.IsConnected);
        Assert.IsNotNull(disconnectedReason, "DisconnectedReason should not be null.");
        Assert.IsInstanceOfType<ObsWebSocketException>(disconnectedReason);
        Assert.IsInstanceOfType<ConnectionAttemptFailedException>(
            disconnectedReason.InnerException
        );
        Assert.AreEqual(connectException, disconnectedReason.InnerException!.InnerException);

        mockFactory.Verify(f => f.CreateConnection(), Times.Once);
        // Verify against the mock instance that was actually used
        mockFailingConnection.Verify(
            c => c.ConnectAsync(s_testServerUri, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task ConnectAsync_FailAllRetries_RaisesDisconnectedWithLastError()
    {
        // Arrange
        WebSocketException connectException = new("Server unavailable");
        const int maxAttempts = 2;
        (ObsWebSocketClient? client, _, _, Mock<IWebSocketConnectionFactory>? mockFactory) =
            BuildMockedInfrastructure(opts =>
            {
                opts.AutoReconnectEnabled = true;
                opts.MaxReconnectAttempts = maxAttempts;
                opts.InitialReconnectDelayMs = 5; // Short delay for test speed
            });

        List<string> eventLog = [];
        Exception? disconnectedReason = null;
        TaskCompletionSource disconnectedSignal = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        client.Connecting += (_, e) => eventLog.Add($"Connecting_{e.AttemptNumber}");
        client.ConnectionFailed += (_, e) => eventLog.Add($"Failed_{e.AttemptNumber}");
        client.Disconnected += (_, e) =>
        {
            eventLog.Add("Disconnected");
            disconnectedReason = e.ReasonException;
            disconnectedSignal.TrySetResult();
        };

        // Setup the factory to return failing connections
        mockFactory
            .Setup(f => f.CreateConnection())
            .Returns(() =>
            {
                Mock<IWebSocketConnection> mockConnFailing = new(MockBehavior.Strict);
                mockConnFailing.SetupGet(c => c.State).Returns(WebSocketState.None);
                mockConnFailing.SetupGet(c => c.Options).Returns(new ClientWebSocket().Options);
                mockConnFailing.SetupGet(c => c.SubProtocol).Returns("obswebsocket.json");
                mockConnFailing.SetupGet(c => c.CloseStatus).Returns((WebSocketCloseStatus?)null);
                mockConnFailing.SetupGet(c => c.CloseStatusDescription).Returns((string?)null);
                mockConnFailing
                    .Setup(c => c.ConnectAsync(s_testServerUri, It.IsAny<CancellationToken>()))
                    .ThrowsAsync(connectException);
                mockConnFailing.Setup(c => c.Abort());
                mockConnFailing.Setup(c => c.Dispose());
                return mockConnFailing.Object;
            });

        // Act & Assert
        ObsWebSocketException thrownException =
            await Assert.ThrowsExactlyAsync<ObsWebSocketException>(() => client.ConnectAsync());

        Assert.IsTrue(
            thrownException.Message.Contains($"Failed to connect after {maxAttempts} attempts")
        );
        Assert.IsInstanceOfType<ConnectionAttemptFailedException>(thrownException.InnerException);
        Assert.AreEqual(connectException, thrownException.InnerException!.InnerException);

        await Task.WhenAny(disconnectedSignal.Task, Task.Delay(1000))
            .WaitAsync(TimeSpan.FromSeconds(2));

        // Generate expected log dynamically based on maxAttempts
        List<string> expectedLog = [];
        for (int i = 1; i <= maxAttempts; i++)
        {
            expectedLog.Add($"Connecting_{i}");
            expectedLog.Add($"Failed_{i}");
        }

        expectedLog.Add("Disconnected");
        CollectionAssert.AreEqual(expectedLog, eventLog.ToArray());

        Assert.IsFalse(client.IsConnected);
        Assert.IsNotNull(disconnectedReason, "DisconnectedReason should not be null.");
        Assert.IsInstanceOfType<ObsWebSocketException>(disconnectedReason);
        Assert.AreEqual(thrownException.Message, disconnectedReason.Message);

        mockFactory.Verify(f => f.CreateConnection(), Times.Exactly(maxAttempts));
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task DisconnectAsync_DuringRetry_StopsRetriesAndDisconnectsGracefully()
    {
        // Arrange
        WebSocketException connectException = new("Server unavailable");
        (ObsWebSocketClient? client, _, _, Mock<IWebSocketConnectionFactory>? mockFactory) =
            BuildMockedInfrastructure(opts =>
            {
                opts.AutoReconnectEnabled = true;
                opts.MaxReconnectAttempts = 5; // Allow multiple retries
                opts.InitialReconnectDelayMs = 300; // Longer delay to allow disconnect call
            });

        List<string> eventLog = [];
        Exception? disconnectedReason = new("Placeholder"); // Start non-null for check later (Simplified 'new')
        TaskCompletionSource firstFailSignal = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        TaskCompletionSource disconnectedSignal = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        client.Connecting += (_, e) => eventLog.Add($"Connecting_{e.AttemptNumber}");
        client.ConnectionFailed += (_, e) =>
        {
            eventLog.Add($"Failed_{e.AttemptNumber}");
            if (e.AttemptNumber == 1)
            {
                firstFailSignal.TrySetResult(); // Signal after first failure
            }
        };
        client.Disconnected += (_, e) =>
        {
            eventLog.Add("Disconnected");
            disconnectedReason = e.ReasonException;
            disconnectedSignal.TrySetResult();
        };

        mockFactory
            .Setup(f => f.CreateConnection())
            .Returns(() =>
            {
                Mock<IWebSocketConnection> mockConnFailing = new(MockBehavior.Strict);
                mockConnFailing.SetupGet(c => c.State).Returns(WebSocketState.None);
                mockConnFailing.SetupGet(c => c.Options).Returns(new ClientWebSocket().Options);
                mockConnFailing.SetupGet(c => c.SubProtocol).Returns("obswebsocket.json");
                mockConnFailing.SetupGet(c => c.CloseStatus).Returns((WebSocketCloseStatus?)null);
                mockConnFailing.SetupGet(c => c.CloseStatusDescription).Returns((string?)null);
                mockConnFailing
                    .Setup(c => c.ConnectAsync(s_testServerUri, It.IsAny<CancellationToken>()))
                    .ThrowsAsync(connectException);
                mockConnFailing.Setup(c => c.Abort());
                mockConnFailing.Setup(c => c.Dispose());
                return mockConnFailing.Object;
            });

        // Act
        Task connectTask = client.ConnectAsync(); // Start connection attempts in background

        // Wait for the first connection attempt to fail
        bool firstFailed =
            await Task.WhenAny(firstFailSignal.Task, Task.Delay(TimeSpan.FromSeconds(2)))
            == firstFailSignal.Task;
        Assert.IsTrue(firstFailed, "First connection attempt did not fail within timeout.");

        // Request disconnect *while* the client is likely in the retry delay
        await client.DisconnectAsync();

        // Wait for the Disconnected event
        bool disconnected =
            await Task.WhenAny(disconnectedSignal.Task, Task.Delay(TimeSpan.FromSeconds(3)))
            == disconnectedSignal.Task;
        Assert.IsTrue(disconnected, "Disconnect event did not fire after calling DisconnectAsync.");

        // Allow original ConnectAsync task to complete/be observed (it should have been cancelled by DisconnectAsync)
        await connectTask
            .WaitAsync(TimeSpan.FromMilliseconds(100))
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); // Suppress potential TaskCanceledException

        // Assert
        Assert.IsFalse(client.IsConnected);
        Assert.IsTrue(eventLog.Contains("Disconnected"));
        Assert.IsNull(disconnectedReason, "Reason should be null for graceful DisconnectAsync.");

        // Verify only ONE connection attempt was made before DisconnectAsync cancelled the loop
        mockFactory.Verify(f => f.CreateConnection(), Times.Once);
        Assert.AreEqual(1, eventLog.Count(s => s.StartsWith("Connecting_")));
        Assert.AreEqual(1, eventLog.Count(s => s.StartsWith("Failed_")));
    }

    [TestMethod]
    [Timeout(3000)] // Slightly increased timeout
    public async Task ConnectAsync_InfiniteRetries_AttemptsMultipleTimes()
    {
        // Arrange
        const int minExpectedAttempts = 4;
        WebSocketException connectException = new("Network Error");
        using CancellationTokenSource cts = new(); // Token source to cancel the ConnectAsync call
        (ObsWebSocketClient? client, _, _, Mock<IWebSocketConnectionFactory>? mockFactory) =
            BuildMockedInfrastructure(opts =>
            {
                opts.AutoReconnectEnabled = true;
                opts.MaxReconnectAttempts = -1; // Infinite
                opts.InitialReconnectDelayMs = 10;
                opts.ReconnectBackoffMultiplier = 1.0; // Fixed delay for faster testing
            });

        int attemptCounter = 0;
        object counterLock = new();
        client.Connecting += (_, _) =>
        {
            lock (counterLock)
            {
                attemptCounter++;
            }
        };

        mockFactory
            .Setup(f => f.CreateConnection())
            .Returns(() =>
            {
                Mock<IWebSocketConnection> mockConnFailing = new(MockBehavior.Strict);
                mockConnFailing.SetupGet(c => c.State).Returns(WebSocketState.None);
                mockConnFailing.SetupGet(c => c.Options).Returns(new ClientWebSocket().Options);
                mockConnFailing.SetupGet(c => c.SubProtocol).Returns("obswebsocket.json");
                mockConnFailing.SetupGet(c => c.CloseStatus).Returns((WebSocketCloseStatus?)null);
                mockConnFailing.SetupGet(c => c.CloseStatusDescription).Returns((string?)null);
                mockConnFailing
                    .Setup(c => c.ConnectAsync(s_testServerUri, It.IsAny<CancellationToken>()))
                    .ThrowsAsync(connectException);
                mockConnFailing.Setup(c => c.Abort());
                mockConnFailing.Setup(c => c.Dispose());
                return mockConnFailing.Object;
            });

        // Act
        Task connectTask = client.ConnectAsync(cts.Token); // Pass the cancellation token

        // Wait until enough attempts have occurred or task completes
        Stopwatch sw = Stopwatch.StartNew();
        while (
            Volatile.Read(ref attemptCounter) < minExpectedAttempts
            && sw.ElapsedMilliseconds < 2500
            && !connectTask.IsCompleted
        )
        {
            await Task.Delay(25);
        }

        sw.Stop();

        // Cancel the operation only if the loop didn't complete unexpectedly
        if (!connectTask.IsCompleted)
        {
            Debug.WriteLine($"--> Cancelling token after {attemptCounter} attempts...");
            cts.Cancel();
        }
        else
        {
            // Log if task completed early
            Debug.WriteLine(
                $"--> ConnectAsync task completed before cancellation/minimum attempts. Status: {connectTask.Status}, Exception: {connectTask.Exception}"
            );
        }

        // Assert
        // Expect TaskCanceledException because we cancelled the token *passed into* ConnectAsync
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => connectTask);

        int finalAttemptCount = Volatile.Read(ref attemptCounter);
        Assert.IsTrue(
            finalAttemptCount >= minExpectedAttempts,
            $"Expected >= {minExpectedAttempts} attempts, got {finalAttemptCount}"
        );

        mockFactory.Verify(f => f.CreateConnection(), Times.AtLeast(minExpectedAttempts));
        Assert.IsFalse(client.IsConnected);
    }
}
