using System.Text;
using Microsoft.CodeAnalysis;
using ObsWebSocket.Codegen.Tasks.Generation;

namespace ObsWebSocket.Codegen.Tasks;

internal static class ProtocolCodegenRunner
{
    private const string ProtocolUrl = "https://raw.githubusercontent.com/obsproject/obs-websocket/master/docs/generated/protocol.json";

    public static async Task<int> GenerateAsync(
        string protocolPath,
        string outputDirectory,
        bool downloadIfMissing,
        CancellationToken cancellationToken,
        Action<string>? logInfo = null,
        Action<string>? logError = null
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(protocolPath);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        try
        {
            string fullProtocolPath = Path.GetFullPath(protocolPath);
            string fullOutputDirectory = Path.GetFullPath(outputDirectory);

            if (!File.Exists(fullProtocolPath))
            {
                if (!downloadIfMissing)
                {
                    logError?.Invoke($"Protocol file not found: {fullProtocolPath}");
                    return 2;
                }

                await DownloadProtocolAsync(fullProtocolPath, cancellationToken).ConfigureAwait(false);
                logInfo?.Invoke($"Downloaded protocol.json to '{fullProtocolPath}'.");
            }

            string protocolJson = await File.ReadAllTextAsync(fullProtocolPath, cancellationToken).ConfigureAwait(false);
            (IReadOnlyDictionary<string, string> sources, IReadOnlyList<Diagnostic> diagnostics) = ProtocolCodeGenerator.Generate(protocolJson);

            Diagnostic[] errors = [.. diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)];
            if (errors.Length > 0)
            {
                StringBuilder builder = new();
                builder.AppendLine("Code generation failed:");
                foreach (Diagnostic diagnostic in errors)
                {
                    builder.AppendLine(diagnostic.ToString());
                }

                logError?.Invoke(builder.ToString());
                return 1;
            }

            WriteSources(fullOutputDirectory, sources);
            logInfo?.Invoke($"Generated {sources.Count} source files to '{fullOutputDirectory}'.");

            return 0;
        }
        catch (Exception ex)
        {
            logError?.Invoke(ex.ToString());
            return 1;
        }
    }

    private static async Task DownloadProtocolAsync(string protocolPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(protocolPath)!);
        using HttpClient http = new();
        using HttpResponseMessage response = await http.GetAsync(ProtocolUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string protocolJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(protocolPath, protocolJson, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private static void WriteSources(string outputDirectory, IReadOnlyDictionary<string, string> sources)
    {
        Directory.CreateDirectory(outputDirectory);

        HashSet<string> generatedRelativePaths = sources.Keys
            .Select(NormalizeRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string existingFile in Directory.GetFiles(outputDirectory, "*.g.cs", SearchOption.AllDirectories))
        {
            string relativePath = NormalizeRelativePath(Path.GetRelativePath(outputDirectory, existingFile));
            if (!generatedRelativePaths.Contains(relativePath))
            {
                File.Delete(existingFile);
            }
        }

        foreach ((string relativePath, string source) in sources)
        {
            string normalizedRelativePath = NormalizeRelativePath(relativePath);
            string outputPath = Path.Combine(outputDirectory, normalizedRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, source, new UTF8Encoding(false));
        }
    }

    private static string NormalizeRelativePath(string path)
    {
        return path
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
    }
}
