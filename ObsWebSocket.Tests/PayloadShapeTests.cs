using System.Text.Json;
using MessagePack;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Responses;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Tests;

/// <summary>
/// Reading a batch payload as the wrong response record used to be silent on MessagePack, which
/// maps by key name and leaves everything unmatched at its default, so a fabricated reading of
/// cpu=0 looked genuine. JSON was silent too for the response records with no required member,
/// which is most of them.
/// </summary>
[TestClass]
public sealed class PayloadShapeTests
{
    private static RequestResponsePayload<object> Row(object payload) =>
        new("GetVersion", "id", new RequestStatus(true, 100), payload);

    [TestMethod]
    public void MsgPack_ReadingAPayloadAsTheWrongRecord_Throws()
    {
        GetVersionResponseData version = TestUtils.SampleVersion();
        byte[] packed = MessagePackSerializer.Serialize(
            version,
            MsgPackMessageSerializer.s_msgPackOptions
        );

        RequestResponsePayload<object> row = Row(new ReadOnlyMemory<byte>(packed));

        _ = Assert.ThrowsExactly<ObsWebSocketSerializationException>(() =>
            row.GetData<GetStatsResponseData>()
        );
    }

    [TestMethod]
    public void Json_ReadingAPayloadAsTheWrongRecord_Throws()
    {
        // GetSceneListResponseData has no required member, so System.Text.Json alone accepted this.
        using JsonDocument doc = JsonDocument.Parse("""{"cpuUsage":0.5,"memoryUsage":300.0}""");
        RequestResponsePayload<object> row = Row(doc.RootElement.Clone());

        _ = Assert.ThrowsExactly<ObsWebSocketSerializationException>(() =>
            row.GetData<GetSceneListResponseData>()
        );
    }

    [TestMethod]
    public void ReadingAPayloadAsItsOwnRecord_StillWorks()
    {
        GetVersionResponseData version = TestUtils.SampleVersion();
        byte[] packed = MessagePackSerializer.Serialize(
            version,
            MsgPackMessageSerializer.s_msgPackOptions
        );

        GetVersionResponseData? read = Row(new ReadOnlyMemory<byte>(packed))
            .GetData<GetVersionResponseData>();

        Assert.AreEqual("32.2.2", read?.ObsVersion);
    }

    [TestMethod]
    public void RecordsWithAnIdenticalShape_AreStillInterchangeable()
    {
        // GetInputMute and ToggleInputMute are both a single inputMuted field, so reading one as
        // the other gives the right value. The check must not reject that.
        using JsonDocument doc = JsonDocument.Parse("""{"inputMuted":true}""");
        RequestResponsePayload<object> row = Row(doc.RootElement.Clone());

        ToggleInputMuteResponseData? read = row.GetData<ToggleInputMuteResponseData>();

        Assert.IsTrue(read?.InputMuted);
    }
}
