using System.Buffers;
using System.Diagnostics.Metrics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Events;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Protocol;
using ObsWebSocket.Core.Protocol.Common;
using ObsWebSocket.Core.Protocol.Events;
using ObsWebSocket.Core.Protocol.Generated;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Tests;

/// <summary>
/// Invariants the client has to keep: what it accepts off the wire, what it serializes
/// re-identification against, what it treats as per-connection state, and what it reports.
/// </summary>
[TestClass]
public sealed class ClientContractTests
{
    private const int TimeoutMs = 20_000;

    #region Inbound message size limit

    /// <summary>
    /// An endless fragmented message stops at the ceiling instead of allocating without bound.
    /// </summary>
    [TestMethod]
    [Timeout(TimeoutMs)]
    public async Task ReceiveLoop_MessageExceedingTheLimit_FailsInsteadOfGrowing()
    {
        const int limit = 32 * 1024;
        long bytesProduced = 0;

        (ObsWebSocketClient client, Mock<IWebSocketConnection> socket, _, _) =
            TestUtils.BuildMockedClientInfrastructure(o => o.MaxIncomingMessageBytes = limit);

        socket.Reset();
        _ = socket.SetupGet(c => c.State).Returns(WebSocketState.Open);
        _ = socket.Setup(c => c.Abort());
        _ = socket.Setup(c => c.Dispose());

        // Never sets EndOfMessage.
        _ = socket
            .Setup(c => c.ReceiveAsync(It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(
                (Memory<byte> buffer, CancellationToken ct) =>
                {
                    buffer.Span.Fill(0x41);
                    _ = Interlocked.Add(ref bytesProduced, buffer.Length);
                    return new ValueTask<ValueWebSocketReceiveResult>(
                        new ValueWebSocketReceiveResult(
                            buffer.Length,
                            WebSocketMessageType.Text,
                            endOfMessage: false
                        )
                    );
                }
            );

        using CancellationTokenSource lifetime = new();
        ObsConnectionContext connection = TestUtils.CreateConnectionContext(
            socket.Object,
            Mock.Of<IWebSocketMessageSerializer>(),
            lifetime.Token
        );
        TestUtils.SetPrivateField(client, "_clientLifetimeCts", lifetime);
        TestUtils.SetPrivateField(client, "_connection", connection);

        ObsWebSocketMessageTooLargeException thrown =
            await Assert.ThrowsExactlyAsync<ObsWebSocketMessageTooLargeException>(() =>
                RunReceiveLoopAsync(client, connection)
            );

        Assert.AreEqual(limit, thrown.MaxBytes);
        Assert.IsGreaterThan(limit, thrown.AttemptedBytes, "The limit should have been crossed.");

        // Stops within one receive buffer of the ceiling, not some multiple of it.
        Assert.IsLessThanOrEqualTo(
            limit + ObsWebSocketClient.ReceiveBufferSize,
            Interlocked.Read(ref bytesProduced),
            "The loop read well past the limit before stopping."
        );
    }

    /// <summary>
    /// A message that fits is still delivered, so the limit is a ceiling and not a throttle.
    /// </summary>
    [TestMethod]
    [Timeout(TimeoutMs)]
    public async Task ReceiveLoop_MessageWithinTheLimit_IsDelivered()
    {
        byte[] payload = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(
                new IncomingMessage<JsonElement>(
                    WebSocketOpCode.Hello,
                    JsonDocument.Parse("""{"rpcVersion":1}""").RootElement.Clone()
                ),
                TestUtils.s_jsonSerializerOptions
            )
        );

        (ObsWebSocketClient client, Mock<IWebSocketConnection> socket, _, _) =
            TestUtils.BuildMockedClientInfrastructure(o =>
                o.MaxIncomingMessageBytes = ObsWebSocketClient.ReceiveBufferSize
            );

        socket.Reset();
        _ = socket.SetupGet(c => c.State).Returns(WebSocketState.Open);
        _ = socket.Setup(c => c.Abort());
        _ = socket.Setup(c => c.Dispose());
        _ = socket.SetupGet(c => c.CloseStatus).Returns((WebSocketCloseStatus?)null);
        _ = socket.SetupGet(c => c.CloseStatusDescription).Returns((string?)null);
        SetupFragmentedThenClose(socket, payload, fragmentSize: 8);

        using CancellationTokenSource lifetime = new();
        ObsConnectionContext connection = TestUtils.CreateConnectionContext(
            socket.Object,
            new JsonMessageSerializer(NullLogger<JsonMessageSerializer>.Instance),
            lifetime.Token
        );
        TestUtils.SetPrivateField(client, "_clientLifetimeCts", lifetime);
        TestUtils.SetPrivateField(client, "_connection", connection);

        await RunReceiveLoopAsync(client, connection);

        Assert.IsTrue(
            connection.Hello.Task.IsCompletedSuccessfully,
            "A message assembled from many small fragments should still have been dispatched."
        );
    }

    #endregion

    #region Re-identification single flight

    /// <summary>
    /// Two concurrent re-identifications do not overlap on one connection.
    /// </summary>
    /// <remarks>
    /// <c>Identified</c> carries no request id, so overlapping operations cannot be matched to
    /// their replies.
    /// </remarks>
    [TestMethod]
    [Timeout(TimeoutMs)]
    public async Task ReidentifyAsync_TwoConcurrentCallers_AreSerialized()
    {
        (
            ObsWebSocketClient client,
            Mock<IWebSocketMessageSerializer> serializer,
            Mock<IWebSocketConnection> socket
        ) = TestUtils.SetupConnectedClientForceState();

        int inFlight = 0;
        int maxObservedInFlight = 0;
        TaskCompletionSource firstSendObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        _ = serializer
            .Setup(s =>
                s.SerializeAsync(
                    It.IsAny<OutgoingMessage<ReidentifyPayload>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                async (OutgoingMessage<ReidentifyPayload> _, CancellationToken _) =>
                {
                    int now = Interlocked.Increment(ref inFlight);
                    _ = InterlockedMax(ref maxObservedInFlight, now);
                    _ = firstSendObserved.TrySetResult();

                    // Held open so a missing gate would put both callers here at once.
                    await Task.Delay(150);
                    _ = Interlocked.Decrement(ref inFlight);
                    return [];
                }
            );

        ObsConnectionContext connection = TestUtils.GetPrivateField<ObsConnectionContext>(
            client,
            "_connection"
        )!;

        // Answers whichever waiter is installed, as the receive loop does.
        using CancellationTokenSource replies = new();
        Task replyPump = Task.Run(
            async () =>
            {
                while (!replies.IsCancellationRequested)
                {
                    _ = connection.Identified.TrySetResult(IdentifiedEnvelope());
                    await Task.Delay(10, CancellationToken.None);
                }
            },
            CancellationToken.None
        );

        _ = serializer
            .Setup(s => s.DeserializePayload<IdentifiedPayload>(It.IsAny<object?>()))
            .Returns(new IdentifiedPayload(1));

        // Released together; sequential calls could not overlap.
        Task first = client.ReidentifyAsync((uint)EventSubscription.All);
        Task second = client.ReidentifyAsync((uint)EventSubscription.General);
        await firstSendObserved.Task;
        await Task.WhenAll(first, second);

        await replies.CancelAsync();
        await replyPump.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        Assert.AreEqual(
            1,
            Volatile.Read(ref maxObservedInFlight),
            "Two re-identifications were in flight at once on one connection."
        );
    }

    #endregion

    #region Configuration is not mutated

    /// <summary>
    /// Connecting does not write back onto the options object it was handed, which under
    /// <c>IOptionsMonitor</c> is shared with every other reader.
    /// </summary>
    [TestMethod]
    [Timeout(TimeoutMs)]
    public async Task ConnectAsync_WithAnOutOfRangeMultiplier_LeavesTheOptionsAlone()
    {
        ObsWebSocketClientOptions options = new()
        {
            ServerUri = new Uri("ws://testhost:4455"),
            ReconnectBackoffMultiplier = 0.25,
            AutoReconnectEnabled = false,
            MaxReconnectAttempts = 0,
            HandshakeTimeoutMs = 50,
        };

        Mock<IWebSocketConnection> socket = new(MockBehavior.Loose);
        _ = socket.SetupGet(c => c.State).Returns(WebSocketState.Closed);
        _ = socket.SetupGet(c => c.Options).Returns(new ClientWebSocket().Options);
        _ = socket
            .Setup(c => c.ConnectAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WebSocketException("refused"));

        Mock<IWebSocketConnectionFactory> factory = new(MockBehavior.Strict);
        _ = factory.Setup(f => f.CreateConnection()).Returns(socket.Object);

        await using ObsWebSocketClient client = new(
            NullLogger<ObsWebSocketClient>.Instance,
            _ =>
                Mock.Of<IWebSocketMessageSerializer>(s =>
                    s.ProtocolSubProtocol == "obswebsocket.json"
                ),
            Options.Create(options),
            factory.Object
        );

        _ = await Assert.ThrowsAsync<Exception>(() => client.ConnectAsync());

        Assert.AreEqual(
            0.25,
            options.ReconnectBackoffMultiplier,
            "Connecting rewrote the caller's configuration."
        );
    }

    #endregion

    #region Named client registration

    /// <summary>
    /// A container with only named clients resolves, and each reads its own options.
    /// </summary>
    [TestMethod]
    [Timeout(TimeoutMs)]
    public async Task AddNamedClient_Only_ResolvesAndReadsItsOwnLiveOptions()
    {
        ServiceCollection services = new();
        _ = services.AddLogging();
        _ = services.AddObsWebSocketClient(
            "left",
            o =>
            {
                o.ServerUri = new Uri("ws://left:4455");
                o.RequestTimeoutMs = 1111;
            }
        );
        _ = services.AddObsWebSocketClient(
            "right",
            o =>
            {
                o.ServerUri = new Uri("ws://right:4455");
                o.RequestTimeoutMs = 2222;
            }
        );

        await using ServiceProvider provider = services.BuildServiceProvider();

        ObsWebSocketClient left = provider.GetRequiredKeyedService<ObsWebSocketClient>("left");
        ObsWebSocketClient right = provider.GetRequiredKeyedService<ObsWebSocketClient>("right");

        Assert.AreNotSame(left, right);
        Assert.AreEqual(1111, left._options.Value.RequestTimeoutMs);
        Assert.AreEqual(2222, right._options.Value.RequestTimeoutMs);

        // A snapshot would survive the cache reset; the monitor re-reads.
        provider
            .GetRequiredService<IOptionsMonitorCache<ObsWebSocketClientOptions>>()
            .TryRemove("left");
        _ = provider.GetRequiredService<IConfigureOptions<ObsWebSocketClientOptions>>().GetType();

        Assert.AreEqual(
            1111,
            left._options.Value.RequestTimeoutMs,
            "The named client should still resolve its own options after a cache reset."
        );
    }

    /// <summary>
    /// A named client must pick the serializer its own format asks for.
    /// </summary>
    [TestMethod]
    [Timeout(TimeoutMs)]
    public async Task AddNamedClient_WithDifferentFormats_EachGetsItsOwnSerializer()
    {
        ServiceCollection services = new();
        _ = services.AddLogging();
        _ = services.AddObsWebSocketClient(
            "json",
            o =>
            {
                o.ServerUri = new Uri("ws://json:4455");
                o.Format = SerializationFormat.Json;
            }
        );
        _ = services.AddObsWebSocketClient(
            "pack",
            o =>
            {
                o.ServerUri = new Uri("ws://pack:4455");
                o.Format = SerializationFormat.MsgPack;
            }
        );

        await using ServiceProvider provider = services.BuildServiceProvider();

        ObsSerializerFactory factory = provider.GetRequiredService<ObsSerializerFactory>();
        _ = Assert.IsInstanceOfType<JsonMessageSerializer>(factory(SerializationFormat.Json));
        _ = Assert.IsInstanceOfType<MsgPackMessageSerializer>(factory(SerializationFormat.MsgPack));
    }

    #endregion

    #region Transport switching

    /// <summary>
    /// The serializer follows the configured format at connection time, not at registration time.
    /// </summary>
    [TestMethod]
    [Timeout(TimeoutMs)]
    public async Task ChangingFormat_ChangesTheSerializerTheNextConnectionUses()
    {
        List<SerializationFormat> requested = [];

        ObsWebSocketClientOptions options = new()
        {
            ServerUri = new Uri("ws://testhost:4455"),
            Format = SerializationFormat.Json,
            AutoReconnectEnabled = false,
            MaxReconnectAttempts = 0,
            HandshakeTimeoutMs = 50,
        };

        Mock<IWebSocketConnectionFactory> factory = new(MockBehavior.Strict);
        _ = factory
            .Setup(f => f.CreateConnection())
            .Returns(() =>
            {
                Mock<IWebSocketConnection> socket = new(MockBehavior.Loose);
                _ = socket.SetupGet(c => c.State).Returns(WebSocketState.Closed);
                _ = socket.SetupGet(c => c.Options).Returns(new ClientWebSocket().Options);
                _ = socket
                    .Setup(c => c.ConnectAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new WebSocketException("refused"));
                return socket.Object;
            });

        await using ObsWebSocketClient client = new(
            NullLogger<ObsWebSocketClient>.Instance,
            format =>
            {
                requested.Add(format);
                return Mock.Of<IWebSocketMessageSerializer>(s =>
                    s.ProtocolSubProtocol
                    == (
                        format == SerializationFormat.MsgPack
                            ? "obswebsocket.msgpack"
                            : "obswebsocket.json"
                    )
                );
            },
            Options.Create(options),
            factory.Object
        );

        _ = await Assert.ThrowsAsync<Exception>(() => client.ConnectAsync());
        options.Format = SerializationFormat.MsgPack;
        _ = await Assert.ThrowsAsync<Exception>(() => client.ConnectAsync());

        CollectionAssert.AreEqual(
            new[] { SerializationFormat.Json, SerializationFormat.MsgPack },
            requested,
            "The second connection should have been built for the newly configured format."
        );
    }

    #endregion

    #region Event drop telemetry

    /// <summary>
    /// A stream that overflows reports how many events it discarded.
    /// </summary>
    /// <remarks>
    /// Dropping is intended; <c>TryWrite</c> reporting success on eviction is what makes it
    /// invisible without a counter.
    /// </remarks>
    [TestMethod]
    [Timeout(TimeoutMs)]
    public async Task EventStream_WhenTheConsumerFallsBehind_CountsTheDrops()
    {
        using MeterFactoryStub meterFactory = new();
        using ObsWebSocketMetrics metrics = new(meterFactory);

        long dropped = 0;
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "obsws.events.dropped")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (_, measurement, _, _) => Interlocked.Add(ref dropped, measurement)
        );
        listener.Start();

        EventHandler<CurrentProgramSceneChangedEventArgs>? handler = null;
        IAsyncEnumerable<CurrentProgramSceneChangedEventArgs> stream =
            EventStream.Create<CurrentProgramSceneChangedEventArgs>(
                h => handler = h,
                h => handler -= h,
                capacity: 1,
                metrics,
                CancellationToken.None
            );

        await using IAsyncEnumerator<CurrentProgramSceneChangedEventArgs> enumerator =
            stream.GetAsyncEnumerator();
        ValueTask<bool> pending = enumerator.MoveNextAsync();

        const int raised = 10;
        for (int i = 0; i < raised; i++)
        {
            handler!.Invoke(
                null,
                new CurrentProgramSceneChangedEventArgs(
                    new CurrentProgramSceneChangedPayload($"scene-{i}", Guid.NewGuid().ToString())
                )
            );
        }

        _ = await pending;
        listener.RecordObservableInstruments();

        Assert.IsGreaterThan(
            0,
            Interlocked.Read(ref dropped),
            "A capacity-one stream fed ten events without being read should report drops."
        );
    }

    #endregion

    #region Serializer contract

    /// <summary>
    /// Both serializers read a stream that cannot seek or report a length.
    /// </summary>
    [TestMethod]
    [Timeout(TimeoutMs)]
    public async Task Serializers_GivenANonSeekableStream_StillRead()
    {
        byte[] json = Encoding.UTF8.GetBytes("""{"op":0,"d":{"rpcVersion":1}}""");

        object? fromJson = await new JsonMessageSerializer(
            NullLogger<JsonMessageSerializer>.Instance
        ).DeserializeAsync(new ForwardOnlyStream(json));

        Assert.IsInstanceOfType<IncomingMessage<JsonElement>>(fromJson);

        byte[] pack = await new MsgPackMessageSerializer(
            NullLogger<MsgPackMessageSerializer>.Instance
        ).SerializeAsync(
            new OutgoingMessage<ReidentifyPayload>(
                WebSocketOpCode.Reidentify,
                new ReidentifyPayload(0)
            )
        );

        object? fromPack = await new MsgPackMessageSerializer(
            NullLogger<MsgPackMessageSerializer>.Instance
        ).DeserializeAsync(new ForwardOnlyStream(pack));

        Assert.IsInstanceOfType<IncomingMessage<ReadOnlyMemory<byte>>>(fromPack);
    }

    /// <summary>The memory and stream overloads produce the same envelope.</summary>
    [TestMethod]
    [Timeout(TimeoutMs)]
    public async Task Serializers_MemoryAndStreamOverloads_ProduceTheSameEnvelope()
    {
        byte[] json = Encoding.UTF8.GetBytes("""{"op":5,"d":{"eventType":"ExitStarted"}}""");
        JsonMessageSerializer serializer = new(NullLogger<JsonMessageSerializer>.Instance);

        IncomingMessage<JsonElement> viaStream =
            (IncomingMessage<JsonElement>)
                (await serializer.DeserializeAsync(new MemoryStream(json)))!;
        IncomingMessage<JsonElement> viaMemory =
            (IncomingMessage<JsonElement>)
                (await serializer.DeserializeAsync(new ReadOnlyMemory<byte>(json)))!;

        Assert.AreEqual(viaStream.Op, viaMemory.Op);
        Assert.AreEqual(viaStream.D.GetRawText(), viaMemory.D.GetRawText());
    }

    #endregion

    #region Helpers

    private static Task RunReceiveLoopAsync(
        ObsWebSocketClient client,
        ObsConnectionContext connection
    )
    {
        Func<ObsConnectionContext, Task> loop = TestUtils.GetPrivateMethodDelegate<
            Func<ObsConnectionContext, Task>
        >(client, "ReceiveLoopAsync")!;
        return loop(connection);
    }

    private static void SetupFragmentedThenClose(
        Mock<IWebSocketConnection> socket,
        byte[] payload,
        int fragmentSize
    )
    {
        int offset = 0;
        _ = socket
            .Setup(c => c.ReceiveAsync(It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(
                (Memory<byte> buffer, CancellationToken _) =>
                {
                    if (offset >= payload.Length)
                    {
                        return new ValueTask<ValueWebSocketReceiveResult>(
                            new ValueWebSocketReceiveResult(
                                0,
                                WebSocketMessageType.Close,
                                endOfMessage: true
                            )
                        );
                    }

                    int count = Math.Min(fragmentSize, payload.Length - offset);
                    payload.AsSpan(offset, count).CopyTo(buffer.Span);
                    offset += count;

                    return new ValueTask<ValueWebSocketReceiveResult>(
                        new ValueWebSocketReceiveResult(
                            count,
                            WebSocketMessageType.Text,
                            endOfMessage: offset >= payload.Length
                        )
                    );
                }
            );
    }

    private static object IdentifiedEnvelope() =>
        new IncomingMessage<JsonElement>(
            WebSocketOpCode.Identified,
            JsonDocument.Parse("""{"negotiatedRpcVersion":1}""").RootElement.Clone()
        );

    private static int InterlockedMax(ref int target, int value)
    {
        int seen = Volatile.Read(ref target);
        while (value > seen)
        {
            int previous = Interlocked.CompareExchange(ref target, value, seen);
            if (previous == seen)
            {
                return value;
            }

            seen = previous;
        }

        return seen;
    }

    /// <summary>A stream that cannot seek and refuses to report a length.</summary>
    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>A meter factory owning its meters, so a test can read instruments back.</summary>
    private sealed class MeterFactoryStub : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            Meter meter = new(options);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (Meter meter in _meters)
            {
                meter.Dispose();
            }

            _meters.Clear();
        }
    }

    #endregion
}
