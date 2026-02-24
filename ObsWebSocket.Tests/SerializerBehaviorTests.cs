using System.Buffers;
using System.Text.Json;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Events;
using ObsWebSocket.Core.Protocol.Generated;
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
    public async Task MsgPackSerializer_DeserializeAsync_WithEmptyStream_ReturnsNull()
    {
        MsgPackMessageSerializer serializer = CreateMsgPackSerializer();
        await using MemoryStream stream = new([]);
        object? envelope = await serializer.DeserializeAsync(stream);

        Assert.IsNull(envelope);
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
}
