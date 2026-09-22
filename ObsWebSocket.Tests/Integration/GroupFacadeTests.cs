using ObsWebSocket.Core;
using ObsWebSocket.Core.Protocol.Common.StreamServiceSettings;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Tests.Integration;

/// <summary>
/// Exercises the hand written group helpers against a live OBS. These are the surface most
/// callers use, and nothing else in the suite reaches them.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
public sealed class GroupFacadeTests
{
    private const int TimeoutMs = 120_000;

    /// <summary>The context MSTest assigns, used for cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(TimeoutMs, CooperativeCancellation = true)]
    public async Task SceneHelpers_LiveObs_ReportAndSwitch()
    {
        CancellationToken token = TestContext.CancellationToken;
        await using LiveClient live = await LiveClient
            .ConnectAsync(SerializationFormat.Json, TestContext)
            .ConfigureAwait(false);

        GetSceneListResponseData scenes = await live
            .Client.Scenes.GetSceneListAsync(new(), token)
            .ConfigureAwait(false);
        string current = scenes.CurrentProgramSceneName!;

        Assert.IsTrue(
            await live.Client.Scenes.SceneExistsAsync(current, token).ConfigureAwait(false)
        );
        Assert.IsFalse(
            await live
                .Client.Scenes.SceneExistsAsync("__obsws_absent__", token)
                .ConfigureAwait(false)
        );

        await live
            .Client.Scenes.SwitchProgramSceneAsync(current, cancellationToken: token)
            .ConfigureAwait(false);
        await live
            .Client.Scenes.SwitchProgramSceneAndWaitAsync(current, cancellationToken: token)
            .ConfigureAwait(false);
    }

    [TestMethod]
    [Timeout(TimeoutMs, CooperativeCancellation = true)]
    public async Task ConfigHelpers_LiveObs_ReadAndEnsure()
    {
        CancellationToken token = TestContext.CancellationToken;
        await using LiveClient live = await LiveClient
            .ConnectAsync(SerializationFormat.Json, TestContext)
            .ConfigureAwait(false);

        GetProfileListResponseData profiles = await live
            .Client.Config.GetProfileListAsync(token)
            .ConfigureAwait(false);
        GetSceneCollectionListResponseData collections = await live
            .Client.Config.GetSceneCollectionListAsync(token)
            .ConfigureAwait(false);

        Assert.IsTrue(
            await live
                .Client.Config.EnsureProfileActiveAsync(profiles.CurrentProfileName!, token)
                .ConfigureAwait(false)
        );
        Assert.IsFalse(
            await live
                .Client.Config.EnsureProfileActiveAsync("__obsws_absent__", token)
                .ConfigureAwait(false)
        );
        Assert.IsTrue(
            await live
                .Client.Config.EnsureSceneCollectionActiveAsync(
                    collections.CurrentSceneCollectionName!,
                    token
                )
                .ConfigureAwait(false)
        );

        // Typed read of the stream service settings, written back unchanged.
        RtmpCommonStreamServiceSettings? settings = await live
            .Client.Config.GetStreamServiceSettingsAsync<RtmpCommonStreamServiceSettings>(token)
            .ConfigureAwait(false);
        TestContext.WriteLine($"stream service settings: {settings is not null}");
    }

    [TestMethod]
    [Timeout(TimeoutMs, CooperativeCancellation = true)]
    public async Task OutputHelpers_LiveObs_Report()
    {
        CancellationToken token = TestContext.CancellationToken;
        await using LiveClient live = await LiveClient
            .ConnectAsync(SerializationFormat.Json, TestContext)
            .ConfigureAwait(false);

        Assert.IsFalse(await live.Client.Record.IsRecordActiveAsync(token).ConfigureAwait(false));
        Assert.IsFalse(await live.Client.Stream.IsStreamActiveAsync(token).ConfigureAwait(false));
        Assert.IsFalse(
            await live.Client.Outputs.IsVirtualCamActiveAsync(token).ConfigureAwait(false)
        );

        string outputName = live.Outputs.Outputs[0].OutputName;
        TestContext.WriteLine($"first output: {outputName}");
    }
}
