using System.Text.Json;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Tests;

/// <summary>
/// Covers the typed batch builder, which exists so a request type string can never be paired
/// with the wrong payload record.
/// </summary>
[TestClass]
public sealed class ObsBatchBuilderTests
{
    [TestMethod]
    public void Builder_PairsRequestTypeWithItsOwnPayload()
    {
        ObsBatchBuilder builder = new();
        _ = builder.General.GetVersion();
        _ = builder.Scenes.SetCurrentProgramScene(
            new SetCurrentProgramSceneRequestData(sceneName: "Intro")
        );
        _ = builder.General.Sleep(new SleepRequestData(sleepMillis: 100));

        List<BatchRequestItem> items = builder.Build();

        Assert.AreEqual(3, items.Count);

        Assert.AreEqual("GetVersion", items[0].RequestType);
        Assert.IsNull(items[0].RequestData, "requests without fields carry no payload");

        Assert.AreEqual("SetCurrentProgramScene", items[1].RequestType);
        Assert.IsInstanceOfType<SetCurrentProgramSceneRequestData>(items[1].RequestData);

        Assert.AreEqual("Sleep", items[2].RequestType);
        Assert.IsInstanceOfType<SleepRequestData>(items[2].RequestData);
    }

    [TestMethod]
    public void Builder_ReturnsReferencesInSubmissionOrder()
    {
        ObsBatchBuilder builder = new();
        BatchRef<GetVersionResponseData> version = builder.General.GetVersion();
        BatchRef<GetStatsResponseData> stats = builder.General.GetStats();
        BatchRef<GetSceneListResponseData> scenes = builder.Scenes.GetSceneList(
            new GetSceneListRequestData()
        );

        Assert.AreEqual(0, version.Index);
        Assert.AreEqual(1, stats.Index);
        Assert.AreEqual(2, scenes.Index);

        CollectionAssert.AreEqual(
            new[] { "GetVersion", "GetStats", "GetSceneList" },
            builder.Build().Select(i => i.RequestType).ToArray()
        );
    }

    [TestMethod]
    public void Builder_RepeatedRequestType_YieldsDistinctReferences()
    {
        ObsBatchBuilder builder = new();
        BatchRef<GetSceneItemListResponseData> first = builder.SceneItems.GetSceneItemList(
            new GetSceneItemListRequestData(sceneName: "Intro")
        );
        BatchRef<GetSceneItemListResponseData> second = builder.SceneItems.GetSceneItemList(
            new GetSceneItemListRequestData(sceneName: "Outro")
        );

        Assert.AreNotEqual(first, second);
        Assert.AreEqual(0, first.Index);
        Assert.AreEqual(1, second.Index);
    }

    [TestMethod]
    public void Add_AcceptsRawRequestTypesAndPayloads()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"inputName":"Mic"}""");

        ObsBatchBuilder builder = new();
        _ = builder.Add("GetStats");
        _ = builder.Add("SetInputSettings", doc.RootElement.Clone());

        List<BatchRequestItem> items = builder.Build();
        Assert.AreEqual("GetStats", items[0].RequestType);
        Assert.IsNull(items[0].RequestData);
        Assert.AreEqual("SetInputSettings", items[1].RequestType);
        Assert.IsInstanceOfType<JsonElement>(items[1].RequestData);
    }

    [TestMethod]
    public void Build_ReturnsIndependentCopies()
    {
        ObsBatchBuilder builder = new();
        _ = builder.General.GetVersion();

        List<BatchRequestItem> first = builder.Build();
        _ = builder.General.GetStats();

        Assert.AreEqual(1, first.Count, "an already-built list must not grow with the builder");
        Assert.AreEqual(2, builder.Build().Count);
    }

    [TestMethod]
    public void TypedMethod_WithNullPayload_Throws() =>
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new ObsBatchBuilder().Scenes.SetCurrentProgramScene(null!)
        );
}

/// <summary>
/// Covers the typed enums generated for string-valued protocol enums, and their conversion
/// back to the wire strings OBS expects.
/// </summary>
[TestClass]
public sealed class ProtocolEnumTests
{
    [TestMethod]
    public void OutputState_RoundTripsThroughWireValues()
    {
        foreach (OutputState value in Enum.GetValues<OutputState>())
        {
            string wire = value.ToWireValue();
            Assert.AreEqual(value, OutputStateExtensions.FromWireValue(wire), $"round trip for {value}");
        }
    }

    [TestMethod]
    public void MediaInputAction_RoundTripsThroughWireValues()
    {
        foreach (MediaInputAction value in Enum.GetValues<MediaInputAction>())
        {
            string wire = value.ToWireValue();
            Assert.AreEqual(value, MediaInputActionExtensions.FromWireValue(wire), $"round trip for {value}");
        }
    }

    [TestMethod]
    public void ToWireValue_MatchesTheProtocolConstants()
    {
        Assert.AreEqual(ObsOutputState.OBS_WEBSOCKET_OUTPUT_STARTED, OutputState.Started.ToWireValue());
        Assert.AreEqual(
            ObsMediaInputAction.OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PLAY,
            MediaInputAction.Play.ToWireValue()
        );
    }

    [TestMethod]
    public void FromWireValue_WithUnknownOrNullInput_ReturnsNull()
    {
        Assert.IsNull(OutputStateExtensions.FromWireValue("OBS_WEBSOCKET_OUTPUT_NOT_A_STATE"));
        Assert.IsNull(OutputStateExtensions.FromWireValue(null));
        Assert.IsNull(MediaInputActionExtensions.FromWireValue("nonsense"));
    }

    [TestMethod]
    public void ProtocolConstants_UsableAsConstantPatterns()
    {
        // These are const rather than static readonly, which is what lets them appear as
        // constant patterns in a switch. This would not compile against static readonly.
        static bool IsRunning(string wire) =>
            wire switch
            {
                ObsOutputState.OBS_WEBSOCKET_OUTPUT_STARTED => true,
                ObsOutputState.OBS_WEBSOCKET_OUTPUT_RECONNECTED => true,
                _ => false,
            };

        Assert.IsTrue(IsRunning(OutputState.Started.ToWireValue()));
        Assert.IsFalse(IsRunning(OutputState.Stopped.ToWireValue()));
    }
}
