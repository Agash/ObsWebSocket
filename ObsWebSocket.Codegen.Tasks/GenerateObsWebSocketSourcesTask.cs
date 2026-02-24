using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace ObsWebSocket.Codegen.Tasks;

public sealed class GenerateObsWebSocketSourcesTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public string ProtocolPath { get; set; } = string.Empty;

    [Required]
    public string OutputDirectory { get; set; } = string.Empty;

    public bool DownloadIfMissing { get; set; }

    public override bool Execute()
    {
        int exitCode = ProtocolCodegenRunner.GenerateAsync(
                protocolPath: ProtocolPath,
                outputDirectory: OutputDirectory,
                downloadIfMissing: DownloadIfMissing,
                cancellationToken: CancellationToken.None,
                logInfo: message => Log.LogMessage(MessageImportance.High, message),
                logError: message => Log.LogError(message)
            )
            .GetAwaiter()
            .GetResult();

        return exitCode == 0 && !Log.HasLoggedErrors;
    }
}
