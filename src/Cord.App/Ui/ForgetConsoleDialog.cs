using Hexa.NET.ImGui;

namespace Cord.App.Ui;

/// <summary>Asks before removing a paired console, since pairing again needs the console and a new PIN.</summary>
internal sealed class ForgetConsoleDialog
{
    private const string Id = "forget-console";

    private readonly AppServices _services;
    private string? _hostId;
    private string _name = "";
    private bool _open;

    public ForgetConsoleDialog(AppServices services) => _services = services;

    public void Request(string hostId, string name)
    {
        _hostId = hostId;
        _name = name;
        _open = true;
    }

    public void Draw()
    {
        // Opened here, at the window's root, because popup ids depend on the id stack.
        if (_open)
        {
            ImGui.OpenPopup(Id);
            _open = false;
        }

        if (!Widgets.BeginDialog(Id, 460))
            return;

        Widgets.Heading($"Forget {_name}?");
        Widgets.Muted("To play on it again you will need to pair it again with a code from the console.");
        Widgets.Space(8);
        if (Widgets.Button($"{Icons.Trash}  Forget", ButtonKind.Danger))
        {
            if (_hostId is not null)
                _services.Consoles.Remove(_hostId);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (Widgets.Button("Cancel", ButtonKind.Ghost))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}
