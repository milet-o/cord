using System.Runtime.InteropServices;

namespace Cord.App.Platform;

/// <summary>Requests 1 ms Windows timer resolution while streaming to minimize wait/sleep jitter.</summary>
internal sealed partial class HighResolutionTimerScope : IDisposable
{
    private const uint PeriodMs = 1;
    private bool _active;

    public HighResolutionTimerScope()
    {
        if (OperatingSystem.IsWindows())
            _active = TimeBeginPeriod(PeriodMs) == 0;
    }

    public void Dispose()
    {
        if (!_active)
            return;
        _active = false;
        if (OperatingSystem.IsWindows())
            _ = TimeEndPeriod(PeriodMs);
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint uPeriod);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint uPeriod);
}

/// <summary>Registers the calling thread with Windows MMCSS ("Games") for deterministic scheduling.</summary>
internal sealed partial class MmcssThreadScope : IDisposable
{
    private nint _handle;

    public MmcssThreadScope(string taskName = "Games")
    {
        if (!OperatingSystem.IsWindows())
            return;
        uint taskIndex = 0;
        _handle = AvSetMmThreadCharacteristicsW(taskName, ref taskIndex);
    }

    public void Dispose()
    {
        if (_handle == 0)
            return;
        var handle = _handle;
        _handle = 0;
        if (OperatingSystem.IsWindows())
            _ = AvRevertMmThreadCharacteristics(handle);
    }

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [LibraryImport("avrt.dll", EntryPoint = "AvRevertMmThreadCharacteristics")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvRevertMmThreadCharacteristics(nint avrtHandle);
}
