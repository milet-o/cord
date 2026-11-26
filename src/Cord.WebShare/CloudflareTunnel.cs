using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Cord.WebShare;

/// <summary>
/// Manages an ephemeral Cloudflare Quick Tunnel or an optional user-configured Cloudflare Named
/// Tunnel pointing to the local WebShareServer without router port forwarding or CGNAT setup.
/// </summary>
public sealed partial class CloudflareTunnel : IDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _stderrTask;
    private readonly Task _stdoutTask;
    private volatile string? _publicBaseUrl;
    private volatile string? _error;

    private CloudflareTunnel(Process process, string? fixedPublicBaseUrl = null)
    {
        _process = process;
        _publicBaseUrl = fixedPublicBaseUrl;
        _stderrTask = Task.Run(() => ReadOutputAsync(process.StandardError, _stop.Token));
        _stdoutTask = Task.Run(() => ReadOutputAsync(process.StandardOutput, _stop.Token));
        _process.Exited += (_, _) =>
        {
            if (_publicBaseUrl is null && _error is null && !_stop.IsCancellationRequested)
                _error = "O túnel Cloudflare encerrou antes de gerar o link público.";
            else if (fixedPublicBaseUrl is not null && !_stop.IsCancellationRequested && _error is null)
                _error = "O túnel fixo Cloudflare encerrou. Verifique o token ou a URL configurada.";
        };
    }

    public event Action<string>? PublicUrlReady;

    public string? PublicBaseUrl => _publicBaseUrl;

    public string? Error => _error;

    public static string? FindExecutable()
    {
        var name = OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared";
        var localAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            OperatingSystem.IsWindows() ? "Cord" : "cord",
            name);

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, name),
            localAppData,
        };

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            candidates.Add(Path.Combine(dir.Trim('"'), name));

        return candidates.FirstOrDefault(File.Exists);
    }

    public static CloudflareTunnel Start(int localPort, string? executablePath = null)
    {
        var exe = executablePath ?? FindExecutable()
            ?? throw new FileNotFoundException("cloudflared não encontrado.");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"tunnel --url http://127.0.0.1:{localPort.ToString(CultureInfo.InvariantCulture)} --no-autoupdate",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("Não foi possível iniciar o cloudflared.");

        return new CloudflareTunnel(process);
    }

    public static CloudflareTunnel StartNamed(string tunnelToken, string publicBaseUrl, string? executablePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tunnelToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicBaseUrl);

        var exe = executablePath ?? FindExecutable()
            ?? throw new FileNotFoundException("cloudflared não encontrado.");

        var normalizedUrl = NormalizePublicUrl(publicBaseUrl);
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "tunnel run --no-autoupdate",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.Environment["TUNNEL_TOKEN"] = tunnelToken.Trim();

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("Não foi possível iniciar o cloudflared com o túnel fixo.");

        var tunnel = new CloudflareTunnel(process, normalizedUrl);
        tunnel.PublicUrlReady?.Invoke(normalizedUrl);
        return tunnel;
    }

    internal static string NormalizePublicUrl(string raw)
    {
        var trimmed = raw.Trim().TrimEnd('/');
        if (!trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "https://" + trimmed;
        }

        return trimmed;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: false);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        _process.Dispose();
        _stop.Dispose();
    }

    private async Task ReadOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                    break;

                var match = TunnelUrlRegex().Match(line);
                if (match.Success && _publicBaseUrl is null)
                {
                    _publicBaseUrl = match.Value;
                    PublicUrlReady?.Invoke(match.Value);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TunnelUrlRegex();
}
