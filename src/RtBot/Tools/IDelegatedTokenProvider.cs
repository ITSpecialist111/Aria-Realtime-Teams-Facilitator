namespace RtBot.Tools;

/// <summary>Supplies an access token for a downstream tool service.</summary>
public interface IDelegatedTokenProvider
{
    /// <summary>Acquires a token for <paramref name="scope"/>, or null when unavailable.</summary>
    Task<string?> GetTokenAsync(string scope, CancellationToken cancellationToken);
}
