using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Common.FilterSettings;
using ObsWebSocket.Core.Protocol.Common.InputSettings;
using ObsWebSocket.Core.Protocol.Responses;
using ObsWebSocket.Core.Serialization;
using RequestStatus = ObsWebSocket.Core.Protocol.RequestStatus;

namespace ObsWebSocket.Tests;

// ── Consumer-defined type (NOT registered in the library context) ─────────────
// Represents what a consumer app would define to handle a custom or uncommon source.

internal sealed record TestConsumerSettings(
    [property: JsonPropertyName("custom_key")] string? CustomKey = null,
    [property: JsonPropertyName("custom_count")] int? CustomCount = null
);

[JsonSerializable(typeof(TestConsumerSettings))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
)]
internal sealed partial class TestConsumerSettingsJsonContext : JsonSerializerContext { }

// ─────────────────────────────────────────────────────────────────────────────

[TestClass]
public class TypedSettingsTests
{
    private const int TestTimeout = 5000;

    private static MsgPackMessageSerializer CreateMsgPackSerializer() =>
        new(NullLogger<MsgPackMessageSerializer>.Instance);

    // ── Section 1: Direct serialization (no client infrastructure) ────────────

    [TestMethod]
    public void BrowserSourceSettings_SerializeToJson_ProducesCorrectKeys()
    {
        JsonTypeInfo<BrowserSourceSettings> typeInfo = ObsWebSocketSettingsJsonContext
            .Default
            .BrowserSourceSettings;
        BrowserSourceSettings settings = new(
            Url: "https://example.com",
            Width: 1920,
            Height: 1080,
            FpsCustom: false,
            Fps: 30,
            Css: "body { margin: 0; }",
            RerouteAudio: true,
            WebpageControlLevel: 5,
            RestartWhenActive: true
        );

        JsonElement element = JsonSerializer.SerializeToElement(settings, typeInfo);

        Assert.AreEqual("https://example.com", element.GetProperty("url").GetString());
        Assert.AreEqual(1920, element.GetProperty("width").GetInt32());
        Assert.AreEqual(1080, element.GetProperty("height").GetInt32());
        Assert.IsFalse(element.GetProperty("fps_custom").GetBoolean());
        Assert.AreEqual(30, element.GetProperty("fps").GetInt32());
        Assert.AreEqual("body { margin: 0; }", element.GetProperty("css").GetString());
        Assert.IsTrue(element.GetProperty("reroute_audio").GetBoolean());
        Assert.AreEqual(5, element.GetProperty("webpage_control_level").GetInt32());
        Assert.IsTrue(element.GetProperty("restart_when_active").GetBoolean());
    }

    [TestMethod]
    public void BrowserSourceSettings_NullProperties_AreOmittedFromJson()
    {
        JsonTypeInfo<BrowserSourceSettings> typeInfo = ObsWebSocketSettingsJsonContext
            .Default
            .BrowserSourceSettings;
        BrowserSourceSettings settings = new(Url: "https://example.com"); // only Url set

        JsonElement element = JsonSerializer.SerializeToElement(settings, typeInfo);

        Assert.AreEqual("https://example.com", element.GetProperty("url").GetString());
        Assert.IsFalse(element.TryGetProperty("width", out _), "Null width should be absent");
        Assert.IsFalse(element.TryGetProperty("height", out _), "Null height should be absent");
        Assert.IsFalse(
            element.TryGetProperty("fps_custom", out _),
            "Null fps_custom should be absent"
        );
        Assert.IsFalse(element.TryGetProperty("css", out _), "Null css should be absent");
        Assert.IsFalse(
            element.TryGetProperty("reroute_audio", out _),
            "Null reroute_audio should be absent"
        );
    }

    [TestMethod]
    public void GainFilterSettings_SerializeToJson_ProducesCorrectKey()
    {
        JsonTypeInfo<GainFilterSettings> typeInfo = ObsWebSocketSettingsJsonContext
            .Default
            .GainFilterSettings;
        GainFilterSettings settings = new(Db: -6.0);

        JsonElement element = JsonSerializer.SerializeToElement(settings, typeInfo);

        Assert.AreEqual(-6.0, element.GetProperty("db").GetDouble(), 0.0001d);
        Assert.AreEqual(1, element.EnumerateObject().Count(), "Only 'db' key should be present");
    }

    [TestMethod]
    public void ConsumerSettings_ExplicitTypeInfo_SerializesCorrectly()
    {
        JsonTypeInfo<TestConsumerSettings> typeInfo = TestConsumerSettingsJsonContext
            .Default
            .TestConsumerSettings;
        TestConsumerSettings settings = new(CustomKey: "hello", CustomCount: 42);

        JsonElement element = JsonSerializer.SerializeToElement(settings, typeInfo);

        Assert.AreEqual("hello", element.GetProperty("custom_key").GetString());
        Assert.AreEqual(42, element.GetProperty("custom_count").GetInt32());
    }

    [TestMethod]
    public void BrowserSourceSettings_MsgPackRoundtrip_ThroughGetInputSettingsResponse()
    {
        JsonTypeInfo<BrowserSourceSettings> typeInfo = ObsWebSocketSettingsJsonContext
            .Default
            .BrowserSourceSettings;
        BrowserSourceSettings original = new(
            Url: "https://example.com",
            Width: 1920,
            Height: 1080,
            FpsCustom: false,
            Fps: 30,
            RerouteAudio: true,
            WebpageControlLevel: 5
        );

        JsonElement settingsElement = JsonSerializer.SerializeToElement(original, typeInfo);
        GetInputSettingsResponseData response = new(
            inputSettings: settingsElement,
            inputKind: "browser_source"
        );

        byte[] bytes = MessagePackSerializer.Serialize(
            response,
            MsgPackMessageSerializer.s_msgPackOptions
        );
        GetInputSettingsResponseData? roundTripped = CreateMsgPackSerializer()
            .DeserializePayload<GetInputSettingsResponseData>(new ReadOnlyMemory<byte>(bytes));

        Assert.IsNotNull(roundTripped);
        Assert.IsTrue(roundTripped.InputSettings.HasValue);

        BrowserSourceSettings? result = roundTripped.InputSettings.Value.Deserialize(typeInfo);
        Assert.IsNotNull(result);
        Assert.AreEqual("https://example.com", result.Url);
        Assert.AreEqual(1920, result.Width);
        Assert.AreEqual(1080, result.Height);
        Assert.IsFalse(result.FpsCustom, "false bool? should survive MsgPack roundtrip");
        Assert.AreEqual(30, result.Fps);
        Assert.IsTrue(result.RerouteAudio);
        Assert.AreEqual(5, result.WebpageControlLevel);
    }

    [TestMethod]
    public void GainFilterSettings_MsgPackRoundtrip_ThroughGetSourceFilterResponse()
    {
        JsonTypeInfo<GainFilterSettings> typeInfo = ObsWebSocketSettingsJsonContext
            .Default
            .GainFilterSettings;
        GainFilterSettings original = new(Db: -3.5);

        JsonElement settingsElement = JsonSerializer.SerializeToElement(original, typeInfo);
        GetSourceFilterResponseData response = new(
            filterSettings: settingsElement,
            filterEnabled: true,
            filterIndex: 0,
            filterKind: "gain_filter"
        );

        byte[] bytes = MessagePackSerializer.Serialize(
            response,
            MsgPackMessageSerializer.s_msgPackOptions
        );
        GetSourceFilterResponseData? roundTripped = CreateMsgPackSerializer()
            .DeserializePayload<GetSourceFilterResponseData>(new ReadOnlyMemory<byte>(bytes));

        Assert.IsNotNull(roundTripped);
        Assert.IsTrue(roundTripped.FilterSettings.HasValue);

        GainFilterSettings? result = roundTripped.FilterSettings.Value.Deserialize(typeInfo);
        Assert.IsNotNull(result);
        Assert.AreEqual(-3.5, result.Db!.Value, 0.0001d);
    }

    // ── Section 2: SetInputSettingsAsync helpers ──────────────────────────────

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SetInputSettingsAsync_LibraryType_SendsCorrectSettingsElement()
    {
        (
            ObsWebSocketClient client,
            Mock<IWebSocketMessageSerializer> mockSerializer,
            Mock<IWebSocketConnection> mockConnection
        ) = TestUtils.SetupConnectedClientForceState();
        _ = mockSerializer
            .Setup(s => s.DeserializePayload<object>(It.IsAny<object>()))
            .Returns((object?)null);

        BrowserSourceSettings settings = new(Url: "https://test.com", Width: 1280, Height: 720);
        JsonElement? capturedSettings = null;

        _ = mockConnection
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
                    WebSocketMessageType _,
                    bool _,
                    CancellationToken _
                ) =>
                {
                    OutgoingMessage<RequestPayload>? msg = JsonSerializer.Deserialize<
                        OutgoingMessage<RequestPayload>
                    >(buffer.Span, TestUtils.s_jsonSerializerOptions);
                    if (msg?.D?.RequestType == "SetInputSettings")
                    {
                        string id = msg.D.RequestId!;
                        capturedSettings = msg.D.RequestData?.GetProperty("inputSettings");
                        _ = TestUtils.SimulateIncomingResponse(
                            client,
                            id,
                            new RequestResponsePayload<object>(
                                RequestType: "SetInputSettings",
                                RequestId: id,
                                RequestStatus: new RequestStatus(
                                    Result: true,
                                    Code: (int)Core.Protocol.Generated.RequestStatusCode.Success
                                ),
                                ResponseData: null
                            )
                        );
                    }
                }
            )
            .Returns(ValueTask.CompletedTask);

        await client.Inputs.SetInputSettingsAsync("TestSource", settings, overlay: true);

        Assert.IsNotNull(capturedSettings, "Settings element should have been sent");
        Assert.AreEqual("https://test.com", capturedSettings.Value.GetProperty("url").GetString());
        Assert.AreEqual(1280, capturedSettings.Value.GetProperty("width").GetInt32());
        Assert.AreEqual(720, capturedSettings.Value.GetProperty("height").GetInt32());
        Assert.IsFalse(
            capturedSettings.Value.TryGetProperty("fps_custom", out _),
            "Null props should be absent"
        );
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SetInputSettingsAsync_ConsumerType_WithExplicitTypeInfo_SendsCorrectSettingsElement()
    {
        (
            ObsWebSocketClient client,
            Mock<IWebSocketMessageSerializer> mockSerializer,
            Mock<IWebSocketConnection> mockConnection
        ) = TestUtils.SetupConnectedClientForceState();
        _ = mockSerializer
            .Setup(s => s.DeserializePayload<object>(It.IsAny<object>()))
            .Returns((object?)null);

        TestConsumerSettings settings = new(CustomKey: "abc", CustomCount: 99);
        JsonTypeInfo<TestConsumerSettings> typeInfo = TestConsumerSettingsJsonContext
            .Default
            .TestConsumerSettings;
        JsonElement? capturedSettings = null;

        _ = mockConnection
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
                    WebSocketMessageType _,
                    bool _,
                    CancellationToken _
                ) =>
                {
                    OutgoingMessage<RequestPayload>? msg = JsonSerializer.Deserialize<
                        OutgoingMessage<RequestPayload>
                    >(buffer.Span, TestUtils.s_jsonSerializerOptions);
                    if (msg?.D?.RequestType == "SetInputSettings")
                    {
                        string id = msg.D.RequestId!;
                        capturedSettings = msg.D.RequestData?.GetProperty("inputSettings");
                        _ = TestUtils.SimulateIncomingResponse(
                            client,
                            id,
                            new RequestResponsePayload<object>(
                                RequestType: "SetInputSettings",
                                RequestId: id,
                                RequestStatus: new RequestStatus(
                                    Result: true,
                                    Code: (int)Core.Protocol.Generated.RequestStatusCode.Success
                                ),
                                ResponseData: null
                            )
                        );
                    }
                }
            )
            .Returns(ValueTask.CompletedTask);

        await client.Inputs.SetInputSettingsAsync("TestSource", settings, typeInfo, overlay: true);

        Assert.IsNotNull(capturedSettings);
        Assert.AreEqual("abc", capturedSettings.Value.GetProperty("custom_key").GetString());
        Assert.AreEqual(99, capturedSettings.Value.GetProperty("custom_count").GetInt32());
    }

    [TestMethod]
    public async Task SetInputSettingsAsync_UnregisteredType_ThrowsObsWebSocketException()
    {
        (ObsWebSocketClient client, _, _) = TestUtils.SetupConnectedClientForceState();

        _ = await Assert.ThrowsExactlyAsync<ObsWebSocketException>(() =>
            client.Inputs.SetInputSettingsAsync(
                "TestSource",
                new TestConsumerSettings(),
                overlay: true
            )
        );
    }

    // ── Section 3: GetInputSettingsAsync helpers ──────────────────────────────

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetInputSettingsAsync_LibraryType_DeserializesCorrectly()
    {
        (
            ObsWebSocketClient client,
            Mock<IWebSocketMessageSerializer> mockSerializer,
            Mock<IWebSocketConnection> mockConnection
        ) = TestUtils.SetupConnectedClientForceState();

        BrowserSourceSettings expected = new(
            Url: "https://obs.test",
            Width: 1920,
            Height: 1080,
            RerouteAudio: true
        );
        JsonTypeInfo<BrowserSourceSettings> typeInfo = ObsWebSocketSettingsJsonContext
            .Default
            .BrowserSourceSettings;
        JsonElement settingsElement = JsonSerializer.SerializeToElement(expected, typeInfo);
        GetInputSettingsResponseData responseDto = new(
            inputSettings: settingsElement,
            inputKind: "browser_source"
        );

        _ = mockConnection
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
                    WebSocketMessageType _,
                    bool _,
                    CancellationToken _
                ) =>
                {
                    OutgoingMessage<RequestPayload>? msg = JsonSerializer.Deserialize<
                        OutgoingMessage<RequestPayload>
                    >(buffer.Span, TestUtils.s_jsonSerializerOptions);
                    if (msg?.D?.RequestType == "GetInputSettings")
                    {
                        JsonElement rawPayload = TestUtils.ToJsonElement(responseDto)!.Value;
                        _ = TestUtils.SimulateIncomingResponse(
                            client,
                            msg.D.RequestId!,
                            new RequestResponsePayload<object>(
                                RequestType: "GetInputSettings",
                                RequestId: msg.D.RequestId!,
                                RequestStatus: new RequestStatus(
                                    Result: true,
                                    Code: (int)Core.Protocol.Generated.RequestStatusCode.Success
                                ),
                                ResponseData: rawPayload
                            )
                        );
                    }
                }
            )
            .Returns(ValueTask.CompletedTask);

        _ = mockSerializer
            .Setup(s => s.DeserializePayload<GetInputSettingsResponseData>(It.IsAny<object>()))
            .Returns(responseDto);

        BrowserSourceSettings? result =
            await client.Inputs.GetInputSettingsAsync<BrowserSourceSettings>("TestInput");

        Assert.IsNotNull(result);
        Assert.AreEqual("https://obs.test", result.Url);
        Assert.AreEqual(1920, result.Width);
        Assert.AreEqual(1080, result.Height);
        Assert.IsTrue(result.RerouteAudio);
        Assert.IsNull(result.FpsCustom, "Unset props should deserialize as null");
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetInputSettingsAsync_ConsumerType_WithExplicitTypeInfo_DeserializesCorrectly()
    {
        (
            ObsWebSocketClient client,
            Mock<IWebSocketMessageSerializer> mockSerializer,
            Mock<IWebSocketConnection> mockConnection
        ) = TestUtils.SetupConnectedClientForceState();

        TestConsumerSettings expected = new(CustomKey: "xyz", CustomCount: 7);
        JsonTypeInfo<TestConsumerSettings> typeInfo = TestConsumerSettingsJsonContext
            .Default
            .TestConsumerSettings;
        JsonElement settingsElement = JsonSerializer.SerializeToElement(expected, typeInfo);
        GetInputSettingsResponseData responseDto = new(
            inputSettings: settingsElement,
            inputKind: "custom_source"
        );

        _ = mockConnection
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
                    WebSocketMessageType _,
                    bool _,
                    CancellationToken _
                ) =>
                {
                    OutgoingMessage<RequestPayload>? msg = JsonSerializer.Deserialize<
                        OutgoingMessage<RequestPayload>
                    >(buffer.Span, TestUtils.s_jsonSerializerOptions);
                    if (msg?.D?.RequestType == "GetInputSettings")
                    {
                        JsonElement rawPayload = TestUtils.ToJsonElement(responseDto)!.Value;
                        _ = TestUtils.SimulateIncomingResponse(
                            client,
                            msg.D.RequestId!,
                            new RequestResponsePayload<object>(
                                RequestType: "GetInputSettings",
                                RequestId: msg.D.RequestId!,
                                RequestStatus: new RequestStatus(
                                    Result: true,
                                    Code: (int)Core.Protocol.Generated.RequestStatusCode.Success
                                ),
                                ResponseData: rawPayload
                            )
                        );
                    }
                }
            )
            .Returns(ValueTask.CompletedTask);

        _ = mockSerializer
            .Setup(s => s.DeserializePayload<GetInputSettingsResponseData>(It.IsAny<object>()))
            .Returns(responseDto);

        TestConsumerSettings? result = await client.Inputs.GetInputSettingsAsync(
            "TestInput",
            typeInfo
        );

        Assert.IsNotNull(result);
        Assert.AreEqual("xyz", result.CustomKey);
        Assert.AreEqual(7, result.CustomCount);
    }

    // ── Section 4: SetSourceFilterSettingsAsync helpers ───────────────────────

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SetSourceFilterSettingsAsync_LibraryType_SendsCorrectSettingsElement()
    {
        (
            ObsWebSocketClient client,
            Mock<IWebSocketMessageSerializer> mockSerializer,
            Mock<IWebSocketConnection> mockConnection
        ) = TestUtils.SetupConnectedClientForceState();
        _ = mockSerializer
            .Setup(s => s.DeserializePayload<object>(It.IsAny<object>()))
            .Returns((object?)null);

        GainFilterSettings settings = new(Db: -12.0);
        JsonElement? capturedSettings = null;

        _ = mockConnection
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
                    WebSocketMessageType _,
                    bool _,
                    CancellationToken _
                ) =>
                {
                    OutgoingMessage<RequestPayload>? msg = JsonSerializer.Deserialize<
                        OutgoingMessage<RequestPayload>
                    >(buffer.Span, TestUtils.s_jsonSerializerOptions);
                    if (msg?.D?.RequestType == "SetSourceFilterSettings")
                    {
                        string id = msg.D.RequestId!;
                        capturedSettings = msg.D.RequestData?.GetProperty("filterSettings");
                        _ = TestUtils.SimulateIncomingResponse(
                            client,
                            id,
                            new RequestResponsePayload<object>(
                                RequestType: "SetSourceFilterSettings",
                                RequestId: id,
                                RequestStatus: new RequestStatus(
                                    Result: true,
                                    Code: (int)Core.Protocol.Generated.RequestStatusCode.Success
                                ),
                                ResponseData: null
                            )
                        );
                    }
                }
            )
            .Returns(ValueTask.CompletedTask);

        await client.Filters.SetSourceFilterSettingsAsync(
            "AudioSource",
            "Gain",
            settings,
            overlay: true
        );

        Assert.IsNotNull(capturedSettings);
        Assert.AreEqual(-12.0, capturedSettings.Value.GetProperty("db").GetDouble(), 0.0001d);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task SetSourceFilterSettingsAsync_ConsumerType_WithExplicitTypeInfo_SendsCorrectSettingsElement()
    {
        (
            ObsWebSocketClient client,
            Mock<IWebSocketMessageSerializer> mockSerializer,
            Mock<IWebSocketConnection> mockConnection
        ) = TestUtils.SetupConnectedClientForceState();
        _ = mockSerializer
            .Setup(s => s.DeserializePayload<object>(It.IsAny<object>()))
            .Returns((object?)null);

        TestConsumerSettings settings = new(CustomKey: "filter-val");
        JsonTypeInfo<TestConsumerSettings> typeInfo = TestConsumerSettingsJsonContext
            .Default
            .TestConsumerSettings;
        JsonElement? capturedSettings = null;

        _ = mockConnection
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
                    WebSocketMessageType _,
                    bool _,
                    CancellationToken _
                ) =>
                {
                    OutgoingMessage<RequestPayload>? msg = JsonSerializer.Deserialize<
                        OutgoingMessage<RequestPayload>
                    >(buffer.Span, TestUtils.s_jsonSerializerOptions);
                    if (msg?.D?.RequestType == "SetSourceFilterSettings")
                    {
                        string id = msg.D.RequestId!;
                        capturedSettings = msg.D.RequestData?.GetProperty("filterSettings");
                        _ = TestUtils.SimulateIncomingResponse(
                            client,
                            id,
                            new RequestResponsePayload<object>(
                                RequestType: "SetSourceFilterSettings",
                                RequestId: id,
                                RequestStatus: new RequestStatus(
                                    Result: true,
                                    Code: (int)Core.Protocol.Generated.RequestStatusCode.Success
                                ),
                                ResponseData: null
                            )
                        );
                    }
                }
            )
            .Returns(ValueTask.CompletedTask);

        await client.Filters.SetSourceFilterSettingsAsync(
            "AudioSource",
            "CustomFilter",
            settings,
            typeInfo,
            overlay: true
        );

        Assert.IsNotNull(capturedSettings);
        Assert.AreEqual("filter-val", capturedSettings.Value.GetProperty("custom_key").GetString());
        Assert.IsFalse(
            capturedSettings.Value.TryGetProperty("custom_count", out _),
            "Null props should be absent"
        );
    }

    // ── Section 5: GetSourceFilterSettingsAsync helpers ───────────────────────

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetSourceFilterSettingsAsync_LibraryType_DeserializesCorrectly()
    {
        (
            ObsWebSocketClient client,
            Mock<IWebSocketMessageSerializer> mockSerializer,
            Mock<IWebSocketConnection> mockConnection
        ) = TestUtils.SetupConnectedClientForceState();

        GainFilterSettings expected = new(Db: -6.0);
        JsonTypeInfo<GainFilterSettings> typeInfo = ObsWebSocketSettingsJsonContext
            .Default
            .GainFilterSettings;
        JsonElement settingsElement = JsonSerializer.SerializeToElement(expected, typeInfo);
        GetSourceFilterResponseData responseDto = new(
            filterSettings: settingsElement,
            filterEnabled: true,
            filterIndex: 0,
            filterKind: "gain_filter"
        );

        _ = mockConnection
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
                    WebSocketMessageType _,
                    bool _,
                    CancellationToken _
                ) =>
                {
                    OutgoingMessage<RequestPayload>? msg = JsonSerializer.Deserialize<
                        OutgoingMessage<RequestPayload>
                    >(buffer.Span, TestUtils.s_jsonSerializerOptions);
                    if (msg?.D?.RequestType == "GetSourceFilter")
                    {
                        JsonElement rawPayload = TestUtils.ToJsonElement(responseDto)!.Value;
                        _ = TestUtils.SimulateIncomingResponse(
                            client,
                            msg.D.RequestId!,
                            new RequestResponsePayload<object>(
                                RequestType: "GetSourceFilter",
                                RequestId: msg.D.RequestId!,
                                RequestStatus: new RequestStatus(
                                    Result: true,
                                    Code: (int)Core.Protocol.Generated.RequestStatusCode.Success
                                ),
                                ResponseData: rawPayload
                            )
                        );
                    }
                }
            )
            .Returns(ValueTask.CompletedTask);

        _ = mockSerializer
            .Setup(s => s.DeserializePayload<GetSourceFilterResponseData>(It.IsAny<object>()))
            .Returns(responseDto);

        GainFilterSettings? result =
            await client.Filters.GetSourceFilterSettingsAsync<GainFilterSettings>(
                "AudioSource",
                "Gain"
            );

        Assert.IsNotNull(result);
        Assert.AreEqual(-6.0, result.Db!.Value, 0.0001d);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task GetSourceFilterSettingsAsync_ConsumerType_WithExplicitTypeInfo_DeserializesCorrectly()
    {
        (
            ObsWebSocketClient client,
            Mock<IWebSocketMessageSerializer> mockSerializer,
            Mock<IWebSocketConnection> mockConnection
        ) = TestUtils.SetupConnectedClientForceState();

        TestConsumerSettings expected = new(CustomKey: "my-filter", CustomCount: 3);
        JsonTypeInfo<TestConsumerSettings> typeInfo = TestConsumerSettingsJsonContext
            .Default
            .TestConsumerSettings;
        JsonElement settingsElement = JsonSerializer.SerializeToElement(expected, typeInfo);
        GetSourceFilterResponseData responseDto = new(
            filterSettings: settingsElement,
            filterEnabled: true,
            filterIndex: 0,
            filterKind: "custom_filter"
        );

        _ = mockConnection
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
                    WebSocketMessageType _,
                    bool _,
                    CancellationToken _
                ) =>
                {
                    OutgoingMessage<RequestPayload>? msg = JsonSerializer.Deserialize<
                        OutgoingMessage<RequestPayload>
                    >(buffer.Span, TestUtils.s_jsonSerializerOptions);
                    if (msg?.D?.RequestType == "GetSourceFilter")
                    {
                        JsonElement rawPayload = TestUtils.ToJsonElement(responseDto)!.Value;
                        _ = TestUtils.SimulateIncomingResponse(
                            client,
                            msg.D.RequestId!,
                            new RequestResponsePayload<object>(
                                RequestType: "GetSourceFilter",
                                RequestId: msg.D.RequestId!,
                                RequestStatus: new RequestStatus(
                                    Result: true,
                                    Code: (int)Core.Protocol.Generated.RequestStatusCode.Success
                                ),
                                ResponseData: rawPayload
                            )
                        );
                    }
                }
            )
            .Returns(ValueTask.CompletedTask);

        _ = mockSerializer
            .Setup(s => s.DeserializePayload<GetSourceFilterResponseData>(It.IsAny<object>()))
            .Returns(responseDto);

        TestConsumerSettings? result = await client.Filters.GetSourceFilterSettingsAsync(
            "AudioSource",
            "CustomFilter",
            typeInfo
        );

        Assert.IsNotNull(result);
        Assert.AreEqual("my-filter", result.CustomKey);
        Assert.AreEqual(3, result.CustomCount);
    }

    // ── Section 6: CreateInputAsync helpers ───────────────────────────────────

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task CreateInputAsync_LibraryType_SendsCorrectSettingsElement()
    {
        (
            ObsWebSocketClient client,
            Mock<IWebSocketMessageSerializer> mockSerializer,
            Mock<IWebSocketConnection> mockConnection
        ) = TestUtils.SetupConnectedClientForceState();

        BrowserSourceSettings settings = new(Url: "https://create.test", Width: 800, Height: 600);
        JsonElement? capturedSettings = null;
        CreateInputResponseData responseDto = new(sceneItemId: 42);

        _ = mockConnection
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
                    WebSocketMessageType _,
                    bool _,
                    CancellationToken _
                ) =>
                {
                    OutgoingMessage<RequestPayload>? msg = JsonSerializer.Deserialize<
                        OutgoingMessage<RequestPayload>
                    >(buffer.Span, TestUtils.s_jsonSerializerOptions);
                    if (msg?.D?.RequestType == "CreateInput")
                    {
                        string id = msg.D.RequestId!;
                        capturedSettings = msg.D.RequestData?.GetProperty("inputSettings");
                        JsonElement rawPayload = TestUtils.ToJsonElement(responseDto)!.Value;
                        _ = TestUtils.SimulateIncomingResponse(
                            client,
                            id,
                            new RequestResponsePayload<object>(
                                RequestType: "CreateInput",
                                RequestId: id,
                                RequestStatus: new RequestStatus(
                                    Result: true,
                                    Code: (int)Core.Protocol.Generated.RequestStatusCode.Success
                                ),
                                ResponseData: rawPayload
                            )
                        );
                    }
                }
            )
            .Returns(ValueTask.CompletedTask);

        _ = mockSerializer
            .Setup(s => s.DeserializePayload<CreateInputResponseData>(It.IsAny<object>()))
            .Returns(responseDto);

        CreateInputResponseData? result = await client.Inputs.CreateInputAsync(
            inputKind: "browser_source",
            inputName: "New Browser",
            settings: settings,
            sceneName: "Scene A",
            sceneItemEnabled: true
        );

        Assert.IsNotNull(result);
        Assert.AreEqual(42, result.SceneItemId);
        Assert.IsNotNull(capturedSettings);
        Assert.AreEqual(
            "https://create.test",
            capturedSettings.Value.GetProperty("url").GetString()
        );
        Assert.AreEqual(800, capturedSettings.Value.GetProperty("width").GetInt32());
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task CreateInputAsync_ConsumerType_WithExplicitTypeInfo_SendsCorrectSettingsElement()
    {
        (
            ObsWebSocketClient client,
            Mock<IWebSocketMessageSerializer> mockSerializer,
            Mock<IWebSocketConnection> mockConnection
        ) = TestUtils.SetupConnectedClientForceState();

        TestConsumerSettings settings = new(CustomKey: "my-input", CustomCount: 5);
        JsonTypeInfo<TestConsumerSettings> typeInfo = TestConsumerSettingsJsonContext
            .Default
            .TestConsumerSettings;
        JsonElement? capturedSettings = null;
        CreateInputResponseData responseDto = new(sceneItemId: 7);

        _ = mockConnection
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
                    WebSocketMessageType _,
                    bool _,
                    CancellationToken _
                ) =>
                {
                    OutgoingMessage<RequestPayload>? msg = JsonSerializer.Deserialize<
                        OutgoingMessage<RequestPayload>
                    >(buffer.Span, TestUtils.s_jsonSerializerOptions);
                    if (msg?.D?.RequestType == "CreateInput")
                    {
                        string id = msg.D.RequestId!;
                        capturedSettings = msg.D.RequestData?.GetProperty("inputSettings");
                        JsonElement rawPayload = TestUtils.ToJsonElement(responseDto)!.Value;
                        _ = TestUtils.SimulateIncomingResponse(
                            client,
                            id,
                            new RequestResponsePayload<object>(
                                RequestType: "CreateInput",
                                RequestId: id,
                                RequestStatus: new RequestStatus(
                                    Result: true,
                                    Code: (int)Core.Protocol.Generated.RequestStatusCode.Success
                                ),
                                ResponseData: rawPayload
                            )
                        );
                    }
                }
            )
            .Returns(ValueTask.CompletedTask);

        _ = mockSerializer
            .Setup(s => s.DeserializePayload<CreateInputResponseData>(It.IsAny<object>()))
            .Returns(responseDto);

        CreateInputResponseData? result = await client.Inputs.CreateInputAsync(
            inputKind: "custom_source",
            inputName: "My Custom",
            settings: settings,
            typeInfo: typeInfo
        );

        Assert.IsNotNull(result);
        Assert.AreEqual(7, result.SceneItemId);
        Assert.IsNotNull(capturedSettings);
        Assert.AreEqual("my-input", capturedSettings.Value.GetProperty("custom_key").GetString());
        Assert.AreEqual(5, capturedSettings.Value.GetProperty("custom_count").GetInt32());
    }

    // ── Section 7: CreateSourceFilterAsync helpers ────────────────────────────

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task CreateSourceFilterAsync_LibraryType_SendsCorrectSettingsElement()
    {
        (
            ObsWebSocketClient client,
            Mock<IWebSocketMessageSerializer> mockSerializer,
            Mock<IWebSocketConnection> mockConnection
        ) = TestUtils.SetupConnectedClientForceState();
        _ = mockSerializer
            .Setup(s => s.DeserializePayload<object>(It.IsAny<object>()))
            .Returns((object?)null);

        GainFilterSettings settings = new(Db: -3.0);
        JsonElement? capturedSettings = null;

        _ = mockConnection
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
                    WebSocketMessageType _,
                    bool _,
                    CancellationToken _
                ) =>
                {
                    OutgoingMessage<RequestPayload>? msg = JsonSerializer.Deserialize<
                        OutgoingMessage<RequestPayload>
                    >(buffer.Span, TestUtils.s_jsonSerializerOptions);
                    if (msg?.D?.RequestType == "CreateSourceFilter")
                    {
                        string id = msg.D.RequestId!;
                        capturedSettings = msg.D.RequestData?.GetProperty("filterSettings");
                        _ = TestUtils.SimulateIncomingResponse(
                            client,
                            id,
                            new RequestResponsePayload<object>(
                                RequestType: "CreateSourceFilter",
                                RequestId: id,
                                RequestStatus: new RequestStatus(
                                    Result: true,
                                    Code: (int)Core.Protocol.Generated.RequestStatusCode.Success
                                ),
                                ResponseData: null
                            )
                        );
                    }
                }
            )
            .Returns(ValueTask.CompletedTask);

        await client.Filters.CreateSourceFilterAsync(
            sourceName: "AudioSource",
            filterName: "My Gain",
            filterKind: "gain_filter",
            settings: settings
        );

        Assert.IsNotNull(capturedSettings);
        Assert.AreEqual(-3.0, capturedSettings.Value.GetProperty("db").GetDouble(), 0.0001d);
    }

    [TestMethod]
    [Timeout(TestTimeout)]
    public async Task CreateSourceFilterAsync_ConsumerType_WithExplicitTypeInfo_SendsCorrectSettingsElement()
    {
        (
            ObsWebSocketClient client,
            Mock<IWebSocketMessageSerializer> mockSerializer,
            Mock<IWebSocketConnection> mockConnection
        ) = TestUtils.SetupConnectedClientForceState();
        _ = mockSerializer
            .Setup(s => s.DeserializePayload<object>(It.IsAny<object>()))
            .Returns((object?)null);

        TestConsumerSettings settings = new(CustomKey: "my-filter", CustomCount: 1);
        JsonTypeInfo<TestConsumerSettings> typeInfo = TestConsumerSettingsJsonContext
            .Default
            .TestConsumerSettings;
        JsonElement? capturedSettings = null;

        _ = mockConnection
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
                    WebSocketMessageType _,
                    bool _,
                    CancellationToken _
                ) =>
                {
                    OutgoingMessage<RequestPayload>? msg = JsonSerializer.Deserialize<
                        OutgoingMessage<RequestPayload>
                    >(buffer.Span, TestUtils.s_jsonSerializerOptions);
                    if (msg?.D?.RequestType == "CreateSourceFilter")
                    {
                        string id = msg.D.RequestId!;
                        capturedSettings = msg.D.RequestData?.GetProperty("filterSettings");
                        _ = TestUtils.SimulateIncomingResponse(
                            client,
                            id,
                            new RequestResponsePayload<object>(
                                RequestType: "CreateSourceFilter",
                                RequestId: id,
                                RequestStatus: new RequestStatus(
                                    Result: true,
                                    Code: (int)Core.Protocol.Generated.RequestStatusCode.Success
                                ),
                                ResponseData: null
                            )
                        );
                    }
                }
            )
            .Returns(ValueTask.CompletedTask);

        await client.Filters.CreateSourceFilterAsync(
            sourceName: "VideoSource",
            filterName: "CustomFilter",
            filterKind: "custom_filter_kind",
            settings: settings,
            typeInfo: typeInfo
        );

        Assert.IsNotNull(capturedSettings);
        Assert.AreEqual("my-filter", capturedSettings.Value.GetProperty("custom_key").GetString());
        Assert.AreEqual(1, capturedSettings.Value.GetProperty("custom_count").GetInt32());
    }
}
