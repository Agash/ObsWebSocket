using System.Buffers;
using System.Reflection;
using System.Text.Json;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Events;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Tests;

[TestClass]
public class SerializerBehaviorTests
{
    private static JsonMessageSerializer CreateJsonSerializer() =>
        new(NullLogger<JsonMessageSerializer>.Instance);

    private static MsgPackMessageSerializer CreateMsgPackSerializer() =>
        new(NullLogger<MsgPackMessageSerializer>.Instance);

    [TestMethod]
    public void JsonSerializer_DeserializePayload_SceneStubExtensionData_IsAvailable()
    {
        JsonElement payload = JsonDocument
            .Parse(
                """
                {
                  "scenes": [
                    {
                      "sceneIndex": 0,
                      "sceneName": "Scene A",
                      "sceneUuid": "uuid-a",
                      "extraField": {
                        "nestedName": "Nested Value",
                        "enabled": true,
                        "numbers": [1, 2, 3]
                      },
                      "extraArray": [
                        { "id": "a", "value": 10 },
                        { "id": "b", "value": 20 }
                      ]
                    }
                  ]
                }
                """
            )
            .RootElement.Clone();

        SceneListChangedPayload? result = CreateJsonSerializer().DeserializePayload<SceneListChangedPayload>(
            payload
        );

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Scenes);
        Assert.AreEqual(1, result.Scenes.Count);
        Dictionary<string, JsonElement>? extensionData = result.Scenes[0].ExtensionData;
        Assert.IsNotNull(extensionData);
        Assert.IsTrue(extensionData.ContainsKey("extraField"));
        Assert.IsTrue(extensionData.ContainsKey("extraArray"));

        JsonElement extraField = extensionData["extraField"];
        Assert.AreEqual(JsonValueKind.Object, extraField.ValueKind);
        Assert.AreEqual("Nested Value", extraField.GetProperty("nestedName").GetString());
        Assert.IsTrue(extraField.GetProperty("enabled").GetBoolean());

        JsonElement numbers = extraField.GetProperty("numbers");
        Assert.AreEqual(JsonValueKind.Array, numbers.ValueKind);
        Assert.AreEqual(3, numbers.GetArrayLength());
        Assert.AreEqual(1, numbers[0].GetInt32());
        Assert.AreEqual(2, numbers[1].GetInt32());
        Assert.AreEqual(3, numbers[2].GetInt32());

        JsonElement extraArray = extensionData["extraArray"];
        Assert.AreEqual(JsonValueKind.Array, extraArray.ValueKind);
        Assert.AreEqual(2, extraArray.GetArrayLength());
        Assert.AreEqual("a", extraArray[0].GetProperty("id").GetString());
        Assert.AreEqual(10, extraArray[0].GetProperty("value").GetInt32());
        Assert.AreEqual("b", extraArray[1].GetProperty("id").GetString());
        Assert.AreEqual(20, extraArray[1].GetProperty("value").GetInt32());
    }

    [TestMethod]
    public void MsgPackSerializer_DeserializePayload_WithComplexSceneStubBytes_DeserializesValues()
    {
        MsgPackMessageSerializer serializer = CreateMsgPackSerializer();
        byte[] payloadBytes = BuildSceneListChangedPayloadBytes();

        SceneListChangedPayload? payload = serializer.DeserializePayload<SceneListChangedPayload>(
            new ReadOnlyMemory<byte>(payloadBytes)
        );

        Assert.IsNotNull(payload);
        Assert.IsNotNull(payload.Scenes);
        Assert.AreEqual(1, payload.Scenes.Count);
        Assert.AreEqual(1, payload.Scenes[0].SceneIndex);
        Assert.AreEqual("IntegrationScene", payload.Scenes[0].SceneName);
        Assert.AreEqual("scene-uuid-1", payload.Scenes[0].SceneUuid);
        Assert.IsNotNull(payload.Scenes[0].ExtensionData);
        Assert.AreEqual(
            "extension-data-like-field",
            payload.Scenes[0].ExtensionData["extraTag"].GetString()
        );
    }

    [TestMethod]
    public void MsgPackSerializer_DeserializePayload_WithComplexFilterBytes_DeserializesValues()
    {
        MsgPackMessageSerializer serializer = CreateMsgPackSerializer();
        byte[] payloadBytes = BuildSourceFilterListPayloadBytes();

        GetSourceFilterListResponseData? payload =
            serializer.DeserializePayload<GetSourceFilterListResponseData>(
                new ReadOnlyMemory<byte>(payloadBytes)
            );

        Assert.IsNotNull(payload);
        Assert.IsNotNull(payload.Filters);
        Assert.AreEqual(1, payload.Filters.Count);
        Assert.AreEqual("Color Correction", payload.Filters[0].FilterName);
        Assert.AreEqual("color_filter_v2", payload.Filters[0].FilterKind);
        Assert.AreEqual(0, payload.Filters[0].FilterIndex);
        Assert.IsTrue(payload.Filters[0].FilterEnabled ?? false);
        Assert.IsTrue(payload.Filters[0].FilterSettings.HasValue);
        Assert.AreEqual(
            0.8d,
            payload.Filters[0].FilterSettings.Value.GetProperty("opacity").GetDouble(),
            0.0001d
        );
        Assert.AreEqual(
            1.2d,
            payload.Filters[0].FilterSettings.Value.GetProperty("gamma").GetDouble(),
            0.0001d
        );
        Assert.IsNotNull(payload.Filters[0].ExtensionData);
        Assert.AreEqual(
            "present",
            payload.Filters[0].ExtensionData["customExtensionField"].GetString()
        );
        JsonElement listExtension = payload.Filters[0].ExtensionData["listExtension"];
        Assert.AreEqual(JsonValueKind.Array, listExtension.ValueKind);
        Assert.AreEqual(2, listExtension.GetArrayLength());
        Assert.AreEqual("a", listExtension[0].GetString());
        Assert.AreEqual("b", listExtension[1].GetString());
    }

    [TestMethod]
    public void MsgPackSerializer_DeserializeValuePayload_WithIntBytes_DeserializesValue()
    {
        MsgPackMessageSerializer serializer = CreateMsgPackSerializer();
        byte[] bytes = MessagePack.MessagePackSerializer.Serialize(42);
        int? payload = serializer.DeserializeValuePayload<int>(new ReadOnlyMemory<byte>(bytes));

        Assert.IsTrue(payload.HasValue);
        Assert.AreEqual(42, payload.Value);
    }

    [TestMethod]
    public async Task JsonSerializer_SerializeAsync_RequestPayloadWithTypedRequestData_ContainsExpectedShape()
    {
        JsonMessageSerializer serializer = CreateJsonSerializer();
        JsonElement requestData = JsonDocument
            .Parse(
                """
                {
                  "filterName": "Color Correction",
                  "filterSettings": {
                    "opacity": 0.75,
                    "gamma": 1.20,
                    "advanced": { "lift": 0.1 }
                  },
                  "overlay": true
                }
                """
            )
            .RootElement.Clone();

        OutgoingMessage<RequestPayload> message = new(
            WebSocketOpCode.Request,
            new RequestPayload("SetSourceFilterSettings", "req-99", requestData)
        );

        byte[] bytes = await serializer.SerializeAsync(message);
        using JsonDocument doc = JsonDocument.Parse(bytes);
        JsonElement root = doc.RootElement;
        Assert.AreEqual((int)WebSocketOpCode.Request, root.GetProperty("op").GetInt32());
        JsonElement d = root.GetProperty("d");
        Assert.AreEqual("SetSourceFilterSettings", d.GetProperty("requestType").GetString());
        Assert.AreEqual("req-99", d.GetProperty("requestId").GetString());
        JsonElement payload = d.GetProperty("requestData");
        Assert.AreEqual("Color Correction", payload.GetProperty("filterName").GetString());
        Assert.IsTrue(payload.GetProperty("overlay").GetBoolean());
        Assert.AreEqual(
            0.75d,
            payload.GetProperty("filterSettings").GetProperty("opacity").GetDouble(),
            0.0001d
        );
    }

    [TestMethod]
    public void JsonSerializer_DeserializePayload_NestedTypeRequestData_DeserializesModifiers()
    {
        JsonMessageSerializer serializer = CreateJsonSerializer();
        JsonElement payload = JsonDocument
            .Parse(
                """
                {
                  "keyId": "OBS_KEY_A",
                  "keyModifiers": {
                    "alt": true,
                    "command": false,
                    "control": true,
                    "shift": false
                  }
                }
                """
            )
            .RootElement.Clone();

        TriggerHotkeyByKeySequenceRequestData? data =
            serializer.DeserializePayload<TriggerHotkeyByKeySequenceRequestData>(payload);

        Assert.IsNotNull(data);
        Assert.AreEqual("OBS_KEY_A", data.KeyId);
        Assert.IsNotNull(data.KeyModifiers);
        Assert.IsTrue(data.KeyModifiers.Alt);
        Assert.IsFalse(data.KeyModifiers.Command);
        Assert.IsTrue(data.KeyModifiers.Control);
        Assert.IsFalse(data.KeyModifiers.Shift);
    }

    [TestMethod]
    public void JsonSerializer_DeserializePayload_InvalidGeneratedShape_ReturnsDefault()
    {
        JsonMessageSerializer serializer = CreateJsonSerializer();
        JsonElement payload = JsonDocument
            .Parse(
                """
                {
                  "sceneName": 12345
                }
                """
            )
            .RootElement.Clone();

        CreateSceneRequestData? data = serializer.DeserializePayload<CreateSceneRequestData>(payload);
        Assert.IsNull(data);
    }

    [TestMethod]
    public async Task MsgPackSerializer_DeserializeAsync_WithEmptyStream_ReturnsNull()
    {
        MsgPackMessageSerializer serializer = CreateMsgPackSerializer();
        await using MemoryStream stream = new([]);
        object? envelope = await serializer.DeserializeAsync(stream);

        Assert.IsNull(envelope);
    }

    [TestMethod]
    public async Task MsgPackSerializer_DeserializeAsync_IncomingMapPayload_CapturesRawDBytes()
    {
        MsgPackMessageSerializer serializer = CreateMsgPackSerializer();
        byte[] bytes = BuildIncomingHelloEnvelopeBytes();
        await using MemoryStream stream = new(bytes);

        object? envelope = await serializer.DeserializeAsync(stream);

        _ = Assert.IsInstanceOfType<IncomingMessage<ReadOnlyMemory<byte>>>(envelope);
        IncomingMessage<ReadOnlyMemory<byte>> incoming = (IncomingMessage<ReadOnlyMemory<byte>>)envelope;
        Assert.AreEqual(WebSocketOpCode.Hello, incoming.Op);
        Assert.IsTrue(incoming.D.Length > 0);

        HelloPayload? hello = serializer.DeserializePayload<HelloPayload>(incoming.D);
        Assert.IsNotNull(hello);
        Assert.AreEqual("5.0.0", hello.ObsWebSocketVersion);
        Assert.AreEqual(1, hello.RpcVersion);
    }

    [TestMethod]
    public async Task MsgPackSerializer_DeserializeAsync_WithUnreadableStream_ThrowsArgumentException()
    {
        MsgPackMessageSerializer serializer = CreateMsgPackSerializer();
        await using UnreadableMemoryStream stream = new([]);

        try
        {
            _ = await serializer.DeserializeAsync(stream);
            Assert.Fail("Expected ArgumentException for unreadable stream.");
        }
        catch (ArgumentException)
        {
            // Expected.
        }
    }

    [TestMethod]
    public async Task MsgPackSerializer_SerializeAsync_KnownGeneratedPayload_ReturnsBytes()
    {
        MsgPackMessageSerializer serializer = CreateMsgPackSerializer();
        OutgoingMessage<RequestPayload> message = new(
            WebSocketOpCode.Request,
            new RequestPayload("GetVersion", "req-1", null)
        );

        byte[] bytes = await serializer.SerializeAsync(message);
        Assert.IsTrue(bytes.Length > 0);
    }

    [TestMethod]
    public async Task MsgPackSerializer_SerializeAsync_WithJsonElementRequestData_RoundTripsThroughDeserializer()
    {
        MsgPackMessageSerializer serializer = CreateMsgPackSerializer();
        JsonElement settings = JsonDocument
            .Parse(
                """
                {
                  "sceneName": "Scene A",
                  "filterSettings": {
                    "opacity": 0.33
                  }
                }
                """
            )
            .RootElement.Clone();

        OutgoingMessage<RequestPayload> message = new(
            WebSocketOpCode.Request,
            new RequestPayload("SetSourceFilterSettings", "req-55", settings)
        );

        byte[] bytes = await serializer.SerializeAsync(message);
        byte[] dRaw = ExtractDFieldRawBytes(bytes);
        RequestPayload? requestPayload = serializer.DeserializePayload<RequestPayload>(
            new ReadOnlyMemory<byte>(dRaw)
        );
        Assert.IsNotNull(requestPayload);
        Assert.AreEqual("SetSourceFilterSettings", requestPayload.RequestType);
        Assert.AreEqual("req-55", requestPayload.RequestId);
        Assert.IsTrue(requestPayload.RequestData.HasValue);
        Assert.AreEqual(
            "Scene A",
            requestPayload.RequestData.Value.GetProperty("sceneName").GetString()
        );
        Assert.AreEqual(
            0.33d,
            requestPayload
                .RequestData.Value.GetProperty("filterSettings")
                .GetProperty("opacity")
                .GetDouble(),
            0.0001d
        );
    }

    [TestMethod]
    public void MsgPackSerializer_DeserializePayload_NestedType_FromBytes()
    {
        MsgPackMessageSerializer serializer = CreateMsgPackSerializer();
        byte[] bytes = BuildTriggerHotkeyNestedPayloadBytes();

        TriggerHotkeyByKeySequenceRequestData? payload =
            serializer.DeserializePayload<TriggerHotkeyByKeySequenceRequestData>(
                new ReadOnlyMemory<byte>(bytes)
            );

        Assert.IsNotNull(payload);
        Assert.AreEqual("OBS_KEY_B", payload.KeyId);
        Assert.IsNotNull(payload.KeyModifiers);
        Assert.IsTrue(payload.KeyModifiers.Alt);
        Assert.IsTrue(payload.KeyModifiers.Control);
        Assert.IsFalse(payload.KeyModifiers.Command);
        Assert.IsFalse(payload.KeyModifiers.Shift);
    }

    [TestMethod]
    public void MsgPackSerializer_DeserializePayload_SceneItemList_WithTransformAndExtensionData()
    {
        MsgPackMessageSerializer serializer = CreateMsgPackSerializer();
        byte[] bytes = BuildSceneItemListPayloadBytes();

        GetSceneItemListResponseData? payload =
            serializer.DeserializePayload<GetSceneItemListResponseData>(new ReadOnlyMemory<byte>(bytes));

        Assert.IsNotNull(payload);
        Assert.IsNotNull(payload.SceneItems);
        Assert.AreEqual(1, payload.SceneItems.Count);
        Assert.AreEqual(42, payload.SceneItems[0].SceneItemId);
        Assert.AreEqual("Camera", payload.SceneItems[0].SourceName);
        Assert.IsNotNull(payload.SceneItems[0].SceneItemTransform);
        Assert.AreEqual(1920d, payload.SceneItems[0].SceneItemTransform.Width!.Value, 0.001d);
        Assert.IsNotNull(payload.SceneItems[0].ExtensionData);
        Assert.AreEqual("group-A", payload.SceneItems[0].ExtensionData["customGroup"].GetString());
    }

    [TestMethod]
    public void MsgPackSerializer_DeserializePayload_InvalidGeneratedShape_ReturnsDefault()
    {
        MsgPackMessageSerializer serializer = CreateMsgPackSerializer();
        byte[] bytes = BuildInvalidFilterListPayloadBytes();

        GetSourceFilterListResponseData? payload =
            serializer.DeserializePayload<GetSourceFilterListResponseData>(new ReadOnlyMemory<byte>(bytes));

        Assert.IsNull(payload);
    }

    [TestMethod]
    public void MsgPackSerializer_SerializeThenDeserialize_FilterPayload_RoundTripsWithValues()
    {
        MsgPackMessageSerializer serializer = CreateMsgPackSerializer();
        GetSourceFilterListResponseData payload = new(
            [
                new Core.Protocol.Common.FilterStub
                {
                    FilterName = "Color Correction",
                    FilterKind = "color_filter_v2",
                    FilterIndex = 0,
                    FilterEnabled = true,
                },
                new Core.Protocol.Common.FilterStub
                {
                    FilterName = "Limiter",
                    FilterKind = "limiter_filter_v2",
                    FilterIndex = 1,
                    FilterEnabled = false,
                },
            ]
        );

        byte[] bytes = MessagePack.MessagePackSerializer.Serialize(payload);
        GetSourceFilterListResponseData? roundTrip = serializer.DeserializePayload<
            GetSourceFilterListResponseData
        >(new ReadOnlyMemory<byte>(bytes));

        Assert.IsNotNull(roundTrip);
        Assert.IsNotNull(roundTrip.Filters);
        Assert.AreEqual(2, roundTrip.Filters.Count);

        Assert.AreEqual("Color Correction", roundTrip.Filters[0].FilterName);
        Assert.AreEqual("color_filter_v2", roundTrip.Filters[0].FilterKind);
        Assert.AreEqual(0, roundTrip.Filters[0].FilterIndex);
        Assert.IsTrue(roundTrip.Filters[0].FilterEnabled);
        Assert.IsFalse(roundTrip.Filters[0].FilterSettings.HasValue);

        Assert.AreEqual("Limiter", roundTrip.Filters[1].FilterName);
        Assert.AreEqual("limiter_filter_v2", roundTrip.Filters[1].FilterKind);
        Assert.AreEqual(1, roundTrip.Filters[1].FilterIndex);
        Assert.IsFalse(roundTrip.Filters[1].FilterEnabled ?? true);
        Assert.IsFalse(roundTrip.Filters[1].FilterSettings.HasValue);
    }

    [TestMethod]
    public void MsgPackResolver_CoversGeneratedAndCoreProtocolTypes()
    {
        Type[] allTypes = typeof(ObsWebSocketClient).Assembly.GetTypes();
        List<Type> requiredTypes = [];

        requiredTypes.AddRange(
            allTypes.Where(static t =>
                t is { IsClass: true, IsAbstract: false }
                && t.Namespace == "ObsWebSocket.Core.Protocol.Requests"
                && t.Name.EndsWith("RequestData", StringComparison.Ordinal)
            )
        );

        requiredTypes.AddRange(
            allTypes.Where(static t =>
                t is { IsClass: true, IsAbstract: false }
                && t.Namespace == "ObsWebSocket.Core.Protocol.Responses"
                && t.Name.EndsWith("ResponseData", StringComparison.Ordinal)
            )
        );

        requiredTypes.AddRange(
            allTypes.Where(static t =>
                t is { IsClass: true, IsAbstract: false }
                && t.Namespace == "ObsWebSocket.Core.Protocol.Events"
                && t.Name.EndsWith("Payload", StringComparison.Ordinal)
            )
        );

        requiredTypes.AddRange(
            allTypes.Where(static t =>
                t is { IsClass: true, IsAbstract: false }
                && t.Namespace == "ObsWebSocket.Core.Protocol.Common.NestedTypes"
            )
        );

        requiredTypes.Add(
            typeof(HelloPayload)
        );
        requiredTypes.Add(
            typeof(AuthenticationData)
        );
        requiredTypes.Add(
            typeof(IdentifyPayload)
        );
        requiredTypes.Add(
            typeof(IdentifiedPayload)
        );
        requiredTypes.Add(
            typeof(ReidentifyPayload)
        );
        requiredTypes.Add(
            typeof(RequestPayload)
        );
        requiredTypes.Add(
            typeof(RequestBatchPayload)
        );
        requiredTypes.Add(typeof(ObsWebSocket.Core.Protocol.RequestStatus));

        List<string> missing = [];
        foreach (Type type in requiredTypes.Distinct())
        {
            if (!HasFormatter(ObsWebSocketMsgPackResolver.Instance, type))
            {
                missing.Add(type.FullName ?? type.Name);
            }
        }

        if (missing.Count > 0)
        {
            Assert.Fail(
                $"ObsWebSocketMsgPackResolver is missing formatter(s): {string.Join(", ", missing.OrderBy(static s => s, StringComparer.Ordinal))}"
            );
        }
    }

    private static byte[] BuildSceneListChangedPayloadBytes()
    {
        ArrayBufferWriter<byte> buffer = new();
        MessagePackWriter writer = new(buffer);

        writer.WriteMapHeader(1);
        writer.Write("scenes");
        writer.WriteArrayHeader(1);
        writer.WriteMapHeader(4);
        writer.Write("sceneIndex");
        writer.Write(1);
        writer.Write("sceneName");
        writer.Write("IntegrationScene");
        writer.Write("sceneUuid");
        writer.Write("scene-uuid-1");
        writer.Write("extraTag");
        writer.Write("extension-data-like-field");
        writer.Flush();

        return [.. buffer.WrittenSpan];
    }

    private static byte[] ExtractDFieldRawBytes(byte[] messageBytes)
    {
        MessagePackReader reader = new(new ReadOnlySequence<byte>(messageBytes));
        int count = reader.ReadMapHeader();
        for (int i = 0; i < count; i++)
        {
            string key = reader.ReadString()!;
            if (key == "d")
            {
                return ReadRawValue(ref reader);
            }

            reader.Skip();
        }

        Assert.Fail("MessagePack payload did not contain 'd' field.");
        return [];
    }

    private static byte[] ReadRawValue(ref MessagePackReader reader)
    {
        SequencePosition start = reader.Position;
        MessagePackReader clone = reader;
        clone.Skip();
        SequencePosition end = clone.Position;
        ReadOnlySequence<byte> sequence = reader.Sequence.Slice(start, end);
        byte[] raw = new byte[checked((int)sequence.Length)];
        sequence.CopyTo(raw);
        reader = clone;
        return raw;
    }

    private static byte[] BuildTriggerHotkeyNestedPayloadBytes()
    {
        ArrayBufferWriter<byte> buffer = new();
        MessagePackWriter writer = new(buffer);

        writer.WriteMapHeader(2);
        writer.Write("keyId");
        writer.Write("OBS_KEY_B");
        writer.Write("keyModifiers");
        writer.WriteMapHeader(4);
        writer.Write("alt");
        writer.Write(true);
        writer.Write("command");
        writer.Write(false);
        writer.Write("control");
        writer.Write(true);
        writer.Write("shift");
        writer.Write(false);
        writer.Flush();

        return [.. buffer.WrittenSpan];
    }

    private static byte[] BuildSceneItemListPayloadBytes()
    {
        ArrayBufferWriter<byte> buffer = new();
        MessagePackWriter writer = new(buffer);

        writer.WriteMapHeader(1);
        writer.Write("sceneItems");
        writer.WriteArrayHeader(1);
        writer.WriteMapHeader(6);
        writer.Write("sceneItemId");
        writer.Write(42);
        writer.Write("sourceName");
        writer.Write("Camera");
        writer.Write("sceneItemEnabled");
        writer.Write(true);
        writer.Write("sceneItemTransform");
        writer.WriteMapHeader(2);
        writer.Write("width");
        writer.Write(1920d);
        writer.Write("height");
        writer.Write(1080d);
        writer.Write("customGroup");
        writer.Write("group-A");
        writer.Write("customArray");
        writer.WriteArrayHeader(2);
        writer.Write("x");
        writer.Write("y");
        writer.Flush();

        return [.. buffer.WrittenSpan];
    }

    private static byte[] BuildInvalidFilterListPayloadBytes()
    {
        ArrayBufferWriter<byte> buffer = new();
        MessagePackWriter writer = new(buffer);

        writer.WriteMapHeader(1);
        writer.Write("filters");
        writer.WriteArrayHeader(1);
        writer.WriteMapHeader(4);
        writer.Write("filterName");
        writer.Write("Broken Filter");
        writer.Write("filterKind");
        writer.Write("color_filter_v2");
        writer.Write("filterIndex");
        writer.Write("not-a-number");
        writer.Write("filterEnabled");
        writer.Write(true);
        writer.Flush();

        return [.. buffer.WrittenSpan];
    }

    private static byte[] BuildIncomingHelloEnvelopeBytes()
    {
        ArrayBufferWriter<byte> buffer = new();
        MessagePackWriter writer = new(buffer);

        writer.WriteMapHeader(2);
        writer.Write("op");
        writer.Write((int)WebSocketOpCode.Hello);
        writer.Write("d");
        writer.WriteMapHeader(2);
        writer.Write("obsWebSocketVersion");
        writer.Write("5.0.0");
        writer.Write("rpcVersion");
        writer.Write(1);
        writer.Flush();

        return [.. buffer.WrittenSpan];
    }

    private static byte[] BuildSourceFilterListPayloadBytes()
    {
        ArrayBufferWriter<byte> buffer = new();
        MessagePackWriter writer = new(buffer);

        writer.WriteMapHeader(1);
        writer.Write("filters");
        writer.WriteArrayHeader(1);
        writer.WriteMapHeader(7);
        writer.Write("filterName");
        writer.Write("Color Correction");
        writer.Write("filterKind");
        writer.Write("color_filter_v2");
        writer.Write("filterIndex");
        writer.Write(0);
        writer.Write("filterEnabled");
        writer.Write(true);
        writer.Write("filterSettings");
        writer.WriteMapHeader(2);
        writer.Write("opacity");
        writer.Write(0.8);
        writer.Write("gamma");
        writer.Write(1.2);
        writer.Write("customExtensionField");
        writer.Write("present");
        writer.Write("listExtension");
        writer.WriteArrayHeader(2);
        writer.Write("a");
        writer.Write("b");
        writer.Flush();

        return [.. buffer.WrittenSpan];
    }

    private sealed class UnreadableMemoryStream(byte[] buffer) : MemoryStream(buffer)
    {
        public override bool CanRead => false;
    }

    private static bool HasFormatter(IFormatterResolver resolver, Type targetType)
    {
        MethodInfo? method = typeof(IFormatterResolver).GetMethod(nameof(IFormatterResolver.GetFormatter));
        Assert.IsNotNull(method, "Could not locate IFormatterResolver.GetFormatter<T>.");
        object? formatter = method.MakeGenericMethod(targetType).Invoke(resolver, null);
        return formatter is not null;
    }
}
