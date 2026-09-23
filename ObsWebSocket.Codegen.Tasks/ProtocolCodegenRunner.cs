using System.Text;
using Microsoft.CodeAnalysis;
using ObsWebSocket.Codegen.Tasks.Generation;

namespace ObsWebSocket.Codegen.Tasks;

internal static class ProtocolCodegenRunner
{
    public static async Task<int> GenerateAsync(
        string protocolPath,
        string outputDirectory,
        string? refreshCommit,
        CancellationToken cancellationToken,
        Action<string>? logInfo = null,
        Action<string>? logWarning = null,
        Action<string>? logError = null
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(protocolPath);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);

        try
        {
            string fullProtocolPath = Path.GetFullPath(protocolPath);
            string fullOutputDirectory = Path.GetFullPath(outputDirectory);
            ProtocolLock pinned = ProtocolLock.Read(fullProtocolPath);

            if (!string.IsNullOrEmpty(refreshCommit))
            {
                pinned = await RefreshProtocolAsync(
                        fullProtocolPath,
                        pinned,
                        refreshCommit,
                        cancellationToken,
                        logInfo
                    )
                    .ConfigureAwait(false);
            }

            if (!File.Exists(fullProtocolPath))
            {
                // Not downloaded: the generated types are this library's public API, so a build
                // must derive them from repository content.
                logError?.Invoke(
                    $"Protocol definition not found: {fullProtocolPath}. It is checked in, so "
                        + "restore it from git rather than regenerating it, or run the target "
                        + "'RefreshObsProtocol' to fetch the pinned revision explicitly."
                );
                return 2;
            }

            byte[] protocolBytes = await ReadAllBytesAsync(fullProtocolPath, cancellationToken)
                .ConfigureAwait(false);
            string actualHash = ProtocolLock.HashOf(protocolBytes);
            if (!string.Equals(actualHash, pinned.Sha256, StringComparison.Ordinal))
            {
                logError?.Invoke(
                    $"'{fullProtocolPath}' does not match the revision pinned in "
                        + $"{ProtocolLock.FileName}.{Environment.NewLine}"
                        + $"  expected {pinned.Sha256} (upstream {pinned.Commit}){Environment.NewLine}"
                        + $"  actual   {actualHash}{Environment.NewLine}"
                        + "Edit the definition by refreshing it to a new upstream commit, so the "
                        + "lock records where the generated API came from."
                );
                return 2;
            }

            string protocolJson = Encoding.UTF8.GetString(protocolBytes);
            (IReadOnlyDictionary<string, string> sources, IReadOnlyList<Diagnostic> diagnostics) =
                ProtocolCodeGenerator.Generate(protocolJson);

            Diagnostic[] errors =
            [
                .. diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error),
            ];
            Diagnostic[] warnings =
            [
                .. diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning),
            ];
            Diagnostic[] infos = [.. diagnostics.Where(d => d.Severity == DiagnosticSeverity.Info)];
            if (errors.Length > 0)
            {
                StringBuilder builder = new();
                _ = builder.AppendLine("Code generation failed:");
                foreach (Diagnostic diagnostic in errors)
                {
                    _ = builder.AppendLine(diagnostic.ToString());
                }

                logError?.Invoke(builder.ToString());
                return 1;
            }

            foreach (Diagnostic warning in warnings)
            {
                logWarning?.Invoke(warning.ToString());
            }

            foreach (Diagnostic info in infos)
            {
                logInfo?.Invoke(info.ToString());
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

    /// <summary>
    /// Fetches an upstream revision and re-pins the lock to it. The only path here that touches
    /// the network, addressed by commit so the lock records exactly what was fetched.
    /// </summary>
    private static async Task<ProtocolLock> RefreshProtocolAsync(
        string protocolPath,
        ProtocolLock pinned,
        string commit,
        CancellationToken cancellationToken,
        Action<string>? logInfo
    )
    {
        ProtocolLock target = pinned with { Commit = commit };
        _ = Directory.CreateDirectory(Path.GetDirectoryName(protocolPath)!);

        using HttpClient http = new();
        using HttpResponseMessage response = await http.GetAsync(target.RawUrl, cancellationToken)
            .ConfigureAwait(false);
        _ = response.EnsureSuccessStatusCode();
        byte[] protocolBytes = await response
            .Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        string hash = ProtocolLock.HashOf(protocolBytes);
        await WriteAllBytesAsync(protocolPath, protocolBytes, cancellationToken)
            .ConfigureAwait(false);
        pinned.Write(protocolPath, commit, hash);

        logInfo?.Invoke(
            hash == pinned.Sha256
                ? $"Protocol definition at {commit} is identical to the pinned revision."
                : $"Refreshed the protocol definition to {commit} ({hash})."
        );

        return target with
        {
            Sha256 = hash,
        };
    }

    private static async Task<byte[]> ReadAllBytesAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        using FileStream stream = File.OpenRead(path);
        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static async Task WriteAllBytesAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken
    )
    {
        using FileStream stream = File.Create(path);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static void WriteSources(
        string outputDirectory,
        IReadOnlyDictionary<string, string> sources
    )
    {
        _ = Directory.CreateDirectory(outputDirectory);

        HashSet<string> generatedRelativePaths = sources
            .Keys.Select(NormalizeRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (
            string existingFile in Directory.GetFiles(
                outputDirectory,
                "*.g.cs",
                SearchOption.AllDirectories
            )
        )
        {
            string relativePath = NormalizeRelativePath(
                Path.GetRelativePath(outputDirectory, existingFile)
            );
            if (!generatedRelativePaths.Contains(relativePath))
            {
                File.Delete(existingFile);
            }
        }

        foreach ((string relativePath, string source) in sources)
        {
            string normalizedRelativePath = NormalizeRelativePath(relativePath);
            string outputPath = Path.Combine(outputDirectory, normalizedRelativePath);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            // One newline per file. StringBuilder.AppendLine writes the OS newline while protocol
            // descriptions carry a bare "\n", which left mixed endings that showed as a change on
            // every regeneration. The OS newline is what git checks out on that OS, and git stores
            // LF either way.
            File.WriteAllText(
                outputPath,
                source
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace("\n", Environment.NewLine, StringComparison.Ordinal),
                new UTF8Encoding(false)
            );
        }
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
}
