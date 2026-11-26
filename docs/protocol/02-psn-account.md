# 2. PSN account id

Registration needs the user's PSN **account id** (8 bytes). It is obtained with the same
OAuth 2.0 authorization-code flow the official Remote Play app uses.

## OAuth client

The flow uses the official Remote Play app's OAuth client (a public client id and secret),
built into Cord as `PsnOAuthOptions.RemotePlayApp`. The `CORD_PSN_CLIENT_ID` and
`CORD_PSN_CLIENT_SECRET` environment variables, or `psnClientId` / `psnClientSecret` in the
settings file, override it (`PsnOAuthOptions.Resolve`).

Requests to the token endpoint authenticate with HTTP Basic auth using
`client_id:client_secret`.

| Item | Value |
|---|---|
| Authorize endpoint | `https://auth.api.sonyentertainmentnetwork.com/2.0/oauth/authorize` |
| Token endpoint | `https://auth.api.sonyentertainmentnetwork.com/2.0/oauth/token` |
| Redirect URI | `https://remoteplay.dl.playstation.net/remoteplay/redirect` |
| Scope | `psn:clientapp referenceDataService:countryConfig.read pushNotification:webSocket.desktop.connect sessionManager:remotePlaySession.system.update` |

## Steps

1. Open the authorize URL in a browser (system browser or embedded web view) with:
   `service_entity=urn:service-entity:psn`, `response_type=code`, `client_id`,
   `redirect_uri`, `scope`, `request_locale=en_US`, `ui=pr`, `service_logo=ps`,
   `layout_type=popup`, `smcid=remoteplay`, `prompt=always`, `PlatformPrivacyWs1=minimal`.
2. After sign-in, the browser is redirected to the redirect URI with `?code=<code>`. The
   redirect page itself is irrelevant; the client only needs the `code` query parameter.
   With a system browser the user copies the final URL: Cord watches the clipboard while
   sign-in is pending and completes it as soon as an address with the redirect URI and a
   `code` appears (pasting it into the app also works). Capturing the redirect directly
   would need an embedded web view.
3. Exchange the code: `POST` to the token endpoint, `Content-Type:
   application/x-www-form-urlencoded`, body
   `grant_type=authorization_code&code=<code>&scope=<scope>&redirect_uri=<redirect uri>`.
   The JSON response contains `access_token`, `refresh_token` and `expires_in` (seconds).
4. Fetch the account: `GET <token endpoint>/<access_token>` with the same Basic auth. The
   JSON response contains `user_id` as a decimal string.
5. Convert `user_id` to a signed 64-bit integer and serialize it as **8 bytes
   little-endian**. These 8 bytes are the account id. Its base64 form (12 characters) is
   what registration sends and what users usually see.

The refresh token can be exchanged later with `grant_type=refresh_token&refresh_token=...`
(same scope and redirect URI) if a new access token is ever needed. For LAN play only the
account id is required, so Cord stores the account id and treats tokens as optional.

## Manual fallback

Cord also accepts the base64 account id typed in directly, for users who obtained it
elsewhere.
