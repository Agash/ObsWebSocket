using ObsWebSocket.Core;
using ObsWebSocket.Core.Protocol.Common;
using ObsWebSocket.Core.Protocol.Common.FilterSettings;
using ObsWebSocket.Core.Protocol.Common.InputSettings;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Tests.Integration;

/// <summary>
/// Covers the typed settings helpers on each group: read as a library type, read as a raw
/// element, and write back what was read.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
public sealed class TypedSettingsHelperTests
{
    private const int TimeoutMs = 120_000;

    /// <summary>The context MSTest assigns, used for cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(TimeoutMs, CooperativeCancellation = true)]
    public async Task TransitionSettings_LiveObs_RoundTrip()
    {
        CancellationToken token = TestContext.CancellationToken;
        await using LiveClient live = await LiveClient
            .ConnectAsync(SerializationFormat.Json, TestContext)
            .ConfigureAwait(false);

        GetSceneTransitionListResponseData transitions = await live
            .Client.Transitions.GetSceneTransitionListAsync(token)
            .ConfigureAwait(false);

        // Chosen by kind rather than taken from whichever is active: the built-in Cut and Fade
        // have no properties, and the record below models a swipe. obs-websocket cannot create a
        // transition, so one has to come from the scene collection, as the CI collection's does.
        TransitionStub? swipe = transitions.Transitions.Find(t =>
            t.TransitionKind == "swipe_transition"
        );
        if (swipe is null)
        {
            ObsLiveEndpoint.Unavailable(
                TestContext,
                "This scene collection has no swipe transition, and obs-websocket cannot add one."
            );
        }

        string? original = transitions.CurrentSceneTransitionName;
        await live
            .Client.Transitions.SetCurrentSceneTransitionAsync(new(swipe.TransitionName), token)
            .ConfigureAwait(false);

        // The library models no transition settings type, so this goes through the overload a
        // consumer would use: their own record plus its metadata.
        SwipeSettings before =
            await live
                .Client.Transitions.GetCurrentSceneTransitionSettingsAsync(
                    TransitionSettingsContext.Default.SwipeSettings,
                    token
                )
                .ConfigureAwait(false)
            ?? new SwipeSettings();

        try
        {
            SwipeSettings changed = before with { SwipeIn = !(before.SwipeIn ?? false) };
            await live
                .Client.Transitions.SetCurrentSceneTransitionSettingsAsync(
                    changed,
                    TransitionSettingsContext.Default.SwipeSettings,
                    cancellationToken: token
                )
                .ConfigureAwait(false);

            SwipeSettings? after = await live
                .Client.Transitions.GetCurrentSceneTransitionSettingsAsync(
                    TransitionSettingsContext.Default.SwipeSettings,
                    token
                )
                .ConfigureAwait(false);

            Assert.AreEqual(changed.SwipeIn, after?.SwipeIn, "the written value must read back");
        }
        finally
        {
            await live
                .Client.Transitions.SetCurrentSceneTransitionSettingsAsync(
                    before,
                    TransitionSettingsContext.Default.SwipeSettings,
                    cancellationToken: CancellationToken.None
                )
                .ConfigureAwait(false);
            if (original is not null)
            {
                await live
                    .Client.Transitions.SetCurrentSceneTransitionAsync(
                        new(original),
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            }
        }
    }

    [TestMethod]
    [Timeout(TimeoutMs, CooperativeCancellation = true)]
    public async Task OutputSettings_LiveObs_ReadRawAndTyped()
    {
        CancellationToken token = TestContext.CancellationToken;
        await using LiveClient live = await LiveClient
            .ConnectAsync(SerializationFormat.Json, TestContext)
            .ConfigureAwait(false);

        string outputName = live.Outputs.Outputs[0].OutputName;

        // Read only. Writing settings back to a real output wedges the output subsystem.
        ColorSourceSettings? settings = await live
            .Client.Outputs.GetOutputSettingsAsync<ColorSourceSettings>(outputName, token)
            .ConfigureAwait(false);

        TestContext.WriteLine($"{outputName} settings present: {settings is not null}");
    }

    [TestMethod]
    [Timeout(TimeoutMs, CooperativeCancellation = true)]
    public async Task InputAndFilterSettings_LiveObs_RoundTrip()
    {
        CancellationToken token = TestContext.CancellationToken;
        await using LiveClient live = await LiveClient
            .ConnectAsync(SerializationFormat.Json, TestContext)
            .ConfigureAwait(false);

        string input = $"__obsws_typed_{Guid.NewGuid():N}"[..26];
        GetSceneListResponseData scenes = await live
            .Client.Scenes.GetSceneListAsync(new(), token)
            .ConfigureAwait(false);

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
            ColorSourceSettings? raw = await live
                .Client.Inputs.GetInputSettingsAsync<ColorSourceSettings>(input, token)
                .ConfigureAwait(false);
            Assert.IsNotNull(raw);

            await live
                .Client.Inputs.SetInputSettingsAsync(input, raw, cancellationToken: token)
                .ConfigureAwait(false);

            if (live.Kinds.ColorFilter is { } filterKind)
            {
                await live
                    .Client.Filters.CreateSourceFilterAsync(
                        new(
                            filterName: "__obsws_typed_filter",
                            filterKind: filterKind,
                            sourceName: input
                        ),
                        token
                    )
                    .ConfigureAwait(false);

                ColorCorrectionFilterSettings? filterRaw = await live
                    .Client.Filters.GetSourceFilterSettingsAsync<ColorCorrectionFilterSettings>(
                        input,
                        "__obsws_typed_filter",
                        token
                    )
                    .ConfigureAwait(false);

                await live
                    .Client.Filters.SetSourceFilterSettingsAsync(
                        input,
                        "__obsws_typed_filter",
                        filterRaw ?? new ColorCorrectionFilterSettings(),
                        cancellationToken: token
                    )
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            await live
                .Client.Inputs.RemoveInputAsync(new(inputName: input), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    [TestMethod]
    [Timeout(TimeoutMs, CooperativeCancellation = true)]
    public async Task PersistentData_LiveObs_RoundTrip()
    {
        CancellationToken token = TestContext.CancellationToken;
        await using LiveClient live = await LiveClient
            .ConnectAsync(SerializationFormat.Json, TestContext)
            .ConfigureAwait(false);

        // One fixed slot, overwritten each run: OBS treats a null value as a missing field, so a
        // slot can never be cleared, and a fresh name per run would leave one behind every time.
        const string realm = "OBS_WEBSOCKET_DATA_REALM_GLOBAL";
        const string slot = "obsws_test_persistent_data";
        SwipeSettings written = new($"run-{Guid.NewGuid():N}", SwipeIn: true);

        await live
            .Client.Config.SetPersistentDataAsync(
                realm,
                slot,
                written,
                TransitionSettingsContext.Default.SwipeSettings,
                token
            )
            .ConfigureAwait(false);

        SwipeSettings? read = await live
            .Client.Config.GetPersistentDataAsync(
                realm,
                slot,
                TransitionSettingsContext.Default.SwipeSettings,
                token
            )
            .ConfigureAwait(false);

        Assert.AreEqual(written, read);
    }
}

/// <summary>Settings for the swipe transition, as a consumer would model them.</summary>
internal sealed record SwipeSettings(
    [property: System.Text.Json.Serialization.JsonPropertyName("direction")]
        string? Direction = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("swipe_in")] bool? SwipeIn = null
);

[System.Text.Json.Serialization.JsonSerializable(typeof(SwipeSettings))]
internal sealed partial class TransitionSettingsContext
    : System.Text.Json.Serialization.JsonSerializerContext { }
