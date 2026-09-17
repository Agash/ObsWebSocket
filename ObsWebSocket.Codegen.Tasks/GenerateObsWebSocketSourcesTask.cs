using Microsoft.Build.Framework;

namespace ObsWebSocket.Codegen.Tasks;

public sealed class GenerateObsWebSocketSourcesTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public string ProtocolPath { get; set; } = string.Empty;

    [Required]
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>
    /// An upstream commit to refresh the checked-in protocol definition to before generating.
    /// </summary>
    /// <remarks>
    /// Empty for every ordinary build, which then generates purely from repository content. Set
    /// only by an explicit refresh, so the network is never a silent build input.
    /// </remarks>
    public string RefreshCommit { get; set; } = string.Empty;

    public override bool Execute()
    {
        int exitCode = ProtocolCodegenRunner
            .GenerateAsync(
                protocolPath: ProtocolPath,
                outputDirectory: OutputDirectory,
                refreshCommit: RefreshCommit,
                cancellationToken: CancellationToken.None,
                logInfo: message => Log.LogMessage(MessageImportance.High, message),
                logWarning: message => Log.LogWarning(message),
                logError: message => Log.LogError(message)
            )
            .GetAwaiter()
            .GetResult();

        return exitCode == 0 && !Log.HasLoggedErrors;
    }
}
