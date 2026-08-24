using System.Diagnostics;
using System.Net.Http;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace DSHHarbor;

internal static class AppUpdateService
{
    private const string LatestReleaseApiUrl =
        "https://api.github.com/repos/david1025/dsh-harbor/releases/latest";
    public const string ReleasesUrl =
        "https://github.com/david1025/dsh-harbor/releases";

    private static readonly HttpClient Http = CreateHttpClient();
    // 安装包下载可能远超 30 秒，使用无超时客户端单独下载。
    private static readonly HttpClient DownloadHttp = CreateHttpClient(Timeout.InfiniteTimeSpan);

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
        AppUpdateAsset? asset = null;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            // API 限流时降级为网页重定向，只能拿到版本号，拿不到资产列表。
            (tagName, releaseUrl) = await GetLatestReleaseFromRedirectAsync(cancellationToken);
        }
        else if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidDataException(
                "GitHub 返回 404（未找到），无法读取更新信息。\n\n" +
                "可能原因：仓库被设为私有（未登录用户无法访问，包括本程序），或尚未发布正式 Release。\n" +
                "请确认仓库为 public 后重试，或手动前往 GitHub Releases 页面查看。");
        }
        else
        {
            response.EnsureSuccessStatusCode();
            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
            var root = document.RootElement;
            tagName = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
            releaseUrl = root.TryGetProperty("html_url", out var url) ? url.GetString() : null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                asset = SelectAsset(assets);
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
            string.IsNullOrWhiteSpace(releaseUrl) ? ReleasesUrl : releaseUrl,
            asset);
    }

    /// <summary>
    /// 后台下载指定安装包到本地更新目录，校验大小与 SHA-256 后返回最终文件路径。
    /// </summary>
    public static async Task<string> DownloadInstallerAsync(
        AppUpdateAsset asset,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(asset.Name))
            throw new InvalidDataException("更新资产缺少文件名。");
        var fileName = Path.GetFileName(asset.Name); // 防御性处理，确保不带路径分隔符
        var directory = AppPaths.UpdateDownloadDirectory;
        var finalPath = Path.Combine(directory, fileName);
        var partialPath = finalPath + ".partial";
        Directory.CreateDirectory(directory);

        using (var response = await DownloadHttp.GetAsync(
                   asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(
                partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long totalBytes = 0;
            int read;
            while ((read = await httpStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalBytes += read;
                progress?.Report(totalBytes);
            }
        }

        if (asset.Size >= 0)
        {
            var actualSize = new FileInfo(partialPath).Length;
            if (actualSize != asset.Size)
                throw new InvalidDataException(
                    $"安装包大小校验失败（预期 {asset.Size} 字节，实际 {actualSize} 字节），文件可能不完整。");
        }

        if (!string.IsNullOrWhiteSpace(asset.Sha256))
        {
            await using var stream = new FileStream(
                partialPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!string.Equals(actualHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装包 SHA-256 校验失败，文件可能已损坏或被篡改。");
        }

        File.Move(partialPath, finalPath, overwrite: true);
        return finalPath;
    }

    /// <summary>
    /// 启动更新安装程序。安装器要求管理员权限；用户在 UAC 中取消时会抛出
    /// <see cref="Win32Exception"/>，由调用方决定如何处理。
    /// </summary>
    public static void LaunchInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
    }

    /// <summary>
    /// 读取已下载、待安装的更新；自动清理过期记录（文件丢失或版本不高于当前）。
    /// </summary>
    public static PendingAppUpdate? GetValidPendingUpdate()
    {
        var state = UpdateState.Load();
        var path = state.PendingAppUpdateInstallerPath;
        var version = state.PendingAppUpdateVersion;
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(version))
            return null;

        if (!File.Exists(path))
        {
            ClearPendingUpdate();
            return null;
        }

        if (Version.TryParse(NormalizeVersion(version), out var pending) &&
            Version.TryParse(NormalizeVersion(CurrentVersion), out var current) &&
            pending <= current)
        {
            // 已经装上新版本（或同版本），缓存的安装包没用了。
            TryDeleteFile(path);
            ClearPendingUpdate();
            return null;
        }

        return new PendingAppUpdate(version, path, state.PendingAppUpdateDownloadedUtc);
    }

    public static void SavePendingUpdate(string version, string installerPath)
    {
        var state = UpdateState.Load();
        state.PendingAppUpdateVersion = version;
        state.PendingAppUpdateInstallerPath = installerPath;
        state.PendingAppUpdateDownloadedUtc = DateTimeOffset.UtcNow;
        state.Save();

        // 清掉更新目录里其他历史安装包，避免越积越多。
        try
        {
            foreach (var file in Directory.EnumerateFiles(AppPaths.UpdateDownloadDirectory, "*.exe"))
            {
                if (!string.Equals(file, installerPath, StringComparison.OrdinalIgnoreCase))
                    TryDeleteFile(file);
            }
            foreach (var file in Directory.EnumerateFiles(AppPaths.UpdateDownloadDirectory, "*.partial"))
                TryDeleteFile(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Write($"Unable to clean up old update packages: {exception.Message}");
        }
    }

    public static void ClearPendingUpdate()
    {
        var state = UpdateState.Load();
        state.PendingAppUpdateVersion = null;
        state.PendingAppUpdateInstallerPath = null;
        state.PendingAppUpdateDownloadedUtc = null;
        state.Save();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Write($"Unable to delete update package {path}: {exception.Message}");
        }
    }

    private static async Task<(string TagName, string ReleaseUrl)> GetLatestReleaseFromRedirectAsync(
        CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync($"{ReleasesUrl}/latest", HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidDataException(
                "GitHub 返回 404（未找到），无法读取更新信息。\n\n" +
                "可能原因：仓库被设为私有（未登录用户无法访问，包括本程序），或尚未发布正式 Release。\n" +
                "请确认仓库为 public 后重试，或手动前往 GitHub Releases 页面查看。");
        }
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

    /// <summary>
    /// 从 Release 资产列表中挑选与当前进程架构匹配的安装包。
    /// 命名约定：x64 为 DSHHarborSetup-&lt;version&gt;.exe，arm64 带 -arm64 后缀。
    /// </summary>
    private static AppUpdateAsset? SelectAsset(JsonElement assets)
    {
        AppUpdateAsset? best = null;
        var bestScore = 0;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) ||
                !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;

            var url = asset.TryGetProperty("browser_download_url", out var urlElement)
                ? urlElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var score = ScoreAssetName(name);
            if (score <= bestScore)
                continue;

            var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var sizeValue)
                ? sizeValue
                : -1L;
            var digest = asset.TryGetProperty("digest", out var digestElement)
                ? digestElement.GetString()
                : null;
            string? sha256 = null;
            if (!string.IsNullOrEmpty(digest) &&
                digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha256 = digest["sha256:".Length..];

            bestScore = score;
            best = new AppUpdateAsset(name, url, size, sha256);
        }

        return best;
    }

    private static int ScoreAssetName(string name)
    {
        var lower = name.ToLowerInvariant();
        var containsArm64 = lower.Contains("arm64") || lower.Contains("aarch64");
        var containsX64 = lower.Contains("x64") || lower.Contains("amd64");
        var containsX86 = lower.Contains("x86") && !containsX64;

        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            if (containsArm64) return 3;
            if (containsX64 || containsX86) return 2; // ARM64 设备上回退到 x64/x86 模拟运行
            return 1;
        }

        if (containsArm64) return 0; // x64/x86 进程不能运行 arm64 安装包
        if (containsX64 || containsX86) return 3;
        return 2; // 无架构标记的默认（x64compatible）安装包
    }

    private static HttpClient CreateHttpClient(TimeSpan? timeout = null)
    {
        var client = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
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
    string ReleaseUrl,
    AppUpdateAsset? Asset = null);

internal sealed record AppUpdateAsset(
    string Name,
    string DownloadUrl,
    long Size,
    string? Sha256);

internal sealed record PendingAppUpdate(
    string Version,
    string InstallerPath,
    DateTimeOffset? DownloadedUtc);
