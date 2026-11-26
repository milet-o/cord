using Cord.Core.Consoles;
using Cord.Protocol.Discovery;

namespace Cord.App.Ui;

internal enum ConsoleAction
{
    Play,
    WakeAndPlay,
    Waking,
    Unavailable,
}

internal enum StatusTone
{
    Ready,
    Resting,
    Working,
    Offline,
}

/// <summary>How a paired console is described on its card and what its main button does.</summary>
internal sealed record ConsoleStatus(string Label, string? Detail, StatusTone Tone, ConsoleAction Action)
{
    public static ConsoleStatus For(ConsoleEntry entry, bool waking)
    {
        if (waking)
            return new ConsoleStatus("Turning on...", "This can take up to a minute.", StatusTone.Working, ConsoleAction.Waking);
        if (entry.Address is null)
            return Offline;
        return entry.State switch
        {
            ConsoleState.Ready => new ConsoleStatus("Ready to play", entry.RunningApp, StatusTone.Ready, ConsoleAction.Play),
            ConsoleState.Standby => new ConsoleStatus("In rest mode", "Cord will turn it on for you.", StatusTone.Resting, ConsoleAction.WakeAndPlay),
            _ => Offline,
        };
    }

    private static ConsoleStatus Offline { get; } =
        new("Not found", "Make sure it is plugged in and on the same network.", StatusTone.Offline, ConsoleAction.Unavailable);
}
