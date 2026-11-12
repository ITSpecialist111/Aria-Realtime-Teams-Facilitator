using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;

namespace RtBot.Tools;

/// <summary>Persists the delegated refresh token between bot restarts.</summary>
public interface ITokenStore
{
    Task<string?> ReadRefreshTokenAsync(CancellationToken cancellationToken);

    Task WriteRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken);
}

/// <summary>
/// Key Vault backed store, read with the host's managed identity.
/// </summary>
/// <remarks>
/// Preferred where the vault is reachable. In a tenant that forces
/// <c>publicNetworkAccess: Disabled</c> that means a private endpoint into the bot's subnet.
/// </remarks>
public sealed class KeyVaultTokenStore : ITokenStore
{
    private const string SecretName = "workiq-delegated-refresh-token";

    private readonly SecretClient _client;
    private readonly ILogger<KeyVaultTokenStore> _logger;

    public KeyVaultTokenStore(Uri vaultUri, TokenCredential credential, ILogger<KeyVaultTokenStore> logger)
    {
        _client = new SecretClient(vaultUri, credential);
        _logger = logger;
    }

    public async Task<string?> ReadRefreshTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            Response<KeyVaultSecret> secret = await _client
                .GetSecretAsync(SecretName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return secret.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the delegated refresh token from Key Vault.");
            return null;
        }
    }

    public async Task WriteRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            await _client.SetSecretAsync(SecretName, refreshToken, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist the rotated refresh token to Key Vault.");
        }
    }
}

/// <summary>
/// Stores the refresh token on the local disk, encrypted with the machine DPAPI key.
/// </summary>
/// <remarks>
/// Used where Key Vault is unreachable — for example a tenant that forces
/// <c>publicNetworkAccess: Disabled</c> and has no private endpoint into the bot's subnet.
/// The ciphertext is bound to this machine, so a copied file is useless, but the file should
/// still be ACL'd to the service account: anything running as that account can decrypt it.
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ProtectedFileTokenStore : ITokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RtBot.WorkIq.RefreshToken.v1");

    private readonly string _path;
    private readonly ILogger<ProtectedFileTokenStore> _logger;

    public ProtectedFileTokenStore(string path, ILogger<ProtectedFileTokenStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public async Task<string?> ReadRefreshTokenAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            byte[] cipher = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not decrypt the delegated refresh token at {Path}.", _path);
            return null;
        }
    }

    public async Task WriteRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            byte[] cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(refreshToken),
                Entropy,
                DataProtectionScope.LocalMachine);

            await File.WriteAllBytesAsync(_path, cipher, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist the rotated refresh token to {Path}.", _path);
        }
    }
}

/// <summary>Used when no delegated identity is configured; every lookup returns nothing.</summary>
public sealed class NullTokenStore : ITokenStore
{
    public Task<string?> ReadRefreshTokenAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task WriteRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
