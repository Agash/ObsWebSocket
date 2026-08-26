using System.Text.Json;
using MessagePack;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Protocol.Common;
using ObsWebSocket.Core.Protocol.Events;
using ObsWebSocket.Core.Protocol.Responses;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Tests;

/// <summary>
/// An <c>Array&lt;Object&gt;</c> the protocol does not describe has to be mapped onto a stub by the
/// generator. Left unmapped it became a list of <see cref="JsonElement"/>, which MessagePack had no
/// formatter for; mapped onto the wrong stub it fails on required members the payload never sends.
/// </summary>
[TestClass]
public sealed class StubArrayTests
{
    [TestMethod]
    public void Canvases_RoundTripOverMsgPack_KeepsFlagsAndVideoSettings()
    {
        GetCanvasListResponseData original = new()
        {
            Canvases =
            [
                new CanvasStub
                {
                    CanvasName = "Main",
                    CanvasUuid = "0e57ad4c-2b2d-4f5b-9d05-3f4b0f4f1f10",
                    CanvasFlags = new CanvasFlagsStub
                    {
                        Main = true,
                        Activate = true,
                        MixAudio = true,
                        SceneRef = true,
                        Ephemeral = false,
                    },
                    CanvasVideoSettings = new CanvasVideoSettingsStub
                    {
                        FpsNumerator = 60,
                        FpsDenominator = 1,
                        BaseWidth = 1920,
                        BaseHeight = 1080,
                        OutputWidth = 1280,
                        OutputHeight = 720,
                    },
                },
            ],
        };

        byte[] packed = MessagePackSerializer.Serialize(
            original,
            MsgPackMessageSerializer.s_msgPackOptions
        );
        GetCanvasListResponseData read =
            MessagePackSerializer.Deserialize<GetCanvasListResponseData>(
                packed,
                MsgPackMessageSerializer.s_msgPackOptions
            );

        Assert.AreEqual(1, read.Canvases.Count);
        Assert.AreEqual("Main", read.Canvases[0].CanvasName);
        Assert.IsTrue(read.Canvases[0].CanvasFlags.Main);
        Assert.IsFalse(read.Canvases[0].CanvasFlags.Ephemeral);
        Assert.AreEqual(1920, read.Canvases[0].CanvasVideoSettings.BaseWidth);
        Assert.AreEqual(720, read.Canvases[0].CanvasVideoSettings.OutputHeight);
    }

    /// <summary>
    /// OBS sends the whole video settings object as nulls when it cannot read the canvas video
    /// info, rather than omitting it.
    /// </summary>
    [TestMethod]
    public void Canvases_NullVideoSettings_ReadOverJson()
    {
        JsonElement payload = JsonDocument
            .Parse(
                """
                {
                  "canvases": [
                    {
                      "canvasName": "Vertical",
                      "canvasUuid": "3d0e6c1a-9a5e-4a1a-9df0-2f0b1c9d7a22",
                      "canvasFlags": {
                        "MAIN": false, "ACTIVATE": true, "MIX_AUDIO": false,
                        "SCENE_REF": true, "EPHEMERAL": false
                      },
                      "canvasVideoSettings": {
                        "fpsNumerator": null, "fpsDenominator": null,
                        "baseWidth": null, "baseHeight": null,
                        "outputWidth": null, "outputHeight": null
                      }
                    }
                  ]
                }
                """
            )
            .RootElement.Clone();

        GetCanvasListResponseData? read = CreateJsonSerializer()
            .DeserializePayload<GetCanvasListResponseData>(payload);

        Assert.IsNotNull(read);
        Assert.AreEqual("Vertical", read.Canvases[0].CanvasName);
        Assert.IsFalse(read.Canvases[0].CanvasFlags.Main);
        Assert.IsNull(read.Canvases[0].CanvasVideoSettings.BaseWidth);
    }

    /// <summary>
    /// The meter payload carries only the name, the uuid and the levels. Reading it as an
    /// <see cref="InputStub"/> failed on the input kind it never sends.
    /// </summary>
    [TestMethod]
    public void InputVolumeMeters_ReadOverJson_KeepsPerChannelLevels()
    {
        JsonElement payload = JsonDocument
            .Parse(
                """
                {
                  "inputs": [
                    {
                      "inputName": "Mic/Aux",
                      "inputUuid": "8b7a1f2e-1c3d-4e5f-8a9b-0c1d2e3f4a5b",
                      "inputLevelsMul": [[0.25, 0.5, 0.75], [0.2, 0.45, 0.7]]
                    }
                  ]
                }
                """
            )
            .RootElement.Clone();

        InputVolumeMetersPayload? read = CreateJsonSerializer()
            .DeserializePayload<InputVolumeMetersPayload>(payload);

        Assert.IsNotNull(read);
        Assert.AreEqual(1, read.Inputs.Count);
        Assert.AreEqual("Mic/Aux", read.Inputs[0].InputName);
        Assert.AreEqual(2, read.Inputs[0].InputLevelsMul.Count);
        Assert.AreEqual(0.75, read.Inputs[0].InputLevelsMul[0][2]);
    }

    [TestMethod]
    public void InputVolumeMeters_RoundTripsOverMsgPack()
    {
        InputVolumeMetersPayload original = new()
        {
            Inputs =
            [
                new InputVolumeMeterStub
                {
                    InputName = "Desktop Audio",
                    InputUuid = "1f2e3d4c-5b6a-4978-8695-a4b3c2d1e0f9",
                    InputLevelsMul =
                    [
                        [0.1, 0.2, 0.3],
                    ],
                },
            ],
        };

        byte[] packed = MessagePackSerializer.Serialize(
            original,
            MsgPackMessageSerializer.s_msgPackOptions
        );
        InputVolumeMetersPayload read = MessagePackSerializer.Deserialize<InputVolumeMetersPayload>(
            packed,
            MsgPackMessageSerializer.s_msgPackOptions
        );

        Assert.AreEqual("Desktop Audio", read.Inputs[0].InputName);
        Assert.AreEqual(0.3, read.Inputs[0].InputLevelsMul[0][2]);
    }

    /// <summary>
    /// The reindex event asks OBS for the basic scene item list, which carries the id and the
    /// index and nothing else.
    /// </summary>
    [TestMethod]
    public void SceneItemListReindexed_ReadOverJson_KeepsIdAndIndex()
    {
        JsonElement payload = JsonDocument
            .Parse(
                """
                {
                  "sceneName": "Intro",
                  "sceneUuid": "5d5db648-93a5-4985-bff8-45f4c9fe15f7",
                  "sceneItems": [
                    { "sceneItemId": 1, "sceneItemIndex": 0 },
                    { "sceneItemId": 4, "sceneItemIndex": 1 }
                  ]
                }
                """
            )
            .RootElement.Clone();

        SceneItemListReindexedPayload? read = CreateJsonSerializer()
            .DeserializePayload<SceneItemListReindexedPayload>(payload);

        Assert.IsNotNull(read);
        Assert.AreEqual(2, read.SceneItems.Count);
        Assert.AreEqual(4, read.SceneItems[1].SceneItemId);
        Assert.AreEqual(1, read.SceneItems[1].SceneItemIndex);
    }

    [TestMethod]
    public void SceneItemListReindexed_RoundTripsOverMsgPack()
    {
        SceneItemListReindexedPayload original = new()
        {
            SceneName = "Intro",
            SceneUuid = "5d5db648-93a5-4985-bff8-45f4c9fe15f7",
            SceneItems = [new SceneItemOrderStub { SceneItemId = 7, SceneItemIndex = 2 }],
        };

        byte[] packed = MessagePackSerializer.Serialize(
            original,
            MsgPackMessageSerializer.s_msgPackOptions
        );
        SceneItemListReindexedPayload read =
            MessagePackSerializer.Deserialize<SceneItemListReindexedPayload>(
                packed,
                MsgPackMessageSerializer.s_msgPackOptions
            );

        Assert.AreEqual(7, read.SceneItems[0].SceneItemId);
        Assert.AreEqual(2, read.SceneItems[0].SceneItemIndex);
    }

    /// <summary>
    /// Why the meter payload needs its own stub, stated as a test so the mapping cannot quietly go
    /// back to <see cref="InputStub"/>: a real meter item has none of the kind fields that stub
    /// requires, so reading one as an input fails outright.
    /// </summary>
    [TestMethod]
    public void MeterItem_ReadAsInputStub_FailsOnTheKindFieldsItNeverSends()
    {
        JsonElement item = JsonDocument
            .Parse(
                """
                {
                  "inputName": "Mic/Aux",
                  "inputUuid": "8b7a1f2e-1c3d-4e5f-8a9b-0c1d2e3f4a5b",
                  "inputLevelsMul": [[0.25, 0.5, 0.75]]
                }
                """
            )
            .RootElement.Clone();

        ObsWebSocketSerializationException ex =
            Assert.ThrowsExactly<ObsWebSocketSerializationException>(() =>
                CreateJsonSerializer().DeserializePayload<InputStub>(item)
            );

        StringAssert.Contains(ex.InnerException!.Message, "inputKind");
    }

    /// <summary>
    /// The formatter that made an unmapped array readable at all. Nothing generated uses it now
    /// that every array is mapped, but it is what stands between a future unmapped array and a
    /// transport that cannot read the message.
    /// </summary>
    [TestMethod]
    public void JsonElementList_HasAMsgPackFormatter()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"a":1}""");
        List<JsonElement> original = [doc.RootElement.Clone()];

        byte[] packed = MessagePackSerializer.Serialize(
            original,
            MsgPackMessageSerializer.s_msgPackOptions
        );
        List<JsonElement> read = MessagePackSerializer.Deserialize<List<JsonElement>>(
            packed,
            MsgPackMessageSerializer.s_msgPackOptions
        );

        Assert.AreEqual(1, read.Count);
        Assert.AreEqual(1, read[0].GetProperty("a").GetInt32());
    }

    private static JsonMessageSerializer CreateJsonSerializer() =>
        new(Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonMessageSerializer>.Instance);
}
