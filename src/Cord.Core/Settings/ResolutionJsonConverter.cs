using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cord.Core.Settings;

/// <summary>
/// Reads resolutions by name. Earlier versions offered 1440p and 2160p, which the console
/// rejects; settings saved with them, or with any unknown name, load as 1080p instead of
/// discarding the whole settings file.
/// </summary>
internal sealed class ResolutionJsonConverter : JsonConverter<Resolution>
{
    public override Resolution Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Enum.TryParse<Resolution>(reader.GetString(), out var value) && Enum.IsDefined(value)
            ? value
            : Resolution.P1080;

    public override void Write(Utf8JsonWriter writer, Resolution value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
