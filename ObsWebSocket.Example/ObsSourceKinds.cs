using ObsWebSocket.Core;
using ObsWebSocket.Core.Protocol.Requests;
using ObsWebSocket.Core.Protocol.Responses;

namespace ObsWebSocket.Example;

/// <summary>
/// The input and filter kinds the connected OBS offers.
/// </summary>
/// <remarks>
/// Kinds are platform and plugin dependent: audio capture is <c>wasapi_output_capture</c> on
/// Windows, <c>pulse_output_capture</c> on Linux and <c>coreaudio_output_capture</c> on macOS, and
/// a build without CEF has no browser source at all. Validation asks OBS what it has rather than
/// assuming, so a missing kind skips the checks that need it instead of failing the run.
/// </remarks>
internal sealed record ObsSourceKinds(
    string? AudioCapture,
    string? Media,
    string? Browser,
    string? Color,
    string? GainFilter,
    string? ColorFilter
)
{
    /// <summary>Asks OBS which kinds it supports.</summary>
    /// <param name="client">A connected client.</param>
    /// <param name="cancellationToken">A token to cancel the lookup.</param>
    public static async Task<ObsSourceKinds> ResolveAsync(
        ObsWebSocketClient client,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(client);

        GetInputKindListResponseData inputs = await client
            .Inputs.GetInputKindListAsync(new GetInputKindListRequestData(), cancellationToken)
            .ConfigureAwait(false);
        GetSourceFilterKindListResponseData filters = await client
            .Filters.GetSourceFilterKindListAsync(cancellationToken)
            .ConfigureAwait(false);

        HashSet<string> inputKinds = new(inputs.InputKinds ?? [], StringComparer.Ordinal);
        HashSet<string> filterKinds = new(filters.SourceFilterKinds ?? [], StringComparer.Ordinal);

        return new ObsSourceKinds(
            AudioCapture: First(
                inputKinds,
                "wasapi_output_capture",
                "pulse_output_capture",
                "coreaudio_output_capture"
            ),
            Media: First(inputKinds, "ffmpeg_source"),
            Browser: First(inputKinds, "browser_source"),
            Color: First(inputKinds, "color_source_v3", "color_source_v2", "color_source"),
            GainFilter: First(filterKinds, "gain_filter"),
            ColorFilter: First(filterKinds, "color_filter_v2", "color_filter")
        );
    }

    /// <summary>Names the kinds that are absent, for reporting a skipped check.</summary>
    public string Describe() =>
        string.Join(
            ", ",
            new (string Name, string? Kind)[]
            {
                ("audio capture", AudioCapture),
                ("media", Media),
                ("browser", Browser),
                ("color", Color),
                ("gain filter", GainFilter),
                ("color filter", ColorFilter),
            }
                .Where(entry => entry.Kind is null)
                .Select(entry => entry.Name)
        );

    private static string? First(HashSet<string> available, params string[] preferred) =>
        Array.Find(preferred, available.Contains);
}
