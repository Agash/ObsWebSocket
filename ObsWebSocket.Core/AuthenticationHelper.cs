using System.Security.Cryptography;
using System.Text;

namespace ObsWebSocket.Core;

/// <summary>
/// Provides helper methods for OBS WebSocket authentication.
/// </summary>
internal static class AuthenticationHelper
{
    /// <summary>
    /// Generates the authentication response string required for the Identify message.
    /// </summary>
    /// <param name="salt">The salt provided by the server's Hello message.</param>
    /// <param name="challenge">The challenge provided by the server's Hello message.</param>
    /// <param name="password">The client's password.</param>
    /// <returns>The Base64 encoded authentication string.</returns>
    /// <exception cref="ArgumentNullException">Thrown if any argument is null.</exception>
    public static string GenerateAuthenticationString(
        string salt,
        string challenge,
        string password
    )
    {
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(password); // Password can be empty, but not null

        // 1. Hash the password with the salt: SHA256(password + salt)
        string secretString = password + salt;
        byte[] secretHashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(secretString));
        string secretBase64 = Convert.ToBase64String(secretHashBytes);

        // 2. Hash the result with the challenge: SHA256(secret_base64 + challenge)
        string authString = secretBase64 + challenge;
        byte[] authHashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(authString));
        string authResponseBase64 = Convert.ToBase64String(authHashBytes);

        return authResponseBase64;
    }
}
