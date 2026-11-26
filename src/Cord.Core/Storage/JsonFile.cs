using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cord.Core.Storage;

/// <summary>Reads and atomically writes JSON documents.</summary>
public static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Load<T>(string path, Func<T> fallback)
    {
        if (!File.Exists(path))
            return fallback();
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options) ?? fallback();
        }
        catch (JsonException)
        {
            return fallback();
        }
    }

    public static void Save<T>(string path, T value, bool restrictAccess = false)
    {
        var temporary = path + ".tmp";
        using (var stream = File.Create(temporary))
            JsonSerializer.Serialize(stream, value, Options);
        if (restrictAccess && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
    }
}
