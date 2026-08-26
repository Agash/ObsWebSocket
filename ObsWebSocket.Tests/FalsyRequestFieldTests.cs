using System.Text.Json;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Tests;

/// <summary>
/// Guards against required request fields being dropped when their value equals the CLR
/// default. The serializer context previously used <c>WhenWritingDefault</c>, which silently
/// removed <c>false</c> and <c>0</c> from outgoing payloads and made OBS reject the request
/// with code 300 ("missing field").
/// </summary>
[TestClass]
public sealed class FalsyRequestFieldTests
{
    private static string Json(object data) =>
        JsonSerializer
            .SerializeToElement(
                data,
                ObsWebSocketJsonContext.Default.Options.GetTypeInfo(data.GetType())
            )
            .GetRawText();

    [TestMethod]
    public void Serialize_RequiredFalseBool_KeepsField()
    {
        string json = Json(new SetStudioModeEnabledRequestData(false));
        Assert.AreEqual("{\"studioModeEnabled\":false}", json);
    }

    [TestMethod]
    public void Serialize_RequiredZeroNumber_KeepsField()
    {
        string json = Json(
            new SetSceneItemEnabledRequestData
            {
                SceneName = "Scene",
                SceneItemId = 0,
                SceneItemEnabled = false,
            }
        );
        StringAssert.Contains(json, "\"sceneItemId\":0");
        StringAssert.Contains(json, "\"sceneItemEnabled\":false");
    }

    [TestMethod]
    public void Serialize_UnsetOptionalField_IsStillOmitted()
    {
        // Optional protocol fields are generated as nullable, so they must stay absent
        // rather than being sent as explicit nulls — OBS applies its own defaults for
        // fields that are not present.
        string json = Json(new CreateSceneItemRequestData(sceneName: "Scene", sourceName: "Src"));
        Assert.IsFalse(json.Contains("null", StringComparison.Ordinal), json);
        Assert.IsFalse(json.Contains("sceneItemEnabled", StringComparison.Ordinal), json);
    }

    [TestMethod]
    public async Task SerializeMsgPack_RequiredFalseBool_KeepsField()
    {
        // The MessagePack transport funnels request data through the JSON context first
        // (RequestPayload.RequestData is a JsonElement), so it shares the same failure mode.
        JsonElement element = JsonSerializer.SerializeToElement(
            new SetStudioModeEnabledRequestData(false),
            ObsWebSocketJsonContext.Default.Options.GetTypeInfo(
                typeof(SetStudioModeEnabledRequestData)
            )
        );
        MsgPackMessageSerializer serializer = new(NullLogger<MsgPackMessageSerializer>.Instance);
        byte[] bytes = await serializer.SerializeAsync(
            new OutgoingMessage<RequestPayload>(
                WebSocketOpCode.Request,
                new RequestPayload("SetStudioModeEnabled", "req-1", element)
            )
        );

        string json = MessagePackSerializer.ConvertToJson(
            bytes,
            MsgPackMessageSerializer.s_msgPackOptions
        );
        StringAssert.Contains(json, "\"studioModeEnabled\":false");
    }

    [TestMethod]
    public void Serialize_IdentifyWithNoSubscriptions_KeepsZeroField()
    {
        // EventSubscription.None is 0. Dropping the field makes OBS fall back to its own
        // default subscription set, silently subscribing to everything.
        string json = JsonSerializer.Serialize(
            new OutgoingMessage<IdentifyPayload>(
                WebSocketOpCode.Identify,
                new IdentifyPayload(1, null, (uint)EventSubscription.None)
            ),
            ObsWebSocketJsonContext.Default.Options.GetTypeInfo(
                typeof(OutgoingMessage<IdentifyPayload>)
            )
        );
        StringAssert.Contains(json, "\"eventSubscriptions\":0");
    }

    [TestMethod]
    public void Serialize_JsonNullValue_IsStillWritten()
    {
        // A JsonElement whose ValueKind is Null is not a CLR null, so WhenWritingNull keeps
        // it. Storing a JSON null in a persistent-data slot must reach OBS as an explicit null.
        string json = Json(
            new SetPersistentDataRequestData
            {
                Realm = "OBS_WEBSOCKET_DATA_REALM_PROFILE",
                SlotName = "slot",
                SlotValue = JsonSerializer.SerializeToElement<object?>(null),
            }
        );
        StringAssert.Contains(json, "\"slotValue\":null");
    }
}
