using System.Net.WebSockets;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ObsWebSocket.Core;
using ObsWebSocket.Core.Networking;
using ObsWebSocket.Core.Protocol.Responses;
using ObsWebSocket.Core.Serialization;

namespace ObsWebSocket.Tests;

/// <summary>
/// Verifies that request timeouts are driven by the injected <see cref="TimeProvider"/>.
/// </summary>
[TestClass]
public sealed class TimeProviderTests
{
    [TestMethod]
    public async Task RequestTimeout_ElapsesOnlyWhenTheProviderAdvances()
    {
        FakeTimeProvider time = new();
        (
            ObsWebSocketClient client,
            Mock<IWebSocketMessageSerializer> _,
            Mock<IWebSocketConnection> mockWebSocket
        ) = TestUtils.SetupConnectedClientForceState(time);

        // Accept the send but never deliver a response, so only the timeout can complete it.
        _ = mockWebSocket
            .Setup(ws =>
                ws.SendAsync(
                    It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<WebSocketMessageType>(),
                    true,
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(ValueTask.CompletedTask);

        Task<GetVersionResponseData?> pending = client.CallAsync<GetVersionResponseData>("GetVersion", null, timeoutMs: 5000);

        await Task.Delay(50, TestContext.CancellationTokenSource.Token);
        Assert.IsFalse(pending.IsCompleted, "wall-clock time must not advance the timeout");

        time.Advance(TimeSpan.FromMilliseconds(4999));
        await Task.Delay(50, TestContext.CancellationTokenSource.Token);
        Assert.IsFalse(pending.IsCompleted, "the timeout must not fire early");

        time.Advance(TimeSpan.FromMilliseconds(2));

        ObsWebSocketException ex = await Assert.ThrowsExactlyAsync<ObsWebSocketException>(
            async () => await pending
        );
        StringAssert.Contains(ex.Message, "timed out");

        await client.DisposeAsync();
    }

    public TestContext TestContext { get; set; } = null!;
}
