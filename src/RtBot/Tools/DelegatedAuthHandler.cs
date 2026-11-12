using System.Net.Http.Headers;

namespace RtBot.Tools;

/// <summary>
/// Attaches a current access token to every MCP request.
/// </summary>
/// <remarks>
/// The MCP transport takes static headers, so a token captured at connection time would pin the
/// session to a value that expires in about an hour and 401s for the rest of the process's life.
/// </remarks>
public sealed class DelegatedAuthHandler : DelegatingHandler
{
    private readonly IDelegatedTokenProvider _tokenProvider;
    private readonly string _scope;

    public DelegatedAuthHandler(IDelegatedTokenProvider tokenProvider, string scope)
    {
        _tokenProvider = tokenProvider;
        _scope = scope;
        InnerHandler = new HttpClientHandler();
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string? token = await _tokenProvider.GetTokenAsync(_scope, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
