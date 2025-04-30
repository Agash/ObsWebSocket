namespace ObsWebSocket.Tests;

/// <summary>
/// Configuration options specifically for OBS integration tests.
/// Populated from the "ObsIntegration" section of testsettings.local.json.
/// </summary>
internal sealed class ObsIntegrationTestOptions
{
    /// <summary>
    /// Gets or sets the WebSocket Server URI for the OBS instance used in tests.
    /// Example: "ws://localhost:4455"
    /// </summary>
    public string? ServerUri { get; set; }

    /// <summary>
    /// Gets or sets the WebSocket Server password, if authentication is enabled in OBS.
    /// Leave null or empty if authentication is disabled.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Gets or sets the name of the scene required to exist in OBS for certain integration tests.
    /// Example: "IntegrationTestScene"
    /// </summary>
    public string? TestSceneName { get; set; }

    /// <summary>
    /// Gets or sets the name of a specific input source (e.g., a Text GDI+ source)
    /// expected to exist within the <see cref="TestSceneName"/> for certain tests.
    /// Example: "IntegrationTestInput"
    /// </summary>
    public string? TestInputName { get; set; }

    /// <summary>
    /// Gets or sets the name of a specific filter (e.g., a Color Correction filter)
    /// expected to exist on the <see cref="TestInputName"/> source for certain tests.
    /// Example: "IntegrationTestFilter"
    /// </summary>
    public string? TestFilterName { get; set; }

    /// <summary>
    /// Gets or sets the name of an audio input source (e.g., Mic/Aux) for testing audio features.
    /// Example: "Mic/Aux"
    /// </summary>
    public string? TestAudioInputName { get; set; }
}
