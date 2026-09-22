using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Responses;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Tests;

/// <summary>
/// The failure paths of both serializers: a payload that is absent, the wrong shape, or not
/// readable at all. The receive loop depends on these reporting rather than throwing.
/// </summary>
[TestClass]
public sealed class SerializerFailurePathTests
{
    private static JsonMessageSerializer Json() => new(NullLogger<JsonMessageSerializer>.Instance);

    private static MsgPackMessageSerializer MsgPack() =>
        new(NullLogger<MsgPackMessageSerializer>.Instance);

    [TestMethod]
    public void JsonDeserializePayload_Null_ReturnsNull() =>
        Assert.IsNull(Json().DeserializePayload<GetVersionResponseData>(null));

    [TestMethod]
    public void JsonDeserializePayload_WrongRuntimeType_ReturnsNull() =>
        Assert.IsNull(Json().DeserializePayload<GetVersionResponseData>("not an element"));

    [TestMethod]
    public void JsonDeserializeValuePayload_Null_ReturnsNull() =>
        Assert.IsNull(Json().DeserializeValuePayload<JsonElement>(null));

    [TestMethod]
    public void JsonTryDeserializePayload_WrongShape_ReturnsFalse()
    {
        JsonElement element = JsonDocument.Parse("""{"obsVersion":5}""").RootElement.Clone();

        bool read = Json().TryDeserializePayload(element, out GetVersionResponseData? payload);

        Assert.IsFalse(read);
        Assert.IsNull(payload);
    }

    [TestMethod]
    public void JsonDeserializePayload_WrongShape_Throws() =>
        Assert.ThrowsExactly<ObsWebSocketSerializationException>(() =>
            Json()
                .DeserializePayload<GetVersionResponseData>(
                    JsonDocument.Parse("""{"obsVersion":5}""").RootElement.Clone()
                )
        );

    [TestMethod]
    public async Task JsonDeserializeAsync_Garbage_ReturnsNull()
    {
        ReadOnlyMemory<byte> garbage = Encoding.UTF8.GetBytes("{ this is not json");

        Assert.IsNull(await Json().DeserializeAsync(garbage, TestContext.CancellationToken));
    }

    [TestMethod]
    public void MsgPackDeserializePayload_Null_ReturnsNull() =>
        Assert.IsNull(MsgPack().DeserializePayload<GetVersionResponseData>(null));

    [TestMethod]
    public void MsgPackTryDeserializePayload_WrongShape_ReturnsFalse()
    {
        ReadOnlyMemory<byte> garbage = new byte[] { 0xC1 };

        bool read = MsgPack().TryDeserializePayload(garbage, out GetVersionResponseData? payload);

        Assert.IsFalse(read);
        Assert.IsNull(payload);
    }

    [TestMethod]
    public async Task MsgPackDeserializeAsync_Garbage_ReturnsNull()
    {
        ReadOnlyMemory<byte> garbage = new byte[] { 0xC1, 0xC1 };

        Assert.IsNull(await MsgPack().DeserializeAsync(garbage, TestContext.CancellationToken));
    }

    [TestMethod]
    public void ProtocolSubProtocol_BothSerializers_ReportTheirName()
    {
        Assert.AreEqual("obswebsocket.json", Json().ProtocolSubProtocol);
        Assert.AreEqual("obswebsocket.msgpack", MsgPack().ProtocolSubProtocol);
    }

    /// <summary>The context MSTest assigns, used for cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;
}
