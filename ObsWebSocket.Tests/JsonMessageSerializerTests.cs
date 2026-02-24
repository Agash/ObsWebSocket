using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Events;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Tests;

[TestClass]
public class JsonMessageSerializerTests
{
    private static JsonMessageSerializer CreateSerializer() =>
        new(NullLogger<JsonMessageSerializer>.Instance);

    [TestMethod]
    public void DeserializePayload_EventPayloadBaseObject_UsesContextBackedBridge()
    {
        JsonElement payload = JsonDocument
            .Parse(
                """
                {
                  "eventType": "SceneListChanged",
                  "eventIntent": 4,
                  "eventData": {
                    "scenes": [
                      { "sceneIndex": 0, "sceneName": "Scene", "sceneUuid": "abc" }
                    ]
                  }
                }
                """
            )
            .RootElement.Clone();

        EventPayloadBase<object>? result = CreateSerializer().DeserializePayload<
            EventPayloadBase<object>
        >(payload);

        Assert.IsNotNull(result);
        Assert.AreEqual("SceneListChanged", result.EventType);
        Assert.IsInstanceOfType<JsonElement>(result.EventData);
        JsonElement eventData = (JsonElement)result.EventData;
        Assert.AreEqual(JsonValueKind.Object, eventData.ValueKind);
        Assert.AreEqual(1, eventData.GetProperty("scenes").GetArrayLength());
    }

    [TestMethod]
    public void DeserializePayload_RequestResponsePayloadObject_UsesContextBackedBridge()
    {
        JsonElement payload = JsonDocument
            .Parse(
                """
                {
                  "requestType": "GetVersion",
                  "requestId": "req-1",
                  "requestStatus": { "result": true, "code": 100 },
                  "responseData": { "obsVersion": "31.1.0" }
                }
                """
            )
            .RootElement.Clone();

        RequestResponsePayload<object>? result = CreateSerializer().DeserializePayload<
            RequestResponsePayload<object>
        >(payload);

        Assert.IsNotNull(result);
        Assert.AreEqual("GetVersion", result.RequestType);
        Assert.IsInstanceOfType<JsonElement>(result.ResponseData);
        JsonElement responseData = (JsonElement)result.ResponseData!;
        Assert.AreEqual("31.1.0", responseData.GetProperty("obsVersion").GetString());
    }

    [TestMethod]
    public void DeserializePayload_SceneListChangedPayload_DeserializesSceneStubs()
    {
        JsonElement payload = JsonDocument
            .Parse(
                """
                {
                  "scenes": [
                    { "sceneIndex": 0, "sceneName": "Scene A", "sceneUuid": "uuid-a", "x-extra": 123 },
                    { "sceneIndex": 1, "sceneName": "Scene B", "sceneUuid": "uuid-b" }
                  ]
                }
                """
            )
            .RootElement.Clone();

        SceneListChangedPayload? result = CreateSerializer().DeserializePayload<SceneListChangedPayload>(
            payload
        );

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Scenes);
        Assert.AreEqual(2, result.Scenes.Count);
        Assert.AreEqual("Scene A", result.Scenes[0].SceneName);
        Dictionary<string, JsonElement>? extensionData = result.Scenes[0].ExtensionData;
        Assert.IsNotNull(extensionData);
        Assert.IsTrue(extensionData.ContainsKey("x-extra"));
        Assert.AreEqual(123, extensionData["x-extra"].GetInt32());
    }

    [TestMethod]
    public void DeserializePayload_RequestBatchResponsePayloadObject_UsesContextBackedBridge()
    {
        JsonElement payload = JsonDocument
            .Parse(
                """
                {
                  "requestId": "batch-1",
                  "results": [
                    {
                      "requestType": "GetVersion",
                      "requestId": "batch-1_0",
                      "requestStatus": { "result": true, "code": 100 },
                      "responseData": { "obsVersion": "31.1.0" }
                    }
                  ]
                }
                """
            )
            .RootElement.Clone();

        RequestBatchResponsePayload<object>? result = CreateSerializer().DeserializePayload<
            RequestBatchResponsePayload<object>
        >(payload);

        Assert.IsNotNull(result);
        Assert.AreEqual("batch-1", result.RequestId);
        Assert.AreEqual(1, result.Results.Count);
        Assert.IsInstanceOfType<JsonElement>(result.Results[0].ResponseData);
        JsonElement responseData = (JsonElement)result.Results[0].ResponseData!;
        Assert.AreEqual("31.1.0", responseData.GetProperty("obsVersion").GetString());
    }

    [TestMethod]
    public async Task SerializeAsync_OutgoingMessage_UsesExpectedEnvelopeShape()
    {
        OutgoingMessage<RequestPayload> outgoing = new(
            WebSocketOpCode.Request,
            new RequestPayload("GetSceneList", "req-1", null)
        );

        byte[] bytes = await CreateSerializer().SerializeAsync(outgoing);
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;

        Assert.AreEqual((int)WebSocketOpCode.Request, root.GetProperty("op").GetInt32());
        JsonElement data = root.GetProperty("d");
        Assert.AreEqual("GetSceneList", data.GetProperty("requestType").GetString());
        Assert.AreEqual("req-1", data.GetProperty("requestId").GetString());
        Assert.IsFalse(data.TryGetProperty("requestData", out _));
    }

    [TestMethod]
    public async Task DeserializeAsync_ValidIncomingEnvelope_ReturnsIncomingMessage()
    {
        const string incomingJson =
            """
            {
              "op": 5,
              "d": {
                "eventType": "SceneListChanged",
                "eventIntent": 4,
                "eventData": {
                  "scenes": [
                    { "sceneIndex": 0, "sceneName": "Scene A", "sceneUuid": "scene-a" }
                  ]
                }
              }
            }
            """;
        await using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(incomingJson));

        object? result = await CreateSerializer().DeserializeAsync(stream);

        Assert.IsNotNull(result);
        Assert.IsInstanceOfType<IncomingMessage<JsonElement>>(result);
        IncomingMessage<JsonElement> incoming = (IncomingMessage<JsonElement>)result;
        Assert.AreEqual(WebSocketOpCode.Event, incoming.Op);
        Assert.AreEqual("SceneListChanged", incoming.D.GetProperty("eventType").GetString());
    }

    [TestMethod]
    public void DeserializeValuePayload_WebSocketOpCode_DeserializesEnumValueType()
    {
        JsonElement payload = JsonDocument.Parse("5").RootElement.Clone();

        WebSocketOpCode? result = CreateSerializer().DeserializeValuePayload<WebSocketOpCode>(
            payload
        );

        Assert.IsTrue(result.HasValue);
        Assert.AreEqual(WebSocketOpCode.Event, result.Value);
    }

    [TestMethod]
    public void DeserializePayload_RequestStatus_DeserializesFields()
    {
        JsonElement payload = JsonDocument
            .Parse(
                """
                {
                  "result": true,
                  "code": 100,
                  "comment": "ok"
                }
                """
            )
            .RootElement.Clone();

        ObsWebSocket.Core.Protocol.RequestStatus? result = CreateSerializer().DeserializePayload<
            ObsWebSocket.Core.Protocol.RequestStatus
        >(payload);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Result);
        Assert.AreEqual(100, result.Code);
        Assert.AreEqual("ok", result.Comment);
    }
}
