using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Web;

namespace Cord.Psn;

public sealed record PsnTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>PSN sign-in used to obtain the account id (docs/protocol/02-psn-account.md).</summary>
public sealed class PsnAuthClient
{
    private readonly HttpClient _http;
    private readonly PsnOAuthOptions _options;
    private readonly TimeProvider _time;

    public PsnAuthClient(HttpClient http, PsnOAuthOptions options, TimeProvider? time = null)
    {
        _http = http;
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    public Uri LoginUri
    {
        get
        {
            var query = HttpUtility.ParseQueryString(string.Empty);
            query["service_entity"] = "urn:service-entity:psn";
            query["response_type"] = "code";
            query["client_id"] = _options.ClientId;
            query["redirect_uri"] = PsnOAuthOptions.RedirectUri;
            query["scope"] = PsnOAuthOptions.Scope;
            query["request_locale"] = "en_US";
            query["ui"] = "pr";
            query["service_logo"] = "ps";
            query["layout_type"] = "popup";
            query["smcid"] = "remoteplay";
            query["prompt"] = "always";
            query["PlatformPrivacyWs1"] = "minimal";
            return new Uri($"{PsnOAuthOptions.AuthorizeEndpoint}?{query}");
        }
    }

    /// <summary>Extracts the authorization code from the URL the browser was redirected to.</summary>
    public static string? ExtractCode(string redirectedUrl)
    {
        if (!Uri.TryCreate(redirectedUrl.Trim(), UriKind.Absolute, out var uri))
            return null;
        var code = HttpUtility.ParseQueryString(uri.Query)["code"];
        return string.IsNullOrEmpty(code) ? null : code;
    }

    public Task<PsnTokens> ExchangeCodeAsync(string code, CancellationToken cancellationToken = default) =>
        RequestTokensAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["scope"] = PsnOAuthOptions.Scope,
            ["redirect_uri"] = PsnOAuthOptions.RedirectUri,
        }, cancellationToken);

    public Task<PsnTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        RequestTokensAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = PsnOAuthOptions.Scope,
            ["redirect_uri"] = PsnOAuthOptions.RedirectUri,
        }, cancellationToken);

    /// <summary>Returns the numeric PSN user id; Remote Play uses it as the account id.</summary>
    public async Task<long> GetUserIdAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{PsnOAuthOptions.TokenEndpoint}/{Uri.EscapeDataString(accessToken)}");
        request.Headers.Authorization = BasicAuthorization();
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var info = await response.Content.ReadFromJsonAsync<TokenInfo>(cancellationToken)
            ?? throw new PsnException("Empty account response.");
        if (!long.TryParse(info.UserId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
            throw new PsnException("The account response has no valid user id.");
        return userId;
    }

    private async Task<PsnTokens> RequestTokensAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, PsnOAuthOptions.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = BasicAuthorization();
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        if (string.IsNullOrEmpty(token?.AccessToken))
            throw new PsnException("The token response has no access token.");
        return new PsnTokens(token.AccessToken, token.RefreshToken, _time.GetUtcNow().AddSeconds(token.ExpiresIn));
    }

    private AuthenticationHeaderValue BasicAuthorization()
    {
        if (!_options.IsConfigured)
            throw new PsnException("PSN sign-in is not configured (client id and secret are missing).");
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
        return new AuthenticationHeaderValue("Basic", credentials);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new PsnException($"PSN request failed with {(int)response.StatusCode}: {body}");
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record TokenInfo([property: JsonPropertyName("user_id")] string? UserId);
}

public sealed class PsnException : Exception
{
    public PsnException()
    {
    }

    public PsnException(string message)
        : base(message)
    {
    }

    public PsnException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
