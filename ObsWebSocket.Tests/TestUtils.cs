using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Tests;

/// <summary>
/// Provides utility methods and shared resources for testing the ObsWebSocketClient.
/// </summary>
internal static class TestUtils
{
    /// <summary>
    /// Default JsonSerializerOptions configured similarly to the main library (Web defaults).
    /// Used for test data serialization.
    /// </summary>
    internal static readonly JsonSerializerOptions s_jsonSerializerOptions = new(
        JsonSerializerDefaults.Web
    );

    /// <summary>
    /// A shared instance of a null logger for convenience in tests where logging is not the focus.
    /// </summary>
    internal static readonly ILogger<ObsWebSocketClient> s_nullLogger =
        NullLogger<ObsWebSocketClient>.Instance;

    /// <summary>
    /// Builds the service provider and core mocked dependencies using strict mock behavior.
    /// Sets up default behaviors for mocks required for basic client operation and cleanup paths.
    /// Ensures the mocked IWebSocketMessageSerializer is correctly injected.
    /// </summary>
    /// <param name="configureOptions">Optional action to configure client options.</param>
    /// <param name="existingSerializerMock">Optional existing serializer mock to reuse.</param>
    /// <returns>A tuple containing the client, mocked connection, mocked serializer, and mocked factory.</returns>
    /// <remarks>
    /// Mocks use MockBehavior.Strict, requiring explicit setups in tests for any interaction not covered by these defaults.
    /// </remarks>
    internal static (
        ObsWebSocketClient client,
        Mock<IWebSocketConnection> mockConnection,
        Mock<IWebSocketMessageSerializer> mockSerializer,
        Mock<IWebSocketConnectionFactory> mockFactory
    ) BuildMockedClientInfrastructure(
        Action<ObsWebSocketClientOptions>? configureOptions = null,
        Mock<IWebSocketMessageSerializer>? existingSerializerMock = null
    )
    {
        ServiceCollection services = new();

        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Trace));

        // --- Create Mocks (Strict) ---
        Mock<IWebSocketConnection> mockConnection = new(MockBehavior.Strict);
        Mock<IWebSocketConnectionFactory> mockConnectionFactory = new(MockBehavior.Strict);
        Mock<IWebSocketMessageSerializer> mockSerializer =
            existingSerializerMock ?? new Mock<IWebSocketMessageSerializer>(MockBehavior.Strict);

        // --- Default Mock Setups (Strict Behavior Compliance) ---

        // IWebSocketConnectionFactory
        mockConnectionFactory.Setup(f => f.CreateConnection()).Returns(mockConnection.Object);

        // IWebSocketConnection (Defaults for basic operation and cleanup)
        mockConnection.SetupGet(c => c.State).Returns(WebSocketState.None);
        mockConnection.SetupGet(c => c.Options).Returns(new ClientWebSocket().Options);
        mockConnection.SetupGet(c => c.SubProtocol).Returns("obswebsocket.json");
        mockConnection.SetupGet(c => c.CloseStatus).Returns((WebSocketCloseStatus?)null);
        mockConnection.SetupGet(c => c.CloseStatusDescription).Returns((string?)null);
        mockConnection
            .Setup(c => c.ConnectAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockConnection
            .Setup(c => c.ReceiveAsync(It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        mockConnection
            .Setup(c =>
                c.SendAsync(
                    It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<WebSocketMessageType>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(ValueTask.CompletedTask);
        mockConnection
            .Setup(c =>
                c.CloseOutputAsync(
                    It.IsAny<WebSocketCloseStatus>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.CompletedTask);
        mockConnection
            .Setup(c =>
                c.CloseAsync(
                    It.IsAny<WebSocketCloseStatus>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.CompletedTask);
        mockConnection.Setup(c => c.Abort());
        mockConnection.Setup(c => c.Dispose());

        // IWebSocketMessageSerializer (Defaults)
        mockSerializer.SetupGet(s => s.ProtocolSubProtocol).Returns("obswebsocket.json");
        mockSerializer
            .Setup(s => s.DeserializeAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((object?)null);
        mockSerializer
            .Setup(s =>
                s.SerializeAsync(
                    It.IsAny<OutgoingMessage<IdentifyPayload>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([]);
        mockSerializer
            .Setup(s =>
                s.SerializeAsync(
                    It.IsAny<OutgoingMessage<ReidentifyPayload>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([]);
        mockSerializer
            .Setup(s =>
                s.SerializeAsync(
                    It.IsAny<OutgoingMessage<RequestPayload>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                (OutgoingMessage<RequestPayload> msg, CancellationToken _) =>
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg, s_jsonSerializerOptions))
            );
        mockSerializer
            .Setup(s =>
                s.SerializeAsync(
                    It.IsAny<OutgoingMessage<RequestBatchPayload>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                (OutgoingMessage<RequestBatchPayload> msg, CancellationToken _) =>
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg, s_jsonSerializerOptions))
            );

        // --- DI Registration ---
        services.AddOptions();
        services.AddSingleton(mockSerializer.Object);
        services.AddSingleton(mockConnectionFactory.Object);
        services.Configure<ObsWebSocketClientOptions>(opts =>
        {
            opts.ServerUri = new Uri("ws://testhost:4455");
            opts.AutoReconnectEnabled = false;
            opts.HandshakeTimeoutMs = 5000;
            opts.RequestTimeoutMs = 5000;
            opts.Format = SerializationFormat.Json;
            configureOptions?.Invoke(opts);
        });
        services.AddSingleton<ObsWebSocketClient>();

        // --- Build and Return ---
        ServiceProvider provider = services.BuildServiceProvider();
        ObsWebSocketClient client = provider.GetRequiredService<ObsWebSocketClient>();

        IWebSocketMessageSerializer? injectedSerializer =
            GetPrivateField<IWebSocketMessageSerializer>(client, "_serializer");
        return injectedSerializer != mockSerializer.Object
            ? throw new InvalidOperationException(
                "DI failed to inject the mocked IWebSocketMessageSerializer."
            )
            : ((
                ObsWebSocketClient client,
                Mock<IWebSocketConnection> mockConnection,
                Mock<IWebSocketMessageSerializer> mockSerializer,
                Mock<IWebSocketConnectionFactory> mockFactory
            ))
                (client, mockConnection, mockSerializer, mockConnectionFactory);
    }

    /// <summary>
    /// Creates a client and mocks using <see cref="BuildMockedClientInfrastructure"/>,
    /// then forcibly sets the client's internal state to Connected via reflection.
    /// </summary>
    /// <returns>A tuple containing the configured client and its mocks.</returns>
    internal static (
        ObsWebSocketClient client,
        Mock<IWebSocketMessageSerializer> mockSerializer,
        Mock<IWebSocketConnection> mockConnection
    ) SetupConnectedClientForceState()
    {
        (
            ObsWebSocketClient? client,
            Mock<IWebSocketConnection>? mockConnection,
            Mock<IWebSocketMessageSerializer>? mockSerializer,
            _
        ) = BuildMockedClientInfrastructure();

        SetPrivateField(client, "_webSocket", mockConnection.Object);
        SetPrivateField(client, "_connectionState", ConnectionState.Connected);
        SetPrivateProperty(client, "IsConnected", true);
        SetPrivateField(client, "_clientLifetimeCts", new CancellationTokenSource());

        mockConnection.SetupGet(c => c.State).Returns(WebSocketState.Open);

        TaskCompletionSource<ValueWebSocketReceiveResult> blockedReceiveTcs = new();
        mockConnection
            .Setup(c => c.ReceiveAsync(It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<ValueWebSocketReceiveResult>(blockedReceiveTcs.Task));

        return (client, mockSerializer, mockConnection);
    }

    /// <summary>
    /// Invokes the private ProcessIncomingMessage method on the client instance using reflection.
    /// </summary>
    internal static void InvokeProcessIncomingMessage(
        ObsWebSocketClient client,
        object messageObject
    )
    {
        MethodInfo? method =
            typeof(ObsWebSocketClient).GetMethod(
                "ProcessIncomingMessage",
                BindingFlags.NonPublic | BindingFlags.Instance
            ) ?? throw new MissingMethodException("ObsWebSocketClient", "ProcessIncomingMessage");
        try
        {
            method.Invoke(client, [messageObject]);
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    /// <summary>
    /// Simulates the arrival of a response message by completing the corresponding pending request TaskCompletionSource.
    /// </summary>
    internal static bool SimulateIncomingResponse(
        ObsWebSocketClient client,
        string requestId,
        object responsePayload
    )
    {
        ConcurrentDictionary<string, TaskCompletionSource<object>>? pendingRequests =
            GetPendingRequests(client);
        ConcurrentDictionary<string, TaskCompletionSource<object>>? pendingBatchRequests =
            GetPendingBatchRequests(client);

        if (pendingRequests?.TryGetValue(requestId, out TaskCompletionSource<object>? tcs) ?? false)
        {
            return tcs.TrySetResult(responsePayload);
        }

        if (
            pendingBatchRequests?.TryGetValue(requestId, out TaskCompletionSource<object>? batchTcs)
            ?? false
        )
        {
            return batchTcs.TrySetResult(responsePayload);
        }

        Console.WriteLine(
            $"Warning: SimulateIncomingResponse did not find pending request/batch ID: {requestId}"
        );
        return false;
    }

    /// <summary>
    /// Simulates a request failure by setting an exception on the corresponding pending request TaskCompletionSource.
    /// </summary>
    internal static bool SimulateIncomingRequestFailure(
        ObsWebSocketClient client,
        string requestId,
        Exception exception
    )
    {
        ConcurrentDictionary<string, TaskCompletionSource<object>>? pendingRequests =
            GetPendingRequests(client);
        ConcurrentDictionary<string, TaskCompletionSource<object>>? pendingBatchRequests =
            GetPendingBatchRequests(client);

        return (
                (
                    pendingRequests?.TryGetValue(requestId, out TaskCompletionSource<object>? tcs)
                    ?? false
                ) && tcs.TrySetException(exception)
            )
            || (
                (
                    pendingBatchRequests?.TryGetValue(
                        requestId,
                        out TaskCompletionSource<object>? batchTcs
                    ) ?? false
                ) && batchTcs.TrySetException(exception)
            );
    }

    /// <summary>
    /// Gets the value of a private field from an object using reflection.
    /// </summary>
    internal static T? GetPrivateField<T>(object obj, string fieldName)
        where T : class =>
        obj.GetType()
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(obj) as T;

    /// <summary>
    /// Sets the value of a private field on an object using reflection.
    /// </summary>
    internal static void SetPrivateField(object obj, string fieldName, object? value) =>
        obj.GetType()
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(obj, value);

    /// <summary>
    /// Sets the value of a property (public or non-public) on an object using reflection.
    /// </summary>
    internal static void SetPrivateProperty(object obj, string propertyName, object? value) =>
        obj.GetType()
            .GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            )
            ?.SetValue(obj, value);

    /// <summary>
    /// Gets the internal dictionary of pending requests using reflection.
    /// </summary>
    internal static ConcurrentDictionary<string, TaskCompletionSource<object>>? GetPendingRequests(
        ObsWebSocketClient client
    ) =>
        GetPrivateField<ConcurrentDictionary<string, TaskCompletionSource<object>>>(
            client,
            "_pendingRequests"
        );

    /// <summary>
    /// Gets the internal dictionary of pending batch requests using reflection.
    /// </summary>
    internal static ConcurrentDictionary<
        string,
        TaskCompletionSource<object>
    >? GetPendingBatchRequests(ObsWebSocketClient client) =>
        GetPrivateField<ConcurrentDictionary<string, TaskCompletionSource<object>>>(
            client,
            "_pendingBatchRequests"
        );

    /// <summary>
    /// Serializes an object to a JsonElement using default web options. Clones the element for safe use.
    /// </summary>
    internal static JsonElement? ToJsonElement(object? obj)
    {
        if (obj == null)
        {
            return null;
        }

        if (obj is JsonElement element)
        {
            return element.Clone();
        }

        using MemoryStream ms = new();
        using Utf8JsonWriter writer = new(ms);
        JsonSerializer.Serialize(writer, obj, s_jsonSerializerOptions);
        writer.Flush();
        ms.Position = 0;
        using JsonDocument doc = JsonDocument.Parse(ms);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Gets a delegate for a private instance method using reflection.
    /// </summary>
    internal static TDelegate? GetPrivateMethodDelegate<TDelegate>(object obj, string methodName)
        where TDelegate : Delegate
    {
        MethodInfo? method = obj.GetType()
            .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        return method != null
            ? (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), obj, method)
            : null;
    }
}
