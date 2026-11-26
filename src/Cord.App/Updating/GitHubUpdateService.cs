using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Cord.App.Updating;

internal sealed record ReleaseAsset(string Name, string DownloadUrl, long SizeBytes);

internal sealed record GitHubReleaseInfo(
    string TagName,
    string Version,
    string Title,
    DateTimeOffset PublishedAt,
    string Body,
    string HtmlUrl,
    bool IsPrerelease,
    IReadOnlyList<ReleaseAsset> Assets);

internal sealed record UpdateCheckResult(
    GitHubReleaseInfo? LatestRelease,
    IReadOnlyList<GitHubReleaseInfo> Releases,
    bool UpdateAvailable,
    string? Error);

/// <summary>Queries and parses GitHub Releases for live patch notes and update checks.</summary>
internal static class GitHubUpdateService
{
    public static IReadOnlyList<GitHubReleaseInfo> ParseReleasesJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<GitHubReleaseInfo>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            if (item.TryGetProperty("draft", out var draftProp) && draftProp.ValueKind == JsonValueKind.True)
                continue;

            var tag = GetString(item, "tag_name");
            if (string.IsNullOrWhiteSpace(tag))
                continue;

            var normalizedVersion = tag.Trim();
            if (normalizedVersion.StartsWith('v') || normalizedVersion.StartsWith('V'))
                normalizedVersion = normalizedVersion[1..];

            var name = GetString(item, "name");
            var title = string.IsNullOrWhiteSpace(name) ? $"Cord v{normalizedVersion}" : name.Trim();
            var body = GetString(item, "body") ?? "";
            var htmlUrl = GetString(item, "html_url") ?? "";
            var isPrerelease = item.TryGetProperty("prerelease", out var preProp) && preProp.ValueKind == JsonValueKind.True;

            var publishedAt = DateTimeOffset.MinValue;
            if (item.TryGetProperty("published_at", out var pubProp) && pubProp.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(pubProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedDate))
            {
                publishedAt = parsedDate;
            }
            else if (item.TryGetProperty("created_at", out var createdProp) && createdProp.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(createdProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedCreated))
            {
                publishedAt = parsedCreated;
            }

            var assets = new List<ReleaseAsset>();
            if (item.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var assetItem in assetsProp.EnumerateArray())
                {
                    if (assetItem.ValueKind != JsonValueKind.Object)
                        continue;
                    var assetName = GetString(assetItem, "name");
                    var downloadUrl = GetString(assetItem, "browser_download_url");
                    if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(downloadUrl))
                        continue;
                    var size = assetItem.TryGetProperty("size", out var sizeProp) && sizeProp.TryGetInt64(out var s) ? s : 0L;
                    assets.Add(new ReleaseAsset(assetName, downloadUrl, size));
                }
            }

            list.Add(new GitHubReleaseInfo(tag.Trim(), normalizedVersion, title, publishedAt, body, htmlUrl, isPrerelease, assets));
        }

        list.Sort((a, b) =>
        {
            var aParsed = SemanticVersion.TryParse(a.Version, out var va);
            var bParsed = SemanticVersion.TryParse(b.Version, out var vb);
            if (aParsed && bParsed)
            {
                var cmp = vb.CompareTo(va);
                if (cmp != 0)
                    return cmp;
            }
            else if (aParsed != bParsed)
            {
                return bParsed ? 1 : -1;
            }

            return b.PublishedAt.CompareTo(a.PublishedAt);
        });

        return list;
    }

    public static string[] ParseBodyToBullets(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return ["See full release notes on GitHub."];

        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        var result = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith('#'))
                line = line.TrimStart('#').Trim();
            else if (line.StartsWith("- ", StringComparison.Ordinal)
                || line.StartsWith("* ", StringComparison.Ordinal)
                || line.StartsWith("• ", StringComparison.Ordinal))
                line = line[2..].Trim();

            line = line.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal).Trim();
            if (line.Length == 0)
                continue;

            result.Add(line);
            if (result.Count >= 16)
                break;
        }

        return result.Count == 0 ? ["See full release notes on GitHub."] : [.. result];
    }

    public static PatchEntry ToPatchEntry(GitHubReleaseInfo release)
    {
        var versionLabel = release.TagName.StartsWith('v') || release.TagName.StartsWith('V')
            ? release.TagName
            : $"v{release.Version}";
        var dateLabel = release.PublishedAt == DateTimeOffset.MinValue
            ? "GitHub Release"
            : release.PublishedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

        return new PatchEntry(versionLabel, dateLabel, release.Title, ParseBodyToBullets(release.Body));
    }

    public static ReleaseAsset? SelectPlatformAsset(IReadOnlyList<ReleaseAsset> assets) =>
        OperatingSystem.IsWindows() ? SelectWindowsZipAsset(assets) : null;

    public static ReleaseAsset? SelectWindowsZipAsset(IReadOnlyList<ReleaseAsset> assets)
    {
        if (assets.Count == 0)
            return null;

        ReleaseAsset? winGeneric = null;
        ReleaseAsset? anyZip = null;
        foreach (var asset in assets)
        {
            if (!asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                continue;

            if (asset.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)
                || asset.Name.Contains("win64", StringComparison.OrdinalIgnoreCase))
            {
                return asset;
            }

            if (winGeneric is null && asset.Name.Contains("win", StringComparison.OrdinalIgnoreCase))
                winGeneric = asset;

            anyZip ??= asset;
        }

        return winGeneric ?? anyZip;
    }

    public static async Task<UpdateCheckResult> FetchAsync(
        HttpClient http,
        string repository,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var normalizedRepo = AppVersion.NormalizeRepository(repository);
        if (normalizedRepo is null)
            return new UpdateCheckResult(null, [], false, "Invalid GitHub repository setting.");

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri($"https://api.github.com/repos/{normalizedRepo}/releases?per_page=15"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Cord", currentVersion));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                return status == 404
                    ? new UpdateCheckResult(null, [], false, "No published releases found on GitHub yet.")
                    : new UpdateCheckResult(null, [], false, $"GitHub returned HTTP {status.ToString(CultureInfo.InvariantCulture)}.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var releases = ParseReleasesJson(json);
            var latest = releases.Count > 0 ? releases[0] : null;
            var updateAvailable = latest is not null && SemanticVersion.IsNewer(latest.Version, currentVersion);
            return new UpdateCheckResult(latest, releases, updateAvailable, null);
        }
        catch (HttpRequestException e)
        {
            return new UpdateCheckResult(null, [], false, $"Network error: {e.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new UpdateCheckResult(null, [], false, "GitHub request timed out.");
        }
        catch (JsonException e)
        {
            return new UpdateCheckResult(null, [], false, $"Invalid GitHub release payload: {e.Message}");
        }
    }

    private static string? GetString(JsonElement obj, string propertyName) =>
        obj.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}
