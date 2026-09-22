using System.Text;
using System.Text.Json;
using MessagePack;
using Microsoft.Extensions.Logging;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Tests;

/// <summary>
/// The serializer paths the fake server cannot reach: streams, failed writes, fields a newer OBS
/// adds, and the diagnostics that only run with tracing on.
/// </summary>
[TestClass]
public sealed class SerializerEdgeTests
{
    /// <summary>A logger with every level on, so the diagnostics behind level checks run.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Messages.Add(formatter(state, exception));
    }

    /// <summary>A stream that cannot seek, like a network stream.</summary>
    private sealed class ForwardOnlyStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    /// <summary>A payload type neither serializer has metadata for.</summary>
    private sealed record Unregistered(int Value);

    public TestContext TestContext { get; set; } = null!;

    #region JSON

    [TestMethod]
    public async Task JsonDeserializeAsync_NonSeekableStream_Reads()
    {
        RecordingLogger<JsonMessageSerializer> logger = new();
        JsonMessageSerializer json = new(logger);
        byte[] frame = Encoding.UTF8.GetBytes("""{"op":2,"d":{"negotiatedRpcVersion":1}}""");

        object? message = await json.DeserializeAsync(
            new ForwardOnlyStream(frame),
            TestContext.CancellationToken
        );

        Assert.AreEqual(WebSocketOpCode.Identified, ((IncomingMessage<JsonElement>)message!).Op);
        Assert.IsNotEmpty(logger.Messages, "tracing is on, so the parse is logged");
    }

    [TestMethod]
    public async Task JsonDeserializeAsync_EmptyOrUnreadable_ReturnsNull()
    {
        RecordingLogger<JsonMessageSerializer> logger = new();
        JsonMessageSerializer json = new(logger);

        Assert.IsNull(
            await json.DeserializeAsync(new MemoryStream(), TestContext.CancellationToken)
        );
        Assert.IsNull(
            await json.DeserializeAsync(ReadOnlyMemory<byte>.Empty, TestContext.CancellationToken)
        );

        string longGarbage = "{" + new string('x', 2048);
        Assert.IsNull(
            await json.DeserializeAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(longGarbage)),
                TestContext.CancellationToken
            )
        );
        Assert.IsNull(
            await json.DeserializeAsync(
                Encoding.UTF8.GetBytes(longGarbage),
                TestContext.CancellationToken
            )
        );
        Assert.IsNull(
            await json.DeserializeAsync(
                new MemoryStream("null"u8.ToArray()),
                TestContext.CancellationToken
            )
        );
        Assert.IsNull(
            await json.DeserializeAsync("null"u8.ToArray(), TestContext.CancellationToken)
        );

        // The raw text is kept for the log, but cut short rather than pasted whole.
        Assert.IsTrue(logger.Messages.Any(m => m.Contains("...", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task JsonSerializeAsync_UnregisteredType_ThrowsSerialization()
    {
        JsonMessageSerializer json = new(new RecordingLogger<JsonMessageSerializer>());

        _ = await Assert.ThrowsExactlyAsync<ObsWebSocketSerializationException>(() =>
            json.SerializeAsync(
                new OutgoingMessage<Unregistered>(WebSocketOpCode.Request, new Unregistered(1)),
                TestContext.CancellationToken
            )
        );
    }

    [TestMethod]
    public void JsonDeserializeValuePayload_Unreadable_Reports()
    {
        RecordingLogger<JsonMessageSerializer> logger = new();
        JsonMessageSerializer json = new(logger);
        JsonElement text = JsonDocument.Parse("\"not a number\"").RootElement.Clone();

        Assert.IsNull(json.DeserializeValuePayload<int>("not an element"));
        Assert.IsNull(json.DeserializeValuePayload<int>(default(JsonElement)));
        _ = Assert.ThrowsExactly<ObsWebSocketSerializationException>(() =>
            json.DeserializeValuePayload<int>(text)
        );

        Assert.IsFalse(json.TryDeserializeValuePayload(text, out int? value));
        Assert.IsNull(value);
        Assert.IsTrue(
            json.TryDeserializeValuePayload(
                JsonDocument.Parse("42").RootElement.Clone(),
                out int? read
            )
        );
        Assert.AreEqual(42, read);
    }

    [TestMethod]
    public void JsonDeserializeValuePayload_LargePayload_TruncatesMessage()
    {
        JsonMessageSerializer json = new(new RecordingLogger<JsonMessageSerializer>());
        JsonElement large = JsonDocument
            .Parse($$"""{"value":"{{new string('x', 4096)}}"}""")
            .RootElement.Clone();

        ObsWebSocketSerializationException error =
            Assert.ThrowsExactly<ObsWebSocketSerializationException>(() =>
                json.DeserializeValuePayload<int>(large)
            );

        Assert.IsLessThan(1024, error.Message.Length);
        Assert.EndsWith("...", error.Message);
    }

    #endregion

    #region MessagePack

    private static MsgPackMessageSerializer MsgPack(
        RecordingLogger<MsgPackMessageSerializer>? logger = null
    ) => new(logger ?? new RecordingLogger<MsgPackMessageSerializer>());

    private static async Task<IncomingMessage<ReadOnlyMemory<byte>>> ReadEnvelopeAsync(
        MsgPackMessageSerializer serializer,
        string json,
        CancellationToken cancellationToken
    ) =>
        (IncomingMessage<ReadOnlyMemory<byte>>)
            (
                await serializer.DeserializeAsync(
                    MessagePackSerializer.ConvertFromJson(json),
                    cancellationToken
                )
            )!;

    [TestMethod]
    public async Task MsgPackTryDeserializePayload_UnknownFields_SkipsThem()
    {
        RecordingLogger<MsgPackMessageSerializer> logger = new();
        MsgPackMessageSerializer serializer = MsgPack(logger);
        CancellationToken token = TestContext.CancellationToken;

        IncomingMessage<ReadOnlyMemory<byte>> ev = await ReadEnvelopeAsync(
            serializer,
            """{"op":5,"future":true,"d":{"eventType":"StudioModeStateChanged","eventIntent":1,"future":[1,2],"eventData":{"studioModeEnabled":true}}}""",
            token
        );
        Assert.IsTrue(
            serializer.TryDeserializePayload(ev.D, out EventPayloadBase<object>? eventPayload)
        );
        Assert.AreEqual("StudioModeStateChanged", eventPayload!.EventType);

        IncomingMessage<ReadOnlyMemory<byte>> response = await ReadEnvelopeAsync(
            serializer,
            """{"op":7,"d":{"requestType":"GetVersion","requestId":"1","future":{"a":1},"requestStatus":{"result":true,"code":100},"responseData":{}}}""",
            token
        );
        Assert.IsTrue(
            serializer.TryDeserializePayload(
                response.D,
                out RequestResponsePayload<object>? responsePayload
            )
        );
        Assert.AreEqual("1", responsePayload!.RequestId);

        IncomingMessage<ReadOnlyMemory<byte>> batch = await ReadEnvelopeAsync(
            serializer,
            """{"op":9,"d":{"requestId":"b","future":"x","results":[{"requestType":"GetVersion","requestId":"b_0","requestStatus":{"result":true,"code":100}}]}}""",
            token
        );
        Assert.IsTrue(
            serializer.TryDeserializePayload(
                batch.D,
                out RequestBatchResponsePayload<object>? batchPayload
            )
        );
        Assert.HasCount(1, batchPayload!.Results);

        Assert.IsNotEmpty(logger.Messages, "tracing is on, so each parse is logged");
    }

    [TestMethod]
    public async Task MsgPackDeserializeAsync_Unreadable_ReturnsNull()
    {
        MsgPackMessageSerializer serializer = MsgPack();

        Assert.IsNull(
            await serializer.DeserializeAsync(new byte[] { 0xC1 }, TestContext.CancellationToken)
        );
        Assert.IsNull(
            await serializer.DeserializeAsync(
                ReadOnlyMemory<byte>.Empty,
                TestContext.CancellationToken
            )
        );
    }

    [TestMethod]
    public async Task MsgPackSerializeAsync_UnregisteredType_ThrowsSerialization()
    {
        MsgPackMessageSerializer serializer = MsgPack();

        _ = await Assert.ThrowsExactlyAsync<ObsWebSocketSerializationException>(() =>
            serializer.SerializeAsync(
                new OutgoingMessage<Unregistered>(WebSocketOpCode.Request, new Unregistered(1)),
                TestContext.CancellationToken
            )
        );
    }

    [TestMethod]
    public void MsgPackTryDeserializeValuePayload_Unreadable_ReturnsFalse()
    {
        MsgPackMessageSerializer serializer = MsgPack();
        ReadOnlyMemory<byte> text = MessagePackSerializer.ConvertFromJson("\"not a number\"");

        Assert.IsFalse(serializer.TryDeserializeValuePayload(text, out int? value));
        Assert.IsNull(value);
        Assert.IsTrue(
            serializer.TryDeserializeValuePayload(
                (ReadOnlyMemory<byte>)MessagePackSerializer.ConvertFromJson("42"),
                out int? read
            )
        );
        Assert.AreEqual(42, read);
    }

    #endregion
}
