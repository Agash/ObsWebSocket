using ObsWebSocket.Core.Events;
using ObsWebSocket.Core.Events.Generated;
using ObsWebSocket.Core.Protocol.Events;

namespace ObsWebSocket.Tests;

/// <summary>
/// Covers the event-to-<see cref="IAsyncEnumerable{T}"/> bridge: delivery, subscription
/// lifetime, and the drop-oldest behaviour that keeps a slow consumer from stalling the
/// receive loop.
/// </summary>
[TestClass]
public sealed class EventStreamTests
{
    private sealed class Source
    {
        public event EventHandler<CurrentProgramSceneChangedEventArgs>? Fired;

        public int HandlerCount { get; private set; }

        public void Subscribe(EventHandler<CurrentProgramSceneChangedEventArgs> handler)
        {
            Fired += handler;
            HandlerCount++;
        }

        public void Unsubscribe(EventHandler<CurrentProgramSceneChangedEventArgs> handler)
        {
            Fired -= handler;
            HandlerCount--;
        }

        public void Raise(string sceneName) =>
            Fired?.Invoke(
                this,
                new CurrentProgramSceneChangedEventArgs(
                    new CurrentProgramSceneChangedPayload(sceneName: sceneName)
                )
            );
    }

    private static IAsyncEnumerable<CurrentProgramSceneChangedEventArgs> StreamOf(
        Source source,
        int capacity,
        CancellationToken cancellationToken
    ) =>
        EventStream.Create<CurrentProgramSceneChangedEventArgs>(
            source.Subscribe,
            source.Unsubscribe,
            capacity,
            cancellationToken
        );

    [TestMethod]
    public async Task Create_RaisedEvents_AreYieldedInOrder()
    {
        Source source = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        List<string> seen = [];

        IAsyncEnumerator<CurrentProgramSceneChangedEventArgs> enumerator = StreamOf(
            source,
            capacity: 8,
            cts.Token
        ).GetAsyncEnumerator(cts.Token);

        try
        {
            // The first MoveNextAsync attaches the handler, so raise after starting it.
            ValueTask<bool> pending = enumerator.MoveNextAsync();
            source.Raise("One");
            Assert.IsTrue(await pending);
            seen.Add(enumerator.Current.EventData.SceneName!);

            source.Raise("Two");
            Assert.IsTrue(await enumerator.MoveNextAsync());
            seen.Add(enumerator.Current.EventData.SceneName!);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        CollectionAssert.AreEqual(new[] { "One", "Two" }, seen);
    }

    [TestMethod]
    public async Task Create_WhenEnumerationEnds_Unsubscribes()
    {
        Source source = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        IAsyncEnumerator<CurrentProgramSceneChangedEventArgs> enumerator = StreamOf(
            source,
            capacity: 4,
            cts.Token
        ).GetAsyncEnumerator(cts.Token);

        ValueTask<bool> pending = enumerator.MoveNextAsync();
        source.Raise("One");
        _ = await pending;
        Assert.AreEqual(1, source.HandlerCount, "handler should be attached while enumerating");

        await enumerator.DisposeAsync();
        Assert.AreEqual(0, source.HandlerCount, "handler should be detached once enumeration ends");
    }

    [TestMethod]
    public async Task Create_WhenConsumerFallsBehind_DropsOldest()
    {
        Source source = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        IAsyncEnumerator<CurrentProgramSceneChangedEventArgs> enumerator = StreamOf(
            source,
            capacity: 2,
            cts.Token
        ).GetAsyncEnumerator(cts.Token);

        try
        {
            // Attach without consuming, then overflow the buffer.
            ValueTask<bool> pending = enumerator.MoveNextAsync();
            source.Raise("One");
            _ = await pending;
            Assert.AreEqual("One", enumerator.Current.EventData.SceneName);

            source.Raise("Two");
            source.Raise("Three");
            source.Raise("Four");

            // Capacity is 2, so the oldest buffered item ("Two") is dropped.
            Assert.IsTrue(await enumerator.MoveNextAsync());
            Assert.AreEqual("Three", enumerator.Current.EventData.SceneName);
            Assert.IsTrue(await enumerator.MoveNextAsync());
            Assert.AreEqual("Four", enumerator.Current.EventData.SceneName);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Create_WhenCancelled_StopsAndUnsubscribes()
    {
        Source source = new();
        using CancellationTokenSource cts = new();

        IAsyncEnumerator<CurrentProgramSceneChangedEventArgs> enumerator = StreamOf(
            source,
            capacity: 4,
            cts.Token
        ).GetAsyncEnumerator(cts.Token);

        ValueTask<bool> pending = enumerator.MoveNextAsync();
        await cts.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await pending);
        await enumerator.DisposeAsync();

        Assert.AreEqual(0, source.HandlerCount);
    }

    [TestMethod]
    public void Create_WithInvalidCapacity_Throws() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            EventStream.Create<CurrentProgramSceneChangedEventArgs>(
                _ => { },
                _ => { },
                capacity: 0
            )
        );
}
