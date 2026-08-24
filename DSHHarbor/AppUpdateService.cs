using System.Net.Http;
using System.Net;
using System.Reflection;
using System.Text.Json;

namespace DSHHarbor;

internal static class AppUpdateService
{
    private const string LatestReleaseApiUrl =
        "https://api.github.com/repos/david1025/dsh-harbor/releases/latest";
    public const string ReleasesUrl =
        "https://github.com/david1025/dsh-harbor/releases";

    private static readonly HttpClient Http = CreateHttpClient();

    public static string CurrentVersion
    {
        get
        {
            var informationalVersion = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informationalVersion))
                return informationalVersion.Split('+')[0];

            return Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "未知";
        }
    }

    public static async Task<AppUpdateResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync(LatestReleaseApiUrl, cancellationToken);
        string? tagName;
        string? releaseUrl;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            (tagName, releaseUrl) = await GetLatestReleaseFromRedirectAsync(cancellationToken);
        }
        else
        {
            response.EnsureSuccessStatusCode();
            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
            var root = document.RootElement;
            tagName = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
            releaseUrl = root.TryGetProperty("html_url", out var url) ? url.GetString() : null;
        }

        if (string.IsNullOrWhiteSpace(tagName))
            throw new InvalidDataException("GitHub Release 未包含版本号。");

        var latestVersion = NormalizeVersion(tagName);
        var currentVersion = NormalizeVersion(CurrentVersion);
        if (!Version.TryParse(latestVersion, out var latest) || !Version.TryParse(currentVersion, out var current))
            throw new InvalidDataException($"无法比较版本号（当前 {CurrentVersion}，最新 {tagName}）。");

        return new AppUpdateResult(
            CurrentVersion,
            latestVersion,
            latest > current,
            string.IsNullOrWhiteSpace(releaseUrl) ? ReleasesUrl : releaseUrl);
    }

    private static async Task<(string TagName, string ReleaseUrl)> GetLatestReleaseFromRedirectAsync(
        CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync($"{ReleasesUrl}/latest", HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var releaseUrl = response.RequestMessage?.RequestUri?.AbsoluteUri;
        if (string.IsNullOrWhiteSpace(releaseUrl))
            throw new InvalidDataException("GitHub 未返回最新 Release 地址。");

        var markerIndex = releaseUrl.IndexOf("/tag/", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            throw new InvalidDataException("GitHub 最新 Release 地址未包含版本标签。");

        var tagName = Uri.UnescapeDataString(releaseUrl[(markerIndex + "/tag/".Length)..]).TrimEnd('/');
        return (tagName, releaseUrl);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"DSH-Harbor/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static string NormalizeVersion(string version)
    {
        var normalized = version.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V')) normalized = normalized[1..];
        var suffixIndex = normalized.IndexOfAny(['-', '+']);
        return suffixIndex >= 0 ? normalized[..suffixIndex] : normalized;
    }
}

internal sealed record AppUpdateResult(
    string CurrentVersion,
    string LatestVersion,
    bool IsUpdateAvailable,
    string ReleaseUrl);
