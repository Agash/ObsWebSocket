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
    /// OBS copies the output dimensions straight out of <c>obs_output_get_width</c> and
    /// <c>obs_output_get_height</c>, which return <c>uint32_t</c> and are not clamped on the way
    /// out. An output that has never started can report a value past <see cref="int.MaxValue"/> —
    /// a live OBS 32.2.2 sent 2586032160 for an idle virtual camera — and as an <c>int</c> that
    /// took the whole GetOutputList response down, not just the one field.
    /// </summary>
    [TestMethod]
    public void OutputList_HeightAboveInt32Max_ReadsOverJson()
    {
        JsonElement payload = JsonDocument
            .Parse(
                """
                {
                  "outputs": [
                    {
                      "outputName": "virtualcam_output",
                      "outputKind": "virtualcam_output",
                      "outputActive": false,
                      "outputWidth": 0,
                      "outputHeight": 2586032160,
                      "outputFlags": { "OBS_OUTPUT_VIDEO": true }
                    }
                  ]
                }
                """
            )
            .RootElement.Clone();

        GetOutputListResponseData? read = CreateJsonSerializer()
            .DeserializePayload<GetOutputListResponseData>(payload);

        Assert.IsNotNull(read);
        Assert.AreEqual(1, read.Outputs.Count);
        Assert.AreEqual(2586032160L, read.Outputs[0].OutputHeight);
    }

    [TestMethod]
    public void OutputList_HeightAboveInt32Max_RoundTripsOverMsgPack()
    {
        GetOutputListResponseData original = new()
        {
            Outputs =
            [
                new OutputStub
                {
                    OutputName = "virtualcam_output",
                    OutputKind = "virtualcam_output",
                    OutputActive = false,
                    OutputWidth = 0,
                    OutputHeight = 2586032160L,
                },
            ],
        };

        byte[] packed = MessagePackSerializer.Serialize(
            original,
            MsgPackMessageSerializer.s_msgPackOptions
        );
        GetOutputListResponseData read = MessagePackSerializer.Deserialize<GetOutputListResponseData>(
            packed,
            MsgPackMessageSerializer.s_msgPackOptions
        );

        Assert.AreEqual(2586032160L, read.Outputs[0].OutputHeight);
    }

    /// <summary>
    /// The same defect in the counters. The frame counts come from <c>uint32_t</c> and the session
    /// message counts from <c>uint64_t</c>, none of them clamped, and a monotonic frame counter
    /// passes <see cref="int.MaxValue"/> after roughly 414 days at 60fps.
    /// </summary>
    [TestMethod]
    public void Stats_CountersAboveInt32Max_ReadOverJson()
    {
        JsonElement payload = JsonDocument
            .Parse(
                """
                {
                  "cpuUsage": 1.5,
                  "memoryUsage": 512.0,
                  "availableDiskSpace": 1024.0,
                  "activeFps": 60.0,
                  "averageFrameRenderTime": 1.2,
                  "renderSkippedFrames": 4294967295,
                  "renderTotalFrames": 3000000000,
                  "outputSkippedFrames": 2147483648,
                  "outputTotalFrames": 4000000000,
                  "webSocketSessionIncomingMessages": 5000000000,
                  "webSocketSessionOutgoingMessages": 6000000000
                }
                """
            )
            .RootElement.Clone();

        GetStatsResponseData? read = CreateJsonSerializer()
            .DeserializePayload<GetStatsResponseData>(payload);

        Assert.IsNotNull(read);
        Assert.AreEqual(4294967295L, read.RenderSkippedFrames);
        Assert.AreEqual(3000000000L, read.RenderTotalFrames);
        Assert.AreEqual(6000000000L, read.WebSocketSessionOutgoingMessages);
    }


    /// <summary>
    /// A canvas-scoped scene list has no index to report, so OBS sends <c>sceneIndex</c> as null
    /// rather than omitting it. As a non-nullable member that failed the whole response.
    /// </summary>
    [TestMethod]
    public void SceneList_NullSceneIndex_ReadsOverJson()
    {
        JsonElement payload = JsonDocument
            .Parse(
                """
                {
                  "scenes": [
                    {
                      "sceneName": "Intro",
                      "sceneUuid": "0e57ad4c-2b2d-4f5b-9d05-3f4b0f4f1f10",
                      "sceneIndex": null
                    }
                  ]
                }
                """
            )
            .RootElement.Clone();

        GetSceneListResponseData? read = CreateJsonSerializer()
            .DeserializePayload<GetSceneListResponseData>(payload);

        Assert.IsNotNull(read);
        Assert.AreEqual(1, read.Scenes.Count);
        Assert.IsNull(read.Scenes[0].SceneIndex);
    }

    /// <summary>
    /// The alignment fields are <c>uint32_t</c> masks in <c>obs_transform_info</c>, and
    /// obs-websocket validates writes to the whole unsigned range rather than to the flags it
    /// defines, so a value past <see cref="int.MaxValue"/> reaches the client.
    /// </summary>
    [TestMethod]
    public void SceneItemTransform_AlignmentAboveInt32Max_ReadsOverJson()
    {
        JsonElement payload = JsonDocument
            .Parse(
                """
                {
                  "sceneItemTransform": {
                    "positionX": 0, "positionY": 0, "rotation": 0,
                    "scaleX": 1, "scaleY": 1, "width": 100, "height": 100,
                    "sourceWidth": 100, "sourceHeight": 100,
                    "alignment": 4294967295,
                    "boundsType": "OBS_BOUNDS_NONE",
                    "boundsAlignment": 3000000000,
                    "boundsWidth": 0, "boundsHeight": 0,
                    "cropLeft": 0, "cropTop": 0, "cropRight": 0, "cropBottom": 0,
                    "cropToBounds": false
                  }
                }
                """
            )
            .RootElement.Clone();

        GetSceneItemTransformResponseData? read = CreateJsonSerializer()
            .DeserializePayload<GetSceneItemTransformResponseData>(payload);

        Assert.IsNotNull(read);
        Assert.AreEqual(4294967295L, read.SceneItemTransform.Alignment);
        Assert.AreEqual(3000000000L, read.SceneItemTransform.BoundsAlignment);
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
