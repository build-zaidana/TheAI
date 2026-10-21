using System.Net.Http;
using System.Security.Cryptography;

namespace CodexBar.Core;

/// <summary>
/// Downloads a release's installer for an in-app update, and only hands it over when its SHA-256
/// matches the checksum published with the release. Only GitHub-hosted https links are fetched.
/// </summary>
public static class UpdateInstaller
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TheAI-Update");
        return client;
    }

    /// <summary>The installer's path once downloaded and verified; throws when it can't be trusted.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, string folder, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        if (!update.CanInstall || !IsGitHub(update.DownloadUrl) || !IsGitHub(update.ChecksumUrl) ||
            update.InstallerName!.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("This release can't be installed from inside the app.");
        }

        var checksumText = await Http.GetStringAsync(update.ChecksumUrl, cancellationToken).ConfigureAwait(false);
        var expected = ExpectedHash(checksumText, update.InstallerName)
            ?? throw new InvalidOperationException("The release has no checksum for its installer.");

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, update.InstallerName);
        using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = File.Create(path);
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                if (total is > 0)
                {
                    progress?.Report((double)received / total.Value);
                }
            }
        }

        if (!Matches(path, expected))
        {
            File.Delete(path);
            throw new InvalidOperationException("The downloaded installer doesn't match the release's checksum.");
        }

        return path;
    }

    /// <summary>The hash for <paramref name="fileName"/> in a checksum file: "hash", or "hash  name" lines (sha256sum format).</summary>
    public static string? ExpectedHash(string checksums, string fileName)
    {
        var lines = checksums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !IsHash(parts[0]))
            {
                continue;
            }

            if (parts.Length == 1 && lines.Length == 1)
            {
                return parts[0].ToLowerInvariant();
            }

            if (parts.Length == 2 && parts[1].TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        return null;
    }

    public static bool Matches(string path, string expectedHash)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHash(string text) => text.Length == 64 && text.All(Uri.IsHexDigit);

    private static bool IsGitHub(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host == "github.com" || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
}
