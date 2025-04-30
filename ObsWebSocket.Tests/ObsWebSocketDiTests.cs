using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Tests;

/// <summary>
/// Contains unit tests specifically for the dependency injection setup
/// provided by <see cref="ObsWebSocketServiceCollectionExtensions"/>.
/// </summary>
[TestClass]
public class ObsWebSocketDiTests
{
    /// <summary>
    /// Creates a basic ServiceCollection with minimal required services (logging).
    /// </summary>
    private static ServiceCollection CreateServiceCollectionWithLogging()
    {
        ServiceCollection services = new();
        // Add minimal logging provider (NullLogger) for tests not verifying log output
        services.AddSingleton<ILoggerFactory, NullLoggerFactory>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return services;
    }

    /// <summary>
    /// Verifies that AddObsWebSocketClient registers all necessary services
    /// with the correct lifetimes (Singleton).
    /// </summary>
    [TestMethod]
    public void AddObsWebSocketClient_RegistersServicesCorrectly()
    {
        // Arrange
        ServiceCollection services = CreateServiceCollectionWithLogging();

        // Act
        services.AddObsWebSocketClient(); // Register the client and its dependencies
        ServiceProvider provider = services.BuildServiceProvider(); // Build the container

        // Assert
        // Verify core services are registered and resolvable
        Assert.IsNotNull(provider.GetService<ObsWebSocketClient>());
        Assert.IsNotNull(provider.GetService<IWebSocketMessageSerializer>());
        Assert.IsNotNull(provider.GetService<IWebSocketConnectionFactory>());
        Assert.IsNotNull(provider.GetService<IOptions<ObsWebSocketClientOptions>>());
        Assert.IsNotNull(provider.GetService<ILogger<ObsWebSocketClient>>()); // Verify logger is available

        // Verify concrete implementations are registered (as singletons)
        Assert.IsNotNull(provider.GetService<JsonMessageSerializer>());
        Assert.IsNotNull(provider.GetService<MsgPackMessageSerializer>());
        Assert.IsInstanceOfType<WebSocketConnectionFactory>(
            provider.GetRequiredService<IWebSocketConnectionFactory>()
        );

        // Verify singleton lifetimes by resolving multiple times
        ObsWebSocketClient? client1 = provider.GetService<ObsWebSocketClient>();
        ObsWebSocketClient? client2 = provider.GetService<ObsWebSocketClient>();
        Assert.AreSame(client1, client2, "ObsWebSocketClient should be a singleton.");

        JsonMessageSerializer? jsonSer1 = provider.GetService<JsonMessageSerializer>();
        JsonMessageSerializer? jsonSer2 = provider.GetService<JsonMessageSerializer>();
        Assert.AreSame(jsonSer1, jsonSer2, "JsonMessageSerializer should be a singleton.");

        MsgPackMessageSerializer? msgPackSer1 = provider.GetService<MsgPackMessageSerializer>();
        MsgPackMessageSerializer? msgPackSer2 = provider.GetService<MsgPackMessageSerializer>();
        Assert.AreSame(msgPackSer1, msgPackSer2, "MsgPackMessageSerializer should be a singleton.");
    }

    /// <summary>
    /// Verifies that when no format is specified, the JsonMessageSerializer is resolved
    /// as the default IWebSocketMessageSerializer.
    /// </summary>
    [TestMethod]
    public void AddObsWebSocketClient_DefaultFormat_ResolvesJsonSerializer()
    {
        // Arrange
        ServiceCollection services = CreateServiceCollectionWithLogging();
        services.AddObsWebSocketClient(); // Use default options
        ServiceProvider provider = services.BuildServiceProvider();

        // Act
        // Resolve the interface and the client itself
        IWebSocketMessageSerializer? resolvedSerializer =
            provider.GetService<IWebSocketMessageSerializer>();
        ObsWebSocketClient? resolvedClient = provider.GetService<ObsWebSocketClient>();

        // Assert
        // Check the resolved interface type
        Assert.IsNotNull(resolvedSerializer);
        Assert.IsInstanceOfType<JsonMessageSerializer>(resolvedSerializer);

        // Check the serializer injected into the client instance
        Assert.IsNotNull(resolvedClient);
        IWebSocketMessageSerializer? injectedSerializer =
            TestUtils.GetPrivateField<IWebSocketMessageSerializer>(resolvedClient, "_serializer");
        Assert.IsNotNull(injectedSerializer);
        Assert.IsInstanceOfType<JsonMessageSerializer>(injectedSerializer);
        Assert.AreEqual("obswebsocket.json", injectedSerializer.ProtocolSubProtocol);
    }

    /// <summary>
    /// Verifies that explicitly configuring the Json format resolves the JsonMessageSerializer.
    /// </summary>
    [TestMethod]
    public void AddObsWebSocketClient_JsonFormatConfigured_ResolvesJsonSerializer()
    {
        // Arrange
        ServiceCollection services = CreateServiceCollectionWithLogging();
        services.AddObsWebSocketClient(options => options.Format = SerializationFormat.Json); // Explicitly configure JSON
        ServiceProvider provider = services.BuildServiceProvider();

        // Act
        IWebSocketMessageSerializer? resolvedSerializer =
            provider.GetService<IWebSocketMessageSerializer>();
        ObsWebSocketClient? resolvedClient = provider.GetService<ObsWebSocketClient>();

        // Assert
        Assert.IsNotNull(resolvedSerializer);
        Assert.IsInstanceOfType<JsonMessageSerializer>(resolvedSerializer);
        Assert.IsNotNull(resolvedClient);
        IWebSocketMessageSerializer? injectedSerializer =
            TestUtils.GetPrivateField<IWebSocketMessageSerializer>(resolvedClient, "_serializer");
        Assert.IsNotNull(injectedSerializer);
        Assert.IsInstanceOfType<JsonMessageSerializer>(injectedSerializer);
        Assert.AreEqual("obswebsocket.json", injectedSerializer.ProtocolSubProtocol);
    }

    /// <summary>
    /// Verifies that configuring the MsgPack format resolves the MsgPackMessageSerializer.
    /// </summary>
    [TestMethod]
    public void AddObsWebSocketClient_MsgPackFormatConfigured_ResolvesMsgPackSerializer()
    {
        // Arrange
        ServiceCollection services = CreateServiceCollectionWithLogging();
        services.AddObsWebSocketClient(options => options.Format = SerializationFormat.MsgPack); // Configure MsgPack
        ServiceProvider provider = services.BuildServiceProvider();

        // Act
        IWebSocketMessageSerializer? resolvedSerializer =
            provider.GetService<IWebSocketMessageSerializer>();
        ObsWebSocketClient? resolvedClient = provider.GetService<ObsWebSocketClient>();

        // Assert
        Assert.IsNotNull(resolvedSerializer);
        Assert.IsInstanceOfType<MsgPackMessageSerializer>(resolvedSerializer);
        Assert.IsNotNull(resolvedClient);
        IWebSocketMessageSerializer? injectedSerializer =
            TestUtils.GetPrivateField<IWebSocketMessageSerializer>(resolvedClient, "_serializer");
        Assert.IsNotNull(injectedSerializer);
        Assert.IsInstanceOfType<MsgPackMessageSerializer>(injectedSerializer);
        Assert.AreEqual("obswebsocket.msgpack", injectedSerializer.ProtocolSubProtocol);
    }

    /// <summary>
    /// Verifies that configuring options via the AddObsWebSocketClient action correctly
    /// sets the values in the resolved ObsWebSocketClientOptions.
    /// </summary>
    [TestMethod]
    public void AddObsWebSocketClient_WithConfiguration_SetsOptions()
    {
        // Arrange
        ServiceCollection services = CreateServiceCollectionWithLogging();
        Uri testUri = new("ws://test-server:1234");
        string testPassword = "test-password";
        EventSubscription testSubs = EventSubscription.General | EventSubscription.Transitions; // Example: General | Transitions
        int testHandshakeTimeout = 9999;
        int testRequestTimeout = 8888;
        bool testAutoReconnect = false;
        int testReconnectDelay = 123;
        int testMaxAttempts = 99;
        double testBackoff = 1.5;
        int testMaxDelay = 9876;

        // Act
        // Configure all options via the action
        services.AddObsWebSocketClient(options =>
        {
            options.ServerUri = testUri;
            options.Password = testPassword;
            options.EventSubscriptions = (uint)testSubs;
            options.HandshakeTimeoutMs = testHandshakeTimeout;
            options.RequestTimeoutMs = testRequestTimeout;
            options.Format = SerializationFormat.MsgPack; // Test non-default format
            options.AutoReconnectEnabled = testAutoReconnect;
            options.InitialReconnectDelayMs = testReconnectDelay;
            options.MaxReconnectAttempts = testMaxAttempts;
            options.ReconnectBackoffMultiplier = testBackoff;
            options.MaxReconnectDelayMs = testMaxDelay;
        });
        ServiceProvider provider = services.BuildServiceProvider();
        IOptions<ObsWebSocketClientOptions>? resolvedOptions = provider.GetService<
            IOptions<ObsWebSocketClientOptions>
        >();

        // Assert
        Assert.IsNotNull(resolvedOptions?.Value, "Options should be resolved.");
        ObsWebSocketClientOptions optionsValue = resolvedOptions.Value;
        Assert.AreEqual(testUri, optionsValue.ServerUri);
        Assert.AreEqual(testPassword, optionsValue.Password);
        Assert.AreEqual((uint)testSubs, optionsValue.EventSubscriptions);
        Assert.AreEqual(testHandshakeTimeout, optionsValue.HandshakeTimeoutMs);
        Assert.AreEqual(testRequestTimeout, optionsValue.RequestTimeoutMs);
        Assert.AreEqual(SerializationFormat.MsgPack, optionsValue.Format);
        Assert.AreEqual(testAutoReconnect, optionsValue.AutoReconnectEnabled);
        Assert.AreEqual(testReconnectDelay, optionsValue.InitialReconnectDelayMs);
        Assert.AreEqual(testMaxAttempts, optionsValue.MaxReconnectAttempts);
        Assert.AreEqual(testBackoff, optionsValue.ReconnectBackoffMultiplier);
        Assert.AreEqual(testMaxDelay, optionsValue.MaxReconnectDelayMs);
    }

    /// <summary>
    /// Verifies that when no configuration action is provided, the resolved options
    /// contain the s_expectedFailNoRetryLog default values.
    /// </summary>
    [TestMethod]
    public void AddObsWebSocketClient_WithoutConfiguration_UsesDefaults()
    {
        // Arrange
        ServiceCollection services = CreateServiceCollectionWithLogging();

        // Act
        services.AddObsWebSocketClient(); // Call without configuration action
        ServiceProvider provider = services.BuildServiceProvider();
        IOptions<ObsWebSocketClientOptions>? resolvedOptions = provider.GetService<
            IOptions<ObsWebSocketClientOptions>
        >();

        // Assert
        Assert.IsNotNull(resolvedOptions?.Value);
        ObsWebSocketClientOptions optionsValue = resolvedOptions.Value;
        Assert.IsNull(optionsValue.ServerUri, "Default ServerUri should be null.");
        Assert.IsNull(optionsValue.Password, "Default Password should be null.");
        Assert.IsNull(
            optionsValue.EventSubscriptions,
            "Default EventSubscriptions should be null."
        );
        Assert.AreEqual(
            ObsWebSocketClient.DefaultHandshakeTimeoutMs,
            optionsValue.HandshakeTimeoutMs
        );
        Assert.AreEqual(ObsWebSocketClient.DefaultRequestTimeoutMs, optionsValue.RequestTimeoutMs);
        Assert.AreEqual(SerializationFormat.Json, optionsValue.Format); // Default format
        Assert.AreEqual(true, optionsValue.AutoReconnectEnabled); // Default auto-reconnect
        Assert.AreEqual(5000, optionsValue.InitialReconnectDelayMs);
        Assert.AreEqual(5, optionsValue.MaxReconnectAttempts);
        Assert.AreEqual(2.0, optionsValue.ReconnectBackoffMultiplier);
        Assert.AreEqual(60000, optionsValue.MaxReconnectDelayMs);
    }

    /// <summary>
    /// Verifies that attempting to connect without setting ServerUri in options throws ArgumentNullException.
    /// </summary>
    [TestMethod]
    [Timeout(1000)] // Short timeout as it should fail quickly
    public async Task ConnectAsync_WithOptions_RequiresServerUri()
    {
        // Arrange
        ServiceCollection services = CreateServiceCollectionWithLogging();
        // Configure *without* setting ServerUri
        services.AddObsWebSocketClient(opts =>
        {
            opts.Password = "abc";
        });
        ServiceProvider provider = services.BuildServiceProvider();
        ObsWebSocketClient client = provider.GetRequiredService<ObsWebSocketClient>();

        // Act & Assert
        // Expect ArgumentNullException when ConnectAsync is called without ServerUri
        ArgumentNullException ex = await Assert.ThrowsExceptionAsync<ArgumentNullException>(() =>
            client.ConnectAsync()
        );
        // Verify the exception parameter name points to the missing option
        Assert.AreEqual("ServerUri", ex.ParamName);
    }
}
