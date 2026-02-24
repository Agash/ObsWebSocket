using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ObsWebSocket.Codegen.Tasks.Generation;

internal sealed class GenerationContext
{
    private readonly List<Diagnostic> _diagnostics = [];
    private readonly Dictionary<string, string> _sources = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public IReadOnlyDictionary<string, string> Sources => _sources;

    public void AddSource(string hintName, SourceText sourceText)
    {
        ArgumentException.ThrowIfNullOrEmpty(hintName);
        ArgumentNullException.ThrowIfNull(sourceText);

        string relativePath = ResolveOutputPath(hintName, sourceText.ToString());
        _sources[relativePath] = sourceText.ToString();
    }

    public void ReportDiagnostic(Diagnostic diagnostic) => _diagnostics.Add(diagnostic);

    private static string ResolveOutputPath(string hintName, string source)
    {
        string fileName = Path.GetFileName(hintName);
        string namespaceLine = source
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("namespace ", StringComparison.Ordinal))
            ?? string.Empty;

        if (namespaceLine.Contains("ObsWebSocket.Core.Protocol.Common.NestedTypes", StringComparison.Ordinal))
        {
            return Path.Combine("Protocol", "Common", "NestedTypes", fileName);
        }

        if (namespaceLine.Contains("ObsWebSocket.Core.Protocol.Requests", StringComparison.Ordinal))
        {
            return Path.Combine("Protocol", "Requests", fileName);
        }

        if (namespaceLine.Contains("ObsWebSocket.Core.Protocol.Responses", StringComparison.Ordinal))
        {
            return Path.Combine("Protocol", "Responses", fileName);
        }

        if (namespaceLine.Contains("ObsWebSocket.Core.Protocol.Events", StringComparison.Ordinal))
        {
            return Path.Combine("Protocol", "Events", fileName);
        }

        if (namespaceLine.Contains("ObsWebSocket.Core.Protocol.Generated", StringComparison.Ordinal))
        {
            return Path.Combine("Protocol", "Generated", fileName);
        }

        return namespaceLine.Contains("ObsWebSocket.Core.Events.Generated", StringComparison.Ordinal)
            ? Path.Combine("Events", "Generated", fileName)
            : namespaceLine.Contains("ObsWebSocket.Core.Serialization", StringComparison.Ordinal)
            ? Path.Combine("Serialization", fileName)
            : Path.Combine("Client", fileName);
    }
}
