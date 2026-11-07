using System.Net.Http;
using System.Text.Json;

namespace CodexBar.Core;

/// <summary>A newer release on GitHub: its version, the page to download it from, and notes.</summary>
/// <summary>
/// A newer release on GitHub: its version, the page to download it from, and notes. When the
/// release carries an installer and its SHA-256 (a "&lt;installer&gt;.sha256" or SHA256SUMS asset) it
/// can be installed from inside the app.
/// </summary>
public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? DownloadUrl, string Notes,
    string? InstallerName = null, string? ChecksumUrl = null)
{
    public bool CanInstall => InstallerName is not null && DownloadUrl is not null && ChecksumUrl is not null;
}

/// <summary>
/// Looks for a newer TheAI release in a public GitHub repository ("owner/name"). It only
/// reads the latest release; downloading and installing stay with the person.
/// </summary>
public static class UpdateChecker
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TheAI-UpdateCheck");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>The latest release when it is newer than <paramref name="current"/>; null otherwise or when the repository has none.</summary>
    public static async Task<UpdateInfo?> CheckAsync(string repository, Version current, CancellationToken cancellationToken = default)
    {
        if (!IsRepository(repository))
        {
            throw new ArgumentException("Repositori harus berformat pemilik/nama.", nameof(repository));
        }

        using var response = await Http.GetAsync($"https://api.github.com/repos/{repository.Trim()}/releases/latest", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null; // no such public repository, or no release yet
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var latest = Parse(json);
        return latest is not null && latest.Version > Normalize(current) ? latest : null;
    }

    /// <summary>Reads a GitHub "latest release" response (tag like "v1.3.0"); null when it has no version.</summary>
    public static UpdateInfo? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? string.Empty : string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
        {
            return null;
        }

        string? download = null;
        string? installer = null;
        var checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            // Prefer the installer, else any Windows executable.
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (url is null)
                {
                    continue;
                }

                if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) || name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    checksums[name] = url;
                    continue;
                }

                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var isSetup = name.Contains("Setup", StringComparison.OrdinalIgnoreCase);
                if (download is null || isSetup)
                {
                    download = url;
                    installer = isSetup ? name : installer;
                }
            }
        }

        var checksum = installer is null ? null
            : checksums.GetValueOrDefault(installer + ".sha256") ?? checksums.GetValueOrDefault("SHA256SUMS") ?? checksums.GetValueOrDefault("SHA256SUMS.txt");

        return new UpdateInfo(
            Normalize(version),
            tag,
            root.TryGetProperty("html_url", out var page) ? page.GetString() ?? string.Empty : string.Empty,
            download,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
            installer,
            checksum);
    }

    public static bool IsRepository(string? repository) =>
        repository is not null && System.Text.RegularExpressions.Regex.IsMatch(repository.Trim(), @"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$");

    // 1.2 == 1.2.0 == 1.2.0.0
    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
}
