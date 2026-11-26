using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Cord.App.Updating;

/// <summary>Downloads and applies Windows portable release updates cleanly via an out-of-process PowerShell helper.</summary>
internal static class AppUpdater
{
    public static bool CanSelfUpdateWindows(string? baseDirectory = null)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var dir = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var exePath = Path.Combine(dir, "Cord.exe");
        if (!File.Exists(exePath))
            return false;

        return !dir.Contains("\\bin\\Debug\\", StringComparison.OrdinalIgnoreCase)
            && !dir.Contains("\\bin\\Release\\", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<(bool RelaunchScheduled, string Message)> DownloadAndApplyWindowsUpdateAsync(
        HttpClient http,
        GitHubReleaseInfo release,
        ReleaseAsset asset,
        Action<float> reportProgress,
        CancellationToken cancellationToken)
    {
        var targetDir = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var safeVersion = SanitizePathSegment(release.Version);
        var stagingRoot = Path.Combine(Path.GetTempPath(), "CordUpdate", safeVersion);
        var zipPath = Path.Combine(stagingRoot, "update.zip");
        var extractDir = Path.Combine(stagingRoot, "extracted");

        try
        {
            if (Directory.Exists(stagingRoot))
                Directory.Delete(stagingRoot, recursive: true);
            Directory.CreateDirectory(stagingRoot);

            reportProgress(0.02f);
            using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(asset.DownloadUrl)))
            {
                request.Headers.UserAgent.ParseAdd($"Cord/{AppVersion.Current}");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return (false, $"Download failed (HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}).");

                var totalBytes = response.Content.Headers.ContentLength ?? asset.SizeBytes;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var dest = File.Create(zipPath);
                var buffer = new byte[65536];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    if (totalBytes > 0)
                        reportProgress(Math.Clamp((float)received / totalBytes * 0.9f, 0.05f, 0.9f));
                }
            }

            reportProgress(0.92f);
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            var payloadRoot = FindPayloadRoot(extractDir);
            if (payloadRoot is null)
                return (false, "Downloaded archive did not contain Cord.exe.");

            var scriptPath = Path.Combine(stagingRoot, "apply-update.ps1");
            var script = BuildApplyScript(
                Environment.ProcessId,
                payloadRoot,
                targetDir,
                Path.Combine(targetDir, "Cord.exe"),
                stagingRoot);
            await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);

            reportProgress(1.0f);
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-WindowStyle");
            startInfo.ArgumentList.Add("Hidden");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            Process.Start(startInfo);
            return (true, "Update ready. Restarting Cord...");
        }
        catch (HttpRequestException e)
        {
            return (false, $"Download failed: {e.Message}");
        }
        catch (TaskCanceledException)
        {
            return (false, "Update download was cancelled or timed out.");
        }
        catch (IOException e)
        {
            return (false, $"File error while preparing update: {e.Message}");
        }
        catch (UnauthorizedAccessException e)
        {
            return (false, $"Permission denied while preparing update: {e.Message}");
        }
        catch (InvalidDataException)
        {
            return (false, "Downloaded update archive is invalid or corrupted.");
        }
        catch (Win32Exception e)
        {
            return (false, $"Could not launch updater script: {e.Message}");
        }
    }

    private static string? FindPayloadRoot(string extractDir)
    {
        if (File.Exists(Path.Combine(extractDir, "Cord.exe")))
            return extractDir;

        foreach (var subDir in Directory.EnumerateDirectories(extractDir))
        {
            if (File.Exists(Path.Combine(subDir, "Cord.exe")))
                return subDir;
        }

        return null;
    }

    private static string BuildApplyScript(int pid, string sourceDir, string targetDir, string exePath, string stagingRoot)
    {
        static string EscapePs(string s) => s.Replace("'", "''", StringComparison.Ordinal);

        return """
            $ErrorActionPreference = 'SilentlyContinue'
            $pidToWait = __PID__
            for ($i = 0; $i -lt 80; $i++) {
                if (-not (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue)) { break }
                Start-Sleep -Milliseconds 250
            }
            Start-Sleep -Milliseconds 300
            Copy-Item -Path '__SOURCE__\*' -Destination '__TARGET__' -Recurse -Force
            Start-Process -FilePath '__EXE__' -WorkingDirectory '__TARGET__'
            Start-Sleep -Seconds 2
            Remove-Item -Path '__STAGING__' -Recurse -Force
            """
            .Replace("__PID__", pid.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__SOURCE__", EscapePs(sourceDir), StringComparison.Ordinal)
            .Replace("__TARGET__", EscapePs(targetDir), StringComparison.Ordinal)
            .Replace("__EXE__", EscapePs(exePath), StringComparison.Ordinal)
            .Replace("__STAGING__", EscapePs(stagingRoot), StringComparison.Ordinal);
    }

    private static string SanitizePathSegment(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        return sb.Length == 0 ? "latest" : sb.ToString();
    }
}
