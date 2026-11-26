using Hexa.NET.ImGui;

namespace Cord.App.Ui;

/// <summary>Pairs another console from the launcher, signing in first when needed.</summary>
internal sealed class PairScreen
{
    private readonly AppServices _services;
    private readonly SignInPanel _signIn;
    private readonly PairingPanel _pairing;

    public PairScreen(AppServices services, SignInPanel signIn, PairingPanel pairing)
    {
        _services = services;
        _signIn = signIn;
        _pairing = pairing;
    }

    public event Action? BackRequested;

    public void Draw()
    {
        Widgets.BeginColumn("pair", 780);
        Widgets.Space(20);
        if (Widgets.IconButton(Icons.ArrowLeft, "back", "Back to your consoles", 44))
            BackRequested?.Invoke();
        ImGui.SameLine(0, Theme.Dp(12));
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Theme.Dp(2));
        Widgets.Title("Pair a console");
        Widgets.Space(8);

        if (_services.Account.AccountId is null)
        {
            Widgets.BeginCard("sign-in");
            Widgets.Heading("First, sign in to PlayStation Network");
            Widgets.Space(2);
            _signIn.Draw();
            Widgets.EndCard();
            Widgets.Space(14);
        }

        Widgets.BeginCard("pairing");
        _pairing.Draw();
        if (_pairing.PairedName is not null && Widgets.Button("Go to your consoles", ButtonKind.Primary))
            BackRequested?.Invoke();
        Widgets.EndCard();
        Widgets.Space(40);
        Widgets.EndColumn();
    }
}
