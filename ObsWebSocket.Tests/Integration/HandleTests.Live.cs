using ObsWebSocket.Core;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Tests.Integration;

/// <summary>
/// Resolves handles against a live OBS. A name resolves to a uuid, the uuid survives a rename,
/// and a miss reports what does exist.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
public sealed class LiveHandleTests
{
    private const int TimeoutMs = 120_000;

    /// <summary>The context MSTest assigns, used for cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(TimeoutMs, CooperativeCancellation = true)]
    public async Task SceneHandles_LiveObs_Resolve()
    {
        CancellationToken token = TestContext.CancellationToken;
        await using LiveClient live = await LiveClient
            .ConnectAsync(SerializationFormat.Json, TestContext)
            .ConfigureAwait(false);

        string scene = $"__obsws_handle_{Guid.NewGuid():N}"[..28];
        await live
            .Client.Scenes.CreateSceneAsync(new(sceneName: scene), token)
            .ConfigureAwait(false);

        try
        {
            SceneOperations resolved = await live
                .Client.Scene(scene)
                .ResolveAsync(token)
                .ConfigureAwait(false);

            Assert.IsTrue(resolved.Handle.IsResolved);

            await live
                .Client.Inputs.CreateInputAsync(
                    new(inputName: $"{scene}_in", inputKind: live.Kinds.Color!, sceneName: scene),
                    token
                )
                .ConfigureAwait(false);

            GetSceneItemListResponseData items = await live
                .Client.Scene(resolved.Handle)
                .GetItemListAsync(token)
                .ConfigureAwait(false);
            Assert.IsNotEmpty(items.SceneItems);

            SceneItemOperations item = await live
                .Client.Scene(resolved.Handle)
                .ItemAsync($"{scene}_in", cancellationToken: token)
                .ConfigureAwait(false);
            Assert.IsGreaterThan(0, item.Handle.SceneItemId);

            // A uuid keeps working across a rename; the old name does not.
            await live
                .Client.Scene(resolved.Handle)
                .SetNameAsync($"{scene}_r", token)
                .ConfigureAwait(false);
            _ = await live
                .Client.Scene(resolved.Handle)
                .GetItemListAsync(token)
                .ConfigureAwait(false);

            _ = await Assert
                .ThrowsExactlyAsync<ObsWebSocketRequestException>(() =>
                    live.Client.Scene(scene).GetItemListAsync(token)
                )
                .ConfigureAwait(false);

            await live
                .Client.Scenes.RemoveSceneAsync(
                    new(sceneName: $"{scene}_r"),
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        }
        catch
        {
            await live
                .Client.Scenes.RemoveSceneAsync(new(sceneName: scene), CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    [TestMethod]
    [Timeout(TimeoutMs, CooperativeCancellation = true)]
    public async Task ItemAsync_LiveObsMissingItem_ThrowsListingAvailable()
    {
        CancellationToken token = TestContext.CancellationToken;
        await using LiveClient live = await LiveClient
            .ConnectAsync(SerializationFormat.Json, TestContext)
            .ConfigureAwait(false);

        GetSceneListResponseData scenes = await live
            .Client.Scenes.GetSceneListAsync(new(), token)
            .ConfigureAwait(false);

        ObsWebSocketResourceNotFoundException error = await Assert
            .ThrowsExactlyAsync<ObsWebSocketResourceNotFoundException>(() =>
                live
                    .Client.Scene(scenes.CurrentProgramSceneName!)
                    .ItemAsync("__obsws_absent__", cancellationToken: token)
                    .AsTask()
            )
            .ConfigureAwait(false);

        TestContext.WriteLine(error.Message);
        Assert.IsNotEmpty(error.Message);
    }

    [TestMethod]
    [Timeout(TimeoutMs, CooperativeCancellation = true)]
    public async Task InputSourceFilterHandles_LiveObs_Resolve()
    {
        CancellationToken token = TestContext.CancellationToken;
        await using LiveClient live = await LiveClient
            .ConnectAsync(SerializationFormat.Json, TestContext)
            .ConfigureAwait(false);

        GetSceneListResponseData scenes = await live
            .Client.Scenes.GetSceneListAsync(new(), token)
            .ConfigureAwait(false);
        string input = $"__obsws_ih_{Guid.NewGuid():N}"[..24];

        await live
            .Client.Inputs.CreateInputAsync(
                new(
                    inputName: input,
                    inputKind: live.Kinds.Color!,
                    sceneName: scenes.CurrentProgramSceneName!
                ),
                token
            )
            .ConfigureAwait(false);

        try
        {
            InputOperations resolvedInput = await live
                .Client.Input(input)
                .ResolveAsync(token)
                .ConfigureAwait(false);
            Assert.IsTrue(resolvedInput.Handle.IsResolved);

            SourceOperations source = live.Client.Input(input).AsSource();
            SourceOperations resolvedSource = await live
                .Client.Source(input)
                .ResolveAsync(token)
                .ConfigureAwait(false);
            Assert.IsTrue(resolvedSource.Handle.IsResolved);

            if (live.Kinds.ColorFilter is { } filterKind)
            {
                await live
                    .Client.Filters.CreateSourceFilterAsync(
                        new(
                            filterName: "__obsws_handle_filter",
                            filterKind: filterKind,
                            sourceName: input
                        ),
                        token
                    )
                    .ConfigureAwait(false);

                GetSourceFilterResponseData filter = await source
                    .Filter("__obsws_handle_filter")
                    .GetAsync(token)
                    .ConfigureAwait(false);
                Assert.IsNotNull(filter);
            }

            _ = await Assert
                .ThrowsExactlyAsync<ObsWebSocketResourceNotFoundException>(() =>
                    live.Client.Input("__obsws_absent__").ResolveAsync(token).AsTask()
                )
                .ConfigureAwait(false);
        }
        finally
        {
            await live
                .Client.Inputs.RemoveInputAsync(new(inputName: input), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }
}
