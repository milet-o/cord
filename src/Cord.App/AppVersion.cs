using System.Reflection;

namespace Cord.App;

/// <summary>Resolves the application's build version and GitHub repository metadata.</summary>
internal static class AppVersion
{
    public static string Current { get; } = ResolveCurrent();

    public static string Display => $"v{Current}";

    public static string? GitHubRepository { get; } = ResolveGitHubRepository();

    public static string? NormalizeRepository(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim().Trim('/');
        var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash >= trimmed.Length - 1)
            return null;
        if (trimmed.IndexOf('/', slash + 1) >= 0)
            return null;

        return trimmed;
    }

    private static string ResolveCurrent()
    {
        var assembly = typeof(AppVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? informational[..plus] : informational;
        }

        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static string? ResolveGitHubRepository()
    {
        var fromEnv = NormalizeRepository(Environment.GetEnvironmentVariable("CORD_GITHUB_REPO"));
        if (fromEnv is not null)
            return fromEnv;

        var meta = typeof(AppVersion).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "GitHubRepository", StringComparison.Ordinal))
            ?.Value;
        return NormalizeRepository(meta);
    }
}
