namespace Cord.Core.Storage;

/// <summary>Per-user data directory (docs/architecture.md#configuration-and-storage).</summary>
public sealed class AppPaths
{
    public AppPaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        Directory.CreateDirectory(dataDirectory);
    }

    public string DataDirectory { get; }

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public string ConsolesFile => Path.Combine(DataDirectory, "consoles.json");

    public string AccountFile => Path.Combine(DataDirectory, "account.json");

    /// <summary>Overrides the data directory, for example to try the first-run setup without losing data.</summary>
    public const string DataDirectoryVariable = "CORD_DATA_DIR";

    public static AppPaths Default()
    {
        var custom = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        if (!string.IsNullOrEmpty(custom))
            return new AppPaths(custom);

        string root;
        if (OperatingSystem.IsWindows())
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            root = string.IsNullOrEmpty(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : xdg;
        }

        return new AppPaths(Path.Combine(root, OperatingSystem.IsWindows() ? "Cord" : "cord"));
    }
}
