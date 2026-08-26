using System.Text.Json;
using MessagePack;
using ObsWebSocket.Core.Protocol.Responses;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Tests;

/// <summary>
/// An array whose item type the protocol does not state is generated as a list of
/// <see cref="JsonElement"/>. Nothing in the MessagePack resolver chain could build a formatter for
/// one, so <c>GetCanvasList</c> could not be read at all on that transport while JSON read it fine.
/// </summary>
[TestClass]
public sealed class JsonElementListTests
{
    [TestMethod]
    public void MsgPack_RoundTripsAListOfJsonElement()
    {
        using JsonDocument doc = JsonDocument.Parse(
            """{"canvasName":"Main","canvasVideoSettings":{"baseWidth":1920,"fpsNumerator":30}}"""
        );
        GetCanvasListResponseData original = new() { Canvases = [doc.RootElement.Clone()] };

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
        Assert.AreEqual("Main", read.Canvases[0].GetProperty("canvasName").GetString());
        Assert.AreEqual(
            1920,
            read.Canvases[0].GetProperty("canvasVideoSettings").GetProperty("baseWidth").GetInt32(),
            "a nested object inside the element has to survive too"
        );
    }

    [TestMethod]
    public void MsgPack_EmptyListRoundTrips()
    {
        GetCanvasListResponseData original = new() { Canvases = [] };

        byte[] packed = MessagePackSerializer.Serialize(
            original,
            MsgPackMessageSerializer.s_msgPackOptions
        );

        Assert.AreEqual(
            0,
            MessagePackSerializer
                .Deserialize<GetCanvasListResponseData>(
                    packed,
                    MsgPackMessageSerializer.s_msgPackOptions
                )
                .Canvases.Count
        );
    }
}
