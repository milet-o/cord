using Hexa.NET.ImGui;
using Cord.Core.Settings;

namespace Cord.App.Ui;

/// <summary>First-run setup: sign in, pair a console, pick a quality.</summary>
internal sealed class SetupScreen
{
    private static readonly string[] StepNames = ["Sign in", "Pair your PS5", "Quality"];

    private readonly AppServices _services;
    private readonly SignInPanel _signIn;
    private readonly PairingPanel _pairing;
    private SetupFlow _flow = new();

    public SetupScreen(AppServices services, SignInPanel signIn, PairingPanel pairing)
    {
        _services = services;
        _signIn = signIn;
        _pairing = pairing;
    }

    public event Action? Finished;

    public void Restart()
    {
        _flow = new SetupFlow();
        _pairing.Reset();
    }

    public void Draw()
    {
        Widgets.BeginColumn("setup", 780);
        Widgets.Space(36);
        switch (_flow.Step)
        {
            case SetupStep.Welcome:
                DrawWelcome();
                break;
            case SetupStep.Done:
                DrawDone();
                break;
            default:
                DrawStep();
                break;
        }

        Widgets.Space(40);
        Widgets.EndColumn();
    }

    private void DrawWelcome()
    {
        Logo.Draw(128, centered: true);
        Widgets.Space(8);
        Widgets.Centered("Welcome to Cord", Theme.TitleSize + 4, bold: true);
        Widgets.Centered("Play your PS5 on this computer, over your home network.", Theme.HeadingSize - 2, Theme.TextMuted);
        Widgets.Space(28);

        Widgets.BeginCard("before");
        Widgets.Text("Before you start, have these ready", bold: true);
        Widgets.Space(4);
        Bullet(Icons.Power, "Your PS5, turned on and connected to your home network.");
        Bullet(Icons.User, "Your PlayStation Network email and password.");
        Bullet(Icons.Gamepad, "A controller plugged into this computer. Optional: the keyboard works too.");
        Widgets.EndCard();
        Widgets.Space(28);

        var width = Theme.Dp(260);
        ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - width) / 2);
        if (Widgets.Button($"Get started  {Icons.ChevronRight}", ButtonKind.Primary, width))
            _flow.Next();
        ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - width) / 2);
        if (Widgets.Button("I'll do this later", ButtonKind.Ghost, width))
            Finish();
    }

    private void DrawStep()
    {
        Widgets.Steps(StepNames, (int)_flow.Step - 1);
        Widgets.Space(12);
        Widgets.BeginCard("step");
        switch (_flow.Step)
        {
            case SetupStep.SignIn:
                Widgets.Title("Sign in to PlayStation Network");
                Widgets.Space(4);
                _signIn.Draw();
                break;
            case SetupStep.Pair:
                Widgets.Title("Pair your PS5");
                Widgets.Muted("You only need to do this once for each console.");
                Widgets.Space(8);
                _pairing.Draw();
                break;
            case SetupStep.Quality:
                Widgets.Title("Choose the picture quality");
                Widgets.Muted("You can change this at any time in Settings.");
                Widgets.Space(8);
                if (QualityPicker.Draw(_services.Settings.Current.Quality) is { } preset)
                    _services.Settings.Save(_services.Settings.Current with { Quality = preset.ToQuality() });
                break;
        }

        Widgets.EndCard();
        Widgets.Space(16);
        DrawFooter();
    }

    private void DrawFooter()
    {
        if (Widgets.Button($"{Icons.ArrowLeft}  Back", ButtonKind.Ghost))
            _flow.Back();

        var canContinue = _flow.CanContinue(_services.Account.AccountId is not null, _services.Consoles.Consoles.Count > 0);
        var label = !canContinue && _flow.CanSkip ? "Skip for now" : $"Continue  {Icons.ChevronRight}";
        var width = MathF.Max(Widgets.ButtonWidth(label), Theme.Dp(180));
        Widgets.SameLineRight(width);
        var kind = canContinue ? ButtonKind.Primary : ButtonKind.Secondary;
        if (Widgets.Button(label, kind, width, canContinue || _flow.CanSkip))
            _flow.Next();
    }

    private void DrawDone()
    {
        var paired = _services.Consoles.Consoles.Count > 0;
        Widgets.Space(40);
        Widgets.Centered(Icons.CircleCheck, Theme.HeroSize, Theme.Success);
        Widgets.Space(8);
        Widgets.Centered("You're all set", Theme.TitleSize + 4, bold: true);
        Widgets.Centered(paired ? "Your PS5 is ready. Pick it on the home screen to start playing." : "Pair your PS5 from the home screen whenever you're ready.", Theme.HeadingSize - 2, Theme.TextMuted);
        Widgets.Space(8);
        Widgets.Centered("While playing, press F1 for the stream menu and F11 for fullscreen.", Theme.SmallSize, Theme.TextFaint);
        Widgets.Space(28);
        var width = Theme.Dp(260);
        ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - width) / 2);
        if (Widgets.Button(paired ? $"{Icons.Play}  Start playing" : "Go to the home screen", ButtonKind.Primary, width))
            Finish();
    }

    private void Finish()
    {
        _services.Settings.Save(_services.Settings.Current with { SetupCompleted = true });
        Finished?.Invoke();
    }

    private static void Bullet(string icon, string text)
    {
        Widgets.Text(icon, color: Theme.Accent);
        ImGui.SameLine(0, Theme.Dp(14));
        Widgets.Text(text, color: Theme.TextMuted);
    }
}
