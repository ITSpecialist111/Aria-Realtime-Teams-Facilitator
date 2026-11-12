using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;

namespace RtBot.Configuration;

/// <summary>
/// Reads secrets from DPAPI-protected files on the local machine.
/// </summary>
/// <remarks>
/// Machine environment variables are readable by <c>BUILTIN\Users</c>, which is not an acceptable
/// home for a client secret that carries tenant-wide calling permissions. Key Vault is the right
/// answer, but this tenant forces <c>publicNetworkAccess: Disabled</c> on vaults, so until a
/// private endpoint exists the secret lives in a machine-bound encrypted file instead.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ProtectedSecretFile
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RtBot.Secret.v1");

    public static string? TryRead(string path, ILogger? logger = null)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            byte[] cipher = File.ReadAllBytes(path);
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.LocalMachine));
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Could not decrypt the secret at {Path}.", path);
            return null;
        }
    }

    public static void Write(string path, string secret)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(
            path,
            ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.LocalMachine));
    }
}
