using System.Text.Json;
using MessagePack;
using ObsWebSocket.Core.Protocol.Events;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Tests;

/// <summary>
/// Protocol enums that travel as strings are generated as C# enums, so the value on the wire has
/// to stay the protocol string on both transports. MessagePack would otherwise write the ordinal,
/// which OBS rejects, and which no round trip through this library alone would notice.
/// </summary>
[TestClass]
public sealed class WireEnumTests
{
    [TestMethod]
    public void Json_WritesTheProtocolStringNotTheMemberName()
    {
        TriggerMediaInputActionRequestData request = new(
            mediaAction: MediaInputAction.Stop,
            inputName: "Stinger"
        );

        string json = JsonSerializer.Serialize(
            request,
            ObsWebSocketJsonContext.Default.TriggerMediaInputActionRequestData
        );

        StringAssert.Contains(json, "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_STOP");
        Assert.IsFalse(json.Contains("\"Stop\"", StringComparison.Ordinal), "member name leaked");
    }

    [TestMethod]
    public void MsgPack_WritesTheProtocolStringNotTheOrdinal()
    {
        TriggerMediaInputActionRequestData request = new(
            mediaAction: MediaInputAction.Stop,
            inputName: "Stinger"
        );

        byte[] bytes = MessagePackSerializer.Serialize(
            request,
            MsgPackMessageSerializer.s_msgPackOptions
        );

        // Converting to JSON shows what actually landed in the buffer.
        string asJson = MessagePackSerializer.ConvertToJson(
            bytes,
            MsgPackMessageSerializer.s_msgPackOptions
        );

        StringAssert.Contains(asJson, "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_STOP");
    }

    [TestMethod]
    public void MsgPack_ReadsTheProtocolStringBackIntoTheEnum()
    {
        StreamStateChangedPayload payload = new(
            outputActive: true,
            outputState: OutputState.Reconnecting
        );

        byte[] bytes = MessagePackSerializer.Serialize(
            payload,
            MsgPackMessageSerializer.s_msgPackOptions
        );
        StringAssert.Contains(
            MessagePackSerializer.ConvertToJson(bytes, MsgPackMessageSerializer.s_msgPackOptions),
            "OBS_WEBSOCKET_OUTPUT_RECONNECTING"
        );

        StreamStateChangedPayload read =
            MessagePackSerializer.Deserialize<StreamStateChangedPayload>(
                bytes,
                MsgPackMessageSerializer.s_msgPackOptions
            );

        Assert.AreEqual(OutputState.Reconnecting, read.OutputState);
    }

    [TestMethod]
    public void UnrecognisedWireValue_FallsBackToTheZeroMemberRatherThanThrowing()
    {
        // A state added by a newer OBS must not fail the whole message.
        string json = """{"outputActive":true,"outputState":"OBS_WEBSOCKET_OUTPUT_FUTURE_STATE"}""";

        StreamStateChangedPayload? read = JsonSerializer.Deserialize(
            json,
            ObsWebSocketJsonContext.Default.StreamStateChangedPayload
        );

        Assert.IsNotNull(read);
        Assert.AreEqual(OutputState.Unknown, read.OutputState);
        Assert.IsTrue(read.OutputActive);
    }
}
