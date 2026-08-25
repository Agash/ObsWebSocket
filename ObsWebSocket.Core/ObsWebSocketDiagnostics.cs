using System.Diagnostics;

namespace ObsWebSocket.Core;

/// <summary>
/// Names and the activity source the client reports under. Both are inert until something
/// subscribes.
/// </summary>
public static class ObsWebSocketDiagnostics
{
    /// <summary>Name of the <see cref="System.Diagnostics.ActivitySource"/> for requests.</summary>
    public const string ActivitySourceName = "ObsWebSocket.Core";

    /// <summary>Name of the meter the client's instruments are created on.</summary>
    public const string MeterName = "ObsWebSocket.Core";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}
