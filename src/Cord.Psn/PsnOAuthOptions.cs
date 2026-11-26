namespace Cord.Psn;

/// <summary>
/// OAuth client used for PSN sign-in (docs/protocol/02-psn-account.md). Defaults to the
/// official Remote Play app's public client; environment variables or settings override it.
/// </summary>
public sealed record PsnOAuthOptions(string ClientId, string ClientSecret)
{
    public const string AuthorizeEndpoint = "https://auth.api.sonyentertainmentnetwork.com/2.0/oauth/authorize";
    public const string TokenEndpoint = "https://auth.api.sonyentertainmentnetwork.com/2.0/oauth/token";
    public const string RedirectUri = "https://remoteplay.dl.playstation.net/remoteplay/redirect";
    public const string Scope =
        "psn:clientapp referenceDataService:countryConfig.read pushNotification:webSocket.desktop.connect sessionManager:remotePlaySession.system.update";

    public const string ClientIdVariable = "CORD_PSN_CLIENT_ID";
    public const string ClientSecretVariable = "CORD_PSN_CLIENT_SECRET";

    /// <summary>Public OAuth client of the official Remote Play app.</summary>
    public static readonly PsnOAuthOptions RemotePlayApp = new("ba495a24-818c-472b-b12d-ff231c1b5745", "mvaiZkRsAsI1IBkY");

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>Environment variables first, then the given (settings) values, then the Remote Play app client.</summary>
    public static PsnOAuthOptions Resolve(string? clientId = null, string? clientSecret = null)
    {
        var fromEnvironment = new PsnOAuthOptions(
            Environment.GetEnvironmentVariable(ClientIdVariable) ?? "",
            Environment.GetEnvironmentVariable(ClientSecretVariable) ?? "");
        if (fromEnvironment.IsConfigured)
            return fromEnvironment;
        var configured = new PsnOAuthOptions(clientId ?? "", clientSecret ?? "");
        return configured.IsConfigured ? configured : RemotePlayApp;
    }
}
