using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ObsWebSocket.Codegen.Tasks;

/// <summary>
/// The pinned upstream revision that <c>protocol.json</c> was taken from.
/// </summary>
/// <param name="Repository">The upstream repository the definition comes from.</param>
/// <param name="Path">The path to the definition within that repository.</param>
/// <param name="Commit">The upstream commit the definition was read at.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the definition's bytes.</param>
internal sealed record ProtocolLock(string Repository, string Path, string Commit, string Sha256)
{
    /// <summary>The lock file's name, alongside <c>protocol.json</c>.</summary>
    public const string FileName = "protocol.lock.json";

    /// <summary>
    /// Builds the immutable raw URL for the pinned revision.
    /// </summary>
    /// <remarks>
    /// Addressed by commit rather than by branch, so a refresh fetches a revision that cannot
    /// change afterwards.
    /// </remarks>
    public string RawUrl =>
        $"https://raw.githubusercontent.com/{RepositoryPath}/{Commit}/{Path.TrimStart('/')}";

    private string RepositoryPath => new Uri(Repository).AbsolutePath.Trim('/');

    /// <summary>Reads the lock sitting next to the given protocol file.</summary>
    /// <param name="protocolPath">Full path to <c>protocol.json</c>.</param>
    /// <returns>The parsed lock.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the lock is missing or incomplete.</exception>
    public static ProtocolLock Read(string protocolPath)
    {
        string lockPath = LockPathFor(protocolPath);
        if (!File.Exists(lockPath))
        {
            throw new InvalidOperationException(
                $"The protocol lock '{lockPath}' is missing. It records which upstream revision "
                    + "protocol.json was taken from, and generation will not run without it."
            );
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(lockPath));
        JsonElement root = document.RootElement;

        return new ProtocolLock(
            Required(root, "repository", lockPath),
            Required(root, "path", lockPath),
            Required(root, "commit", lockPath),
            Required(root, "sha256", lockPath).ToLowerInvariant()
        );
    }

    /// <summary>Rewrites the lock for a newly fetched revision, preserving the comment block.</summary>
    /// <param name="protocolPath">Full path to <c>protocol.json</c>.</param>
    /// <param name="commit">The upstream commit that was fetched.</param>
    /// <param name="sha256">The fetched definition's hash.</param>
    public void Write(string protocolPath, string commit, string sha256)
    {
        string lockPath = LockPathFor(protocolPath);
        using JsonDocument existing = JsonDocument.Parse(File.ReadAllBytes(lockPath));

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in existing.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "commit":
                        writer.WriteString("commit", commit);
                        break;
                    case "sha256":
                        writer.WriteString("sha256", sha256);
                        break;
                    default:
                        property.WriteTo(writer);
                        break;
                }
            }

            writer.WriteEndObject();
        }

        File.WriteAllText(
            lockPath,
            Encoding.UTF8.GetString(buffer.ToArray()) + Environment.NewLine,
            new UTF8Encoding(false)
        );
    }

    /// <summary>Hashes a protocol definition the same way the lock records it.</summary>
    /// <param name="bytes">The definition's raw bytes.</param>
    /// <returns>Lowercase hex SHA-256.</returns>
    public static string HashOf(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string LockPathFor(string protocolPath) =>
        System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(protocolPath))!,
            FileName
        );

    private static string Required(JsonElement root, string name, string lockPath) =>
        root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidOperationException(
                $"The protocol lock '{lockPath}' has no '{name}'."
            );
}
