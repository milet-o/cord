using Hexa.NET.ImGui;
using Cord.App.Platform;
using Cord.Protocol;
using Cord.Psn;

namespace Cord.App.Ui;

/// <summary>
/// PSN sign-in through the browser (docs/protocol/02-psn-account.md). The sign-in ends on a
/// redirect page the app cannot intercept, so the user copies its address and the panel
/// picks it up from the clipboard.
/// </summary>
internal sealed class SignInPanel : IDisposable
{
    private const int ClipboardPollMilliseconds = 500;

    private readonly AppServices _services;
    private readonly HttpClient _http = new();
    private readonly BackgroundJob _job = new();
    private bool _awaitingRedirect;
    private bool _showOtherOptions;
    private long _lastClipboardCheck;
    private string? _ignoredClipboard;
    private string _redirectUrl = "";
    private string _manualAccountId = "";
    private string? _hint;

    public SignInPanel(AppServices services) => _services = services;

    public void Dispose() => _http.Dispose();

    public void Draw()
    {
        if (_services.Account.AccountId is { } accountId)
            DrawSignedIn(accountId);
        else if (_awaitingRedirect)
            DrawWaiting();
        else
            DrawStart();
    }

    private void DrawSignedIn(PsnAccountId accountId)
    {
        Widgets.Text($"{Icons.CircleCheck}  You're signed in", Theme.HeadingSize - 2, Theme.Success, bold: true);
        Widgets.Muted($"PlayStation Network account {accountId.ToBase64()}", Theme.SmallSize);
        Widgets.Space(4);
        if (Widgets.Button($"{Icons.LogOut}  Use a different account", ButtonKind.Ghost))
            _services.Account.Clear();
    }

    private void DrawStart()
    {
        Widgets.Muted("Cord uses your PlayStation Network account to pair with your PS5. You sign in on PlayStation's own website, so Cord never sees your password.");
        Widgets.Space(8);
        if (Widgets.Button($"Sign in with PlayStation Network  {Icons.ExternalLink}", ButtonKind.Primary))
            OpenSignInPage();
        DrawOtherOptions();
    }

    private void DrawWaiting()
    {
        Widgets.NumberedStep(1, "Sign in on the PlayStation page that opened in your browser.");
        Widgets.NumberedStep(2, "When you're done, the page goes blank or shows an error. That's expected.");
        Widgets.NumberedStep(3, "Copy the page address: press Ctrl+L, then Ctrl+C. Cord picks it up by itself.");
        Widgets.Space(6);

        Widgets.Progress(_job.Busy ? "Signing in..." : "Waiting for the copied address...");
        if (_job.Error is { } error)
            Widgets.Text($"{Icons.CircleAlert}  {error}", Theme.SmallSize, Theme.Danger);
        else if (_hint is not null)
            Widgets.Text(_hint, Theme.SmallSize, Theme.Warning);
        Widgets.Space(4);

        if (Widgets.Button($"{Icons.Clipboard}  Paste address", enabled: !_job.Busy))
            TrySignIn(SdlHost.ClipboardText() ?? "");
        ImGui.SameLine();
        if (Widgets.Button("Open the page again", ButtonKind.Ghost))
            OpenSignInPage();
        ImGui.SameLine();
        if (Widgets.Button("Cancel", ButtonKind.Ghost))
            _awaitingRedirect = false;

        DrawOtherOptions();
        WatchClipboard();
    }

    private void DrawOtherOptions()
    {
        Widgets.Space(4);
        if (Widgets.Button(_showOtherOptions ? "Hide other options" : "Other options", ButtonKind.Ghost))
            _showOtherOptions = !_showOtherOptions;
        if (!_showOtherOptions)
            return;

        Widgets.Text("Paste the sign-in address", Theme.SmallSize, Theme.TextMuted);
        ImGui.SetNextItemWidth(Widgets.Fill);
        if (ImGui.InputTextWithHint("##redirect", "https://remoteplay.dl.playstation.net/remoteplay/redirect?code=...", ref _redirectUrl, 2048, ImGuiInputTextFlags.EnterReturnsTrue))
            TrySignIn(_redirectUrl);

        Widgets.Text("Or enter your account ID (base64)", Theme.SmallSize, Theme.TextMuted);
        ImGui.SetNextItemWidth(Theme.Dp(260));
        ImGui.InputText("##account-id", ref _manualAccountId, 64);
        ImGui.SameLine();
        if (Widgets.Button("Save account ID"))
        {
            try
            {
                _services.Account.Save(PsnAccountId.FromBase64(_manualAccountId.Trim()));
                _awaitingRedirect = false;
                _hint = null;
            }
            catch (FormatException)
            {
                _hint = "The account ID must be 8 bytes in base64 (12 characters).";
            }
        }
    }

    private void OpenSignInPage()
    {
        _job.ClearError();
        _hint = null;
        try
        {
            SdlHost.OpenUrl(new PsnAuthClient(_http, _services.PsnOptions).LoginUri.ToString());
        }
        catch (PsnException e)
        {
            _hint = e.Message;
        }

        _awaitingRedirect = true;
        _ignoredClipboard = SdlHost.ClipboardText();
    }

    private void WatchClipboard()
    {
        var now = Environment.TickCount64;
        if (_job.Busy || now - _lastClipboardCheck < ClipboardPollMilliseconds)
            return;
        _lastClipboardCheck = now;

        var clipboard = SdlHost.ClipboardText();
        if (clipboard is null || clipboard == _ignoredClipboard
            || !clipboard.Contains("remoteplay/redirect", StringComparison.OrdinalIgnoreCase)
            || PsnAuthClient.ExtractCode(clipboard) is null)
        {
            return;
        }

        _ignoredClipboard = clipboard;
        TrySignIn(clipboard);
    }

    private void TrySignIn(string address)
    {
        var code = PsnAuthClient.ExtractCode(address);
        if (code is null)
        {
            _hint = "That isn't the sign-in address yet. Finish signing in, then copy the address of the page you end up on.";
            return;
        }

        _hint = null;
        _job.Run(async () =>
        {
            var client = new PsnAuthClient(_http, _services.PsnOptions);
            var tokens = await client.ExchangeCodeAsync(code);
            var userId = await client.GetUserIdAsync(tokens.AccessToken);
            _services.Account.Save(PsnAccountId.FromUserId(userId), refreshToken: tokens.RefreshToken);
            _redirectUrl = "";
            _awaitingRedirect = false;
        });
    }
}
