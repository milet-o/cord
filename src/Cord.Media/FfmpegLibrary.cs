using FFmpeg.AutoGen;

namespace Cord.Media;

/// <summary>Locates and loads the shared FFmpeg libraries once per process.</summary>
public static class FfmpegLibrary
{
    public const string PathEnvironmentVariable = "CORD_FFMPEG_PATH";

    /// <summary>Major version of libavcodec the bindings were generated for (FFmpeg 9).</summary>
    public const int AvcodecMajorVersion = 63;

    private static readonly Lock Gate = new();
    private static string? _version;

    /// <summary>
    /// Loads FFmpeg from <paramref name="directory"/> when given, otherwise from the first
    /// directory found by <see cref="FindLibraryDirectory"/>. Returns the FFmpeg version string.
    /// </summary>
    public static string Load(string? directory = null)
    {
        lock (Gate)
        {
            if (_version is not null)
                return _version;

            var root = string.IsNullOrWhiteSpace(directory) ? FindLibraryDirectory() : directory;
            if (root is null)
            {
                throw new DllNotFoundException(
                    $"FFmpeg {AvcodecMajorVersion - 54} shared libraries were not found. Searched: {string.Join("; ", CandidateDirectories())}");
            }

            ffmpeg.RootPath = root;
            DynamicallyLoadedBindings.Initialize();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            _version = ffmpeg.av_version_info();
            return _version;
        }
    }

    /// <summary>
    /// Returns the first directory containing a compatible libavcodec: CORD_FFMPEG_PATH, the
    /// application directory (where the build places the bundled FFmpeg), then the usual
    /// install locations of each platform and the PATH.
    /// </summary>
    public static string? FindLibraryDirectory() => CandidateDirectories().FirstOrDefault(ContainsCompatibleAvcodec);

    internal static IEnumerable<string> CandidateDirectories()
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var directory in RawCandidates())
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"')));
            if (seen.Add(full))
                yield return full;
        }
    }

    internal static bool ContainsCompatibleAvcodec(string directory)
    {
        var name = OperatingSystem.IsWindows()
            ? $"avcodec-{AvcodecMajorVersion}.dll"
            : OperatingSystem.IsMacOS() ? $"libavcodec.{AvcodecMajorVersion}.dylib" : $"libavcodec.so.{AvcodecMajorVersion}";
        return File.Exists(Path.Combine(directory, name));
    }

    internal static string ErrorText(int error)
    {
        unsafe
        {
            const int size = 256;
            var buffer = stackalloc byte[size];
            ffmpeg.av_strerror(error, buffer, size);
            return new string((sbyte*)buffer);
        }
    }

    private static IEnumerable<string?> RawCandidates()
    {
        yield return Environment.GetEnvironmentVariable(PathEnvironmentVariable);
        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg");

        if (OperatingSystem.IsWindows())
        {
            foreach (var directory in WindowsLocations())
                yield return directory;
        }
        else
        {
            yield return "/usr/lib/x86_64-linux-gnu";
            yield return "/usr/lib/aarch64-linux-gnu";
            yield return "/usr/lib64";
            yield return "/usr/lib";
            yield return "/usr/local/lib";
            yield return "/opt/homebrew/lib";
            yield return "/app/lib";
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            yield return directory;
    }

    private static IEnumerable<string> WindowsLocations()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        yield return Path.Combine(programFiles, "ffmpeg", "bin");
        yield return @"C:\ffmpeg\bin";
        yield return Path.Combine(userProfile, "scoop", "apps", "ffmpeg-shared", "current", "bin");
        yield return Path.Combine(userProfile, "scoop", "apps", "ffmpeg", "current", "bin");
        yield return Path.Combine(programData, "chocolatey", "lib", "ffmpeg-shared", "tools", "ffmpeg", "bin");
        yield return @"C:\msys64\ucrt64\bin";
        yield return @"C:\msys64\mingw64\bin";
        yield return @"C:\msys64\clang64\bin";

        // winget (Gyan.FFmpeg.Shared and similar) extracts into versioned folders.
        var winget = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
        {
            foreach (var package in Directory.EnumerateDirectories(winget, "*FFmpeg*"))
            {
                foreach (var bin in Directory.EnumerateDirectories(package, "bin", SearchOption.AllDirectories))
                    yield return bin;
            }
        }
    }
}
