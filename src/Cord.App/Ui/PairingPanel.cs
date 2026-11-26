using System.Net;
using System.Net.Sockets;
using Hexa.NET.ImGui;
using Cord.Core.Consoles;
using Cord.Protocol.Discovery;
using Cord.Protocol.Registration;

namespace Cord.App.Ui;

/// <summary>Pairs a console found on the network using the PIN it shows (docs/protocol/04-registration.md).</summary>
internal sealed class PairingPanel
{
    private const int PinLength = 8;

    private readonly AppServices _services;
    private readonly BackgroundJob _job = new();
    private readonly ManualSearch _manualSearch = new();
    private string? _selectedHostId;
    private string _pin = "";
    private string _manualAddress = "";
    private bool _showManualAddress;
    private bool _invalidAddress;
    private volatile string? _pairedName;

    public PairingPanel(AppServices services) => _services = services;

    /// <summary>The name of the console paired by this panel, until <see cref="Reset"/>.</summary>
    public string? PairedName => _pairedName;

    public void Reset()
    {
        _pairedName = null;
        _selectedHostId = null;
        _pin = "";
        _job.ClearError();
    }

    public void Draw()
    {
        if (_pairedName is { } name)
        {
            Widgets.Text($"{Icons.CircleCheck}  {name} is paired", Theme.HeadingSize - 2, Theme.Success, bold: true);
            Widgets.Muted("You can play on it from the home screen.");
            Widgets.Space(4);
            if (Widgets.Button($"{Icons.Plus}  Pair another console", ButtonKind.Ghost))
                Reset();
            return;
        }

        Widgets.Text("On your PS5", bold: true);
        Widgets.Space(2);
        Widgets.NumberedStep(1, "Go to Settings > System > Remote Play and turn on Enable Remote Play.");
        Widgets.NumberedStep(2, "Select Pair Device. An 8-digit code appears on your TV.");
        Widgets.NumberedStep(3, "Pick your console below and type the code.");
        Widgets.Space(10);

        Widgets.Text("Consoles on your network", bold: true);
        var entries = _services.Browser.Snapshot();
        var candidates = entries.Where(e => !e.Registered && e.Address is not null).ToList();
        DrawCandidates(candidates);
        DrawManualAddress(entries);

        var selected = candidates.FirstOrDefault(e => e.HostId == _selectedHostId && e.State == ConsoleState.Ready);
        if (selected is not null)
            DrawPin(selected);
    }

    private void DrawCandidates(List<ConsoleEntry> candidates)
    {
        if (candidates.Count == 0)
        {
            Widgets.Progress("Looking for your PS5...");
            Widgets.Muted("Make sure it's turned on (not in rest mode) and connected to the same network as this computer.", Theme.SmallSize);
            return;
        }

        var ready = candidates.Where(e => e.State == ConsoleState.Ready).ToList();
        if (_selectedHostId is null && ready.Count == 1)
            _selectedHostId = ready[0].HostId;

        foreach (var entry in candidates)
        {
            var isReady = entry.State == ConsoleState.Ready;
            var detail = isReady ? $"Ready to pair  ·  {entry.Address}" : "In rest mode. Turn it on with the PS button on your controller.";
            if (Widgets.ListRow(entry.HostId, Icons.Gamepad, entry.Name, detail, entry.HostId == _selectedHostId, isReady))
                _selectedHostId = entry.HostId;
        }
    }

    private void DrawManualAddress(IReadOnlyList<ConsoleEntry> entries)
    {
        if (Widgets.Button(_showManualAddress ? "Hide" : "Can't find your console?", ButtonKind.Ghost))
            _showManualAddress = !_showManualAddress;
        if (!_showManualAddress)
            return;

        Widgets.Muted("Enter its IP address. On the PS5 you find it in Settings > Network > Connection Status.", Theme.SmallSize);
        ImGui.SetNextItemWidth(Theme.Dp(220));
        var submitted = ImGui.InputTextWithHint("##address", "192.168.0.10", ref _manualAddress, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (Widgets.Button($"{Icons.Search}  Search") || submitted)
        {
            _invalidAddress = !IPAddress.TryParse(_manualAddress.Trim(), out var address) || address.AddressFamily != AddressFamily.InterNetwork;
            if (!_invalidAddress)
            {
                _services.Browser.AddManualAddress(address!);
                _manualSearch.Begin(address!);
            }
        }

        if (_invalidAddress)
            Widgets.Text($"{Icons.CircleAlert}  Enter an IPv4 address such as 192.168.0.10.", Theme.SmallSize, Theme.Warning);
        else
            DrawManualSearchStatus(entries);
    }

    private void DrawManualSearchStatus(IReadOnlyList<ConsoleEntry> entries)
    {
        var (state, console) = _manualSearch.Update(entries);
        var address = _manualSearch.Address;
        switch (state)
        {
            case ManualSearchState.Searching:
                Widgets.Progress($"Looking for a console at {address}...");
                break;
            case ManualSearchState.Found:
                Widgets.Text($"{Icons.CircleCheck}  Found {console!.Name} at {address}. It is in the list above.", Theme.SmallSize, Theme.Success);
                break;
            case ManualSearchState.AlreadyPaired:
                Widgets.Text($"{Icons.CircleCheck}  {console!.Name} at {address} is already paired. Play on it from the home screen.", Theme.SmallSize, Theme.Success);
                break;
            case ManualSearchState.NoAnswer:
                Widgets.Text($"{Icons.CircleAlert}  No console answered at {address}.", Theme.SmallSize, Theme.Warning);
                Widgets.Muted(
                    "Check the address and that the console is on. If a friend shares it, their computer must be sharing it and both computers "
                    + "must be on the same VPN. Sharing does not work from the computer that shares the console.",
                    Theme.SmallSize);
                break;
        }
    }

    private void DrawPin(ConsoleEntry console)
    {
        Widgets.Space(10);
        Widgets.Text($"Code shown on your TV for {console.Name}", bold: true);
        ImGui.PushFont(Theme.SemiBold, 26);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Theme.Dp(16, 10));
        ImGui.SetNextItemWidth(Theme.Dp(230));
        var submitted = ImGui.InputTextWithHint("##pin", "00000000", ref _pin, PinLength + 1, ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.PopStyleVar();
        ImGui.PopFont();

        var accountId = _services.Account.AccountId;
        var canPair = accountId is not null && _pin.Length == PinLength && !_job.Busy;
        ImGui.SameLine();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Theme.Dp(4));
        if ((Widgets.Button("Pair", ButtonKind.Primary, Theme.Dp(120), canPair) || (submitted && canPair)) && uint.TryParse(_pin, out var pin))
            Pair(console, accountId!.Value, pin);

        if (accountId is null)
            Widgets.Text($"{Icons.CircleAlert}  Sign in to PlayStation Network first.", Theme.SmallSize, Theme.Warning);
        if (_job.Busy)
            Widgets.Progress("Pairing...");
        if (_job.Error is { } error)
        {
            Widgets.Text($"{Icons.CircleAlert}  Pairing failed. Check the code on your TV and try again.", Theme.SmallSize, Theme.Danger);
            Widgets.Text(error, Theme.SmallSize, Theme.TextFaint);
        }
    }

    private void Pair(ConsoleEntry console, Cord.Protocol.PsnAccountId accountId, uint pin)
    {
        var tables = _services.Tables;
        var address = console.Address!;
        _job.Run(async () =>
        {
            var registered = await new RegistrationClient(tables).RegisterAsync(address, accountId, pin);
            _services.Consoles.Save(StoredConsole.From(console.HostId, address.ToString(), registered, console.Name));
            _pin = "";
            _selectedHostId = null;
            _pairedName = registered.Nickname ?? console.Name;
        });
    }
}
