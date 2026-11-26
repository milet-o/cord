using System.Net;
using Cord.Core.Consoles;
using Cord.Protocol.Discovery;

namespace Cord.App.Ui;

internal enum ManualSearchState
{
    Idle,
    Searching,
    Found,
    AlreadyPaired,
    NoAnswer,
}

/// <summary>
/// Tracks a search for a console at an address the user typed, so the pairing page can say
/// whether something answered instead of silently waiting.
/// </summary>
internal sealed class ManualSearch
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    private readonly TimeProvider _time;
    private DateTimeOffset _started;

    public ManualSearch(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public IPAddress? Address { get; private set; }

    public void Begin(IPAddress address)
    {
        Address = address;
        _started = _time.GetUtcNow();
    }

    /// <summary>The state, and the console that answered at the address if any.</summary>
    public (ManualSearchState State, ConsoleEntry? Console) Update(IReadOnlyList<ConsoleEntry> entries)
    {
        if (Address is null)
            return (ManualSearchState.Idle, null);
        var answered = entries.FirstOrDefault(e => e.State != ConsoleState.Unknown && Address.Equals(e.Address));
        if (answered is not null)
            return (answered.Registered ? ManualSearchState.AlreadyPaired : ManualSearchState.Found, answered);
        return (_time.GetUtcNow() - _started < Timeout ? ManualSearchState.Searching : ManualSearchState.NoAnswer, null);
    }
}
