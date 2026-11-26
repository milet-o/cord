namespace Cord.Media.Tests;

public sealed class FfmpegLibraryTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cord-ffmpeg-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ContainsCompatibleAvcodec_RequiresMatchingMajorVersion()
    {
        var (compatible, other) = OperatingSystem.IsWindows()
            ? ("avcodec-63.dll", "avcodec-61.dll")
            : ("libavcodec.so.63", "libavcodec.so.61");

        File.WriteAllText(Path.Combine(_directory, other), "");
        Assert.False(FfmpegLibrary.ContainsCompatibleAvcodec(_directory));

        File.WriteAllText(Path.Combine(_directory, compatible), "");
        Assert.True(FfmpegLibrary.ContainsCompatibleAvcodec(_directory));
    }

    [Fact]
    public void CandidateDirectories_StartWithEnvironmentThenApplicationDirectory()
    {
        var previous = Environment.GetEnvironmentVariable(FfmpegLibrary.PathEnvironmentVariable);
        Environment.SetEnvironmentVariable(FfmpegLibrary.PathEnvironmentVariable, _directory);
        try
        {
            var candidates = FfmpegLibrary.CandidateDirectories().Take(2).ToList();

            Assert.Equal(Path.GetFullPath(_directory), candidates[0]);
            Assert.Equal(Path.GetFullPath(AppContext.BaseDirectory), candidates[1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FfmpegLibrary.PathEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void Bundled_LibrariesAreCopiedNextToTheTests()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return;
        Assert.True(FfmpegLibrary.ContainsCompatibleAvcodec(AppContext.BaseDirectory), "the build should bundle FFmpeg");
    }
}
