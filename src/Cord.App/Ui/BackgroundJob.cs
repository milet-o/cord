using System.Net.Sockets;
using Cord.Protocol;
using Cord.Psn;

namespace Cord.App.Ui;

/// <summary>Runs one network operation at a time for a panel and keeps its outcome for display.</summary>
internal sealed class BackgroundJob
{
    private volatile bool _busy;
    private volatile string? _error;

    public bool Busy => _busy;

    public string? Error => _error;

    public void ClearError() => _error = null;

    public void Run(Func<Task> action)
    {
        if (_busy)
            return;
        _busy = true;
        _error = null;
        _ = RunAsync(action);
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e) when (e is RemotePlayException or PsnException or IOException or HttpRequestException or SocketException or OperationCanceledException or FormatException or ArgumentException)
        {
            _error = e.Message;
        }
        finally
        {
            _busy = false;
        }
    }
}
