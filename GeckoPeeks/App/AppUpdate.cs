using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeckoPeeks.App;

/// <summary>A newer GitHub release that can replace this exe.</summary>
public sealed record UpdateOffer(Version Version, string Tag, string DownloadUrl, long SizeBytes, string ReleaseUrl);

/// <summary>
/// Parses GitHub release JSON and compares versions. The download and the file swap live in
/// <see cref="UpdateClient"/> / <see cref="UpdateInstaller"/>.
/// </summary>
public static class AppUpdate
{
    public const string Owner = "gecko-of-shadow";
    public const string Repo = "gecko-peeks";
    public const string AssetName = "GeckoPeeks.exe";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string ApiLatestUrl =>
        $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    /// <summary>Three-part version from the assembly (1.0.12.0 → 1.0.12).</summary>
    public static Version CurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    /// <summary>Reads <c>v1.0.12</c> or <c>1.0.12</c>. Other tags are ignored.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        if (!Version.TryParse(text, out var version))
            return null;

        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    public static bool IsNewer(Version candidate, Version current) =>
        Compare(candidate, current) > 0;

    public static int Compare(Version a, Version b)
    {
        var major = a.Major.CompareTo(b.Major);
        if (major != 0)
            return major;
        var minor = a.Minor.CompareTo(b.Minor);
        if (minor != 0)
            return minor;
        return Math.Max(0, a.Build).CompareTo(Math.Max(0, b.Build));
    }

    /// <summary>
    /// Picks the GeckoPeeks.exe asset when the release tag is newer than <paramref name="current"/>.
    /// </summary>
    public static UpdateOffer? PickOffer(string releaseJson, Version current)
    {
        GitHubRelease? release;
        try
        {
            release = JsonSerializer.Deserialize<GitHubRelease>(releaseJson, Json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (release == null)
            return null;

        var version = ParseTag(release.TagName);
        if (version == null || !IsNewer(version, current))
            return null;

        var asset = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl));
        if (asset == null)
            return null;

        return new UpdateOffer(
            version,
            release.TagName.Trim(),
            asset.BrowserDownloadUrl,
            asset.Size,
            string.IsNullOrWhiteSpace(release.HtmlUrl) ? ApiLatestUrl : release.HtmlUrl);
    }

    /// <summary>Deletes a leftover <c>.old</c> from a previous swap. Safe to call every launch.</summary>
    public static void CleanupLeftovers(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return;

        try
        {
            var old = exePath + ".old";
            if (File.Exists(old))
                File.Delete(old);
        }
        catch
        {
            // Antivirus or Explorer may hold it briefly; the next launch tries again.
        }
    }

    public static string FormatVersion(Version version) =>
        $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
}

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = "";

    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = new();
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
