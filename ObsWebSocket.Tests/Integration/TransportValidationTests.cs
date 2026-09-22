using ObsWebSocket.Core;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Tests.Integration;

/// <summary>
/// Sends every request the protocol defines against a live OBS, on both wire formats.
/// </summary>
/// <remarks>
/// One test per format rather than one per sweep. The sweeps share a single OBS and the write
/// sweep changes global state, so they are ordered rather than independent; every check still
/// reports on its own, and a failure names all of them at once.
/// </remarks>
[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
public sealed class TransportValidationTests
{
    private const int CycleTimeoutMs = 600_000;

    /// <summary>The context MSTest assigns, used for cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(SerializationFormat.Json)]
    [DataRow(SerializationFormat.MsgPack)]
    [Timeout(CycleTimeoutMs, CooperativeCancellation = true)]
    public async Task EveryRequest_LiveObs_RoundTrips(SerializationFormat format)
    {
        CancellationToken token = TestContext.CancellationToken;
        await using LiveClient live = await LiveClient
            .ConnectAsync(format, TestContext)
            .ConfigureAwait(false);

        SerializationFailureSink.Reset();

        GetInputListResponseData inputs = await live
            .Client.Inputs.GetInputListAsync(new(), token)
            .ConfigureAwait(false);

        List<(string Label, bool Pass, string Detail)> results =
        [
            .. await ObsRequestSweeps
                .ValidateSettingsModesAsync(live.Client, inputs, live.Kinds, token)
                .ConfigureAwait(false),
            .. await ObsRequestSweeps
                .ValidateModernApisAsync(
                    live.Client,
                    live.HealthChecks,
                    live.Kinds,
                    live.Outputs,
                    token
                )
                .ConfigureAwait(false),
            .. await ObsRequestSweeps
                .SweepEveryReadRequestAsync(live.Client, live.Kinds, live.Outputs, token)
                .ConfigureAwait(false),
            .. await ObsRequestSweeps
                .SweepEveryWriteRequestAsync(live.Client, live.Kinds, live.Outputs, token)
                .ConfigureAwait(false),
        ];

        string[] unreadable = [.. SerializationFailureSink.Failures.Distinct()];
        results.Add(
            (
                "No unreadable payloads",
                unreadable.Length == 0,
                unreadable.Length == 0
                    ? "every payload the run received deserialized"
                    : string.Join(" | ", unreadable.Take(3))
            )
        );

        foreach ((string label, bool pass, string detail) in results)
        {
            TestContext.WriteLine($"{(pass ? "pass" : "FAIL")}  {label}: {detail}");
        }

        Assert.IsNotEmpty(results, "the sweep reported nothing, which means it did not run");

        string[] failures =
        [
            .. results
                .Where(result => !result.Pass)
                .Select(result => $"{result.Label}: {result.Detail}"),
        ];

        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }
}
