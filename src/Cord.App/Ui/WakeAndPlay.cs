using Cord.Core.Consoles;
using Cord.Protocol.Discovery;

namespace Cord.App.Ui;

internal enum WakeStep
{
    Wait,
    SendWakeup,
    Launch,
    TimedOut,
}

/// <summary>
/// Lets "Play" work on a console in rest mode: wakes it, waits until discovery reports it
/// ready, then launches. Wake-up packets are resent because a single UDP packet can be lost.
/// </summary>
internal sealed class WakeAndPlay
{
    public static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time;
    private DateTimeOffset _started;
    private DateTimeOffset _lastWakeup;

    public WakeAndPlay(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>The console being woken, or null when idle.</summary>
    public string? HostId { get; private set; }

    public void Begin(string hostId)
    {
        HostId = hostId;
        _started = _time.GetUtcNow();
        _lastWakeup = DateTimeOffset.MinValue;
    }

    public void Cancel() => HostId = null;

    /// <summary>Decides the next action from the latest discovery entry of the console.</summary>
    public WakeStep Update(ConsoleEntry? entry)
    {
        if (HostId is null)
            return WakeStep.Wait;
        if (entry is { State: ConsoleState.Ready, Address: not null })
        {
            HostId = null;
            return WakeStep.Launch;
        }

        var now = _time.GetUtcNow();
        if (now - _started >= Timeout)
        {
            HostId = null;
            return WakeStep.TimedOut;
        }

        if (entry?.Address is null || now - _lastWakeup < ResendInterval)
            return WakeStep.Wait;
        _lastWakeup = now;
        return WakeStep.SendWakeup;
    }
}
