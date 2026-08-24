using System.Diagnostics;
using System.ComponentModel;
using System.IO.Compression;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DSHHarbor;

internal sealed class DshRuntime
{
    private const string NodeVersion = "22.19.0";
    private const string NodeArchiveSha256 = "EA3FAD0E67A991D8477D8C01344B56E69C676CCB733F065B22436994B1253F86";
    // Pin the initial release rather than allowing an unreviewed compatibility-breaking upgrade.
    private const string DshPackageVersion = "0.1.1-rc.2";
    // pnpm 11 blocks lifecycle scripts until they are explicitly approved. These are
    // the scripts reported by the official DSH package dependency graph.
    private const string PnpmWorkspacePolicy = """
        packages:
          - .
        nodeLinker: hoisted
        allowBuilds:
          '@deepseek-ai/dsh-subprocess-local': true
          '@google/genai': true
          koffi: true
          node-pty: true
          protobufjs: true
        """;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private Process? _dshProcess;
    private Process? _bootstrapProcess;
    private WindowsJob? _dshJob;
    private WindowsJob? _bootstrapJob;
    private string? _activeNodeVersion;

    private static string NodeFolder => Path.Combine(AppPaths.RuntimeDirectory, $"node-v{NodeVersion}-win-x64");
    private static string NodeExe => Path.Combine(NodeFolder, "node.exe");
    private static string NpmCmd => Path.Combine(NodeFolder, "npm.cmd");
    private static string CorepackCmd => Path.Combine(NodeFolder, "corepack.cmd");
    private static string DshCmd => Path.Combine(AppPaths.HarnessDirectory, "node_modules", ".bin", "dsh.cmd");
    private static string DshPackageJson => Path.Combine(AppPaths.HarnessDirectory, "node_modules", "@deepseek-ai", "dsh", "package.json");

    public async Task<string> StartAsync(IProgress<BootstrapUpdate> progress, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(AppPaths.Root);
        EnsureDshHomeLocation();
        var node = await EnsureNodeAsync(progress, cancellationToken);
        _activeNodeVersion = node.Version;
        var wasInstalled = await EnsureHarnessAsync(node, progress, cancellationToken);
        if (wasInstalled)
            throw new LauncherRestartRequiredException("DeepSeek Harness 安装完成，正在重启运行环境。");
        if (!wasInstalled) await CheckForHarnessUpdateAsync(progress, cancellationToken);
        await PrepareProfileModuleFallbackAsync(node, progress, cancellationToken);
        return await StartServiceAsync(node, progress, cancellationToken, 3);
    }

    private static void EnsureDshHomeLocation()
    {
        Directory.CreateDirectory(AppPaths.HarnessDirectory);
        if (Directory.Exists(AppPaths.DshHomeDirectory)) return;

        if (Directory.Exists(AppPaths.LegacyDshHomeDirectory))
        {
            Directory.Move(AppPaths.LegacyDshHomeDirectory, AppPaths.DshHomeDirectory);
            RuntimeLog.Write($"Migrated DSH_HOME from {AppPaths.LegacyDshHomeDirectory} to {AppPaths.DshHomeDirectory}.");
            return;
        }

        Directory.CreateDirectory(AppPaths.DshHomeDirectory);
    }

    private static async Task<NodeInstallation> EnsureNodeAsync(IProgress<BootstrapUpdate> progress, CancellationToken cancellationToken)
    {
        progress.Report(new("正在检查 Node.js", "优先使用本机已安装的兼容版本。", "node --version && npm --version"));
        var systemNode = await TryFindSystemNodeAsync(cancellationToken);
        if (systemNode is not null)
        {
            progress.Report(new("正在使用本机 Node.js", $"检测到 Node.js {systemNode.Version}。", $"使用: {systemNode.NodeCommand}"));
            return systemNode;
        }

        if (File.Exists(NodeExe))
        {
            progress.Report(new("正在使用内置 Node.js", $"使用已下载的 Node.js {NodeVersion}。", NodeExe));
            return new NodeInstallation(NodeExe, NpmCmd, CorepackCmd, NodeFolder, NodeVersion, false);
        }

        var archiveName = $"node-v{NodeVersion}-win-x64.zip";
        var archivePath = Path.Combine(AppPaths.CacheDirectory, archiveName);
        Directory.CreateDirectory(AppPaths.CacheDirectory);
        progress.Report(new("正在下载 Node.js", "仅首次启动需要下载运行环境。"));

        await DownloadNodeArchiveAsync(archiveName, archivePath, progress, cancellationToken);

        progress.Report(new("正在安装 Node.js", "正在解压本地运行环境。", $"解压 {archiveName} 到 {AppPaths.RuntimeDirectory}"));
        Directory.CreateDirectory(AppPaths.RuntimeDirectory);
        ZipFile.ExtractToDirectory(archivePath, AppPaths.RuntimeDirectory, overwriteFiles: true);
        return new NodeInstallation(NodeExe, NpmCmd, CorepackCmd, NodeFolder, NodeVersion, false);
    }

    private async Task<bool> EnsureHarnessAsync(NodeInstallation node, IProgress<BootstrapUpdate> progress, CancellationToken cancellationToken)
    {
        if (File.Exists(DshCmd)) return false;
        Directory.CreateDirectory(AppPaths.HarnessDirectory);
        EnsurePnpmWorkspacePolicy();
        var installArguments = $"pnpm@11.7.0 add --node-linker=hoisted --reporter=append-only --registry=https://registry.npmmirror.com @deepseek-ai/dsh@{DshPackageVersion}";
        progress.Report(new("正在安装 DeepSeek Harness", "正在使用 pnpm 和阿里 npmmirror 下载插件与本地工具。",
            $"{node.CorepackCommand} {installArguments}"));
        await RunProcessAsync(node.CorepackCommand, installArguments,
            AppPaths.HarnessDirectory, node.Directory, progress, cancellationToken);
        return true;
    }

    private static async Task CheckForHarnessUpdateAsync(IProgress<BootstrapUpdate> progress,
        CancellationToken cancellationToken)
    {
        var state = UpdateState.Load();
        if (state.LastHarnessUpdateCheckUtc is { } lastCheck && DateTimeOffset.UtcNow - lastCheck < TimeSpan.FromDays(1))
            return;

        try
        {
            progress.Report(new("正在检查 DeepSeek Harness 更新", "通过阿里 npmmirror 检查新版本，不会阻塞启动。",
                "GET https://registry.npmmirror.com/@deepseek-ai%2fdsh/latest"));
            using var response = await Http.GetAsync("https://registry.npmmirror.com/@deepseek-ai%2fdsh/latest", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var latest = document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
            state.LastHarnessUpdateCheckUtc = DateTimeOffset.UtcNow;
            state.Save();
            if (!string.IsNullOrWhiteSpace(latest))
                progress.Report(new("DeepSeek Harness 更新检查完成", $"镜像最新版本：{latest}。将在后续版本加入无感后台升级。"));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            RuntimeLog.Write($"Harness update check failed; continuing with installed version: {exception}");
            progress.Report(new("DeepSeek Harness 更新检查失败", "将继续使用本机已安装版本。", exception.Message));
        }
    }

    private static async Task PrepareProfileModuleFallbackAsync(NodeInstallation node, IProgress<BootstrapUpdate> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new("正在准备 DeepSeek Harness", "正在建立首次启动所需的本地模块链接。",
            "初始化 $DSH_HOME/profiles/node_modules"));
        var info = new ProcessStartInfo(node.NodeCommand)
        {
            WorkingDirectory = AppPaths.HarnessDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        info.Environment["DSH_HOME"] = AppPaths.DshHomeDirectory;
        info.ArgumentList.Add("--input-type=module");
        info.ArgumentList.Add("--eval");
        info.ArgumentList.Add("import { healProfilesModuleFallback } from '@deepseek-ai/dsh-app-boot'; healProfilesModuleFallback(process.argv[1]);");
        info.ArgumentList.Add(DshPackageJson);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法准备 DeepSeek Harness 的本地模块链接。");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode == 0) return;
        var error = (await stderr).Trim();
        RuntimeLog.Write($"Unable to prepare Harness profile module fallback: {error}");
        throw new InvalidOperationException($"无法准备 DeepSeek Harness 的本地模块链接（退出码 {process.ExitCode}）。{error}");
    }

    private async Task<string> StartServiceAsync(NodeInstallation node, IProgress<BootstrapUpdate> progress,
        CancellationToken cancellationToken, int maximumAttempts)
    {
        if (_dshProcess is { HasExited: false }) throw new InvalidOperationException("DeepSeek Harness 已在运行，但本地地址不可用。请重试。");

        var logPath = Path.Combine(AppPaths.LogDirectory, "dsh.log");
        Directory.CreateDirectory(AppPaths.LogDirectory);
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            var port = GetAvailableLoopbackPort();
            var startInfo = new ProcessStartInfo(DshCmd, $"web --no-open --port {port}")
            {
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.Environment["PATH"] = $"{node.Directory};{startInfo.Environment["PATH"]}";
            startInfo.Environment["DSH_HOME"] = AppPaths.DshHomeDirectory;
            progress.Report(new("正在启动 DeepSeek Harness", attempt == 0 ? "正在等待本地网页服务就绪。" : $"正在重新启动本地服务（第 {attempt + 1}/{maximumAttempts} 次）。",
                $"{DshCmd} web --no-open --port {port}"));
            _dshProcess = Process.Start(startInfo) ?? throw new InvalidOperationException("无法创建 DeepSeek Harness 进程。");
            _dshJob = WindowsJob.CreateKillOnClose();
            _dshJob.Assign(_dshProcess);
            _ = PumpOutputAsync(_dshProcess.StandardOutput, logPath);
            _ = PumpOutputAsync(_dshProcess.StandardError, logPath);

            var address = $"http://127.0.0.1:{port}";
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_dshProcess.HasExited)
                {
                    var exitCode = _dshProcess.ExitCode;
                    DisposeExitedDshProcess();
                    if (attempt < maximumAttempts - 1)
                    {
                        progress.Report(new("正在初始化 DeepSeek Harness", $"首次启动正在完成本地模块链接（{attempt + 1}/{maximumAttempts}），修复后将自动重试。"));
                        await PrepareProfileModuleFallbackAsync(node, progress, cancellationToken);
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                        break;
                    }
                    throw new InvalidOperationException($"DeepSeek Harness 已退出（退出码 {exitCode}）。请打开日志目录查看详情。");
                }
                try
                {
                    using var response = await Http.GetAsync(address, cancellationToken);
                    if ((int)response.StatusCode < 500) return address;
                }
                catch (HttpRequestException) { }
                await Task.Delay(500, cancellationToken);
            }

            if (_dshProcess is null) continue;
            throw new TimeoutException("等待 DeepSeek Harness 启动超时。请检查网络或打开日志目录查看详情。");
        }
        throw new InvalidOperationException("DeepSeek Harness 初始化后仍无法启动。请打开日志目录查看详情。");
    }

    public void Stop()
    {
        // Closing the Job Object terminates every inherited child process, including dsh's Web server.
        _bootstrapJob?.Dispose();
        _bootstrapJob = null;
        _dshJob?.Dispose();
        _dshJob = null;
        StopProcess(ref _bootstrapProcess);
        StopProcess(ref _dshProcess);
    }

    public async Task<RuntimeVersionInfo> GetVersionInfoAsync(CancellationToken cancellationToken = default)
    {
        var harnessVersion = "未安装";
        try
        {
            if (File.Exists(DshPackageJson))
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(DshPackageJson, cancellationToken));
                if (document.RootElement.TryGetProperty("version", out var version) &&
                    !string.IsNullOrWhiteSpace(version.GetString()))
                    harnessVersion = version.GetString()!;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            RuntimeLog.Write($"Unable to read Harness version: {exception.Message}");
            harnessVersion = "未知";
        }

        var nodeVersion = _activeNodeVersion;
        if (string.IsNullOrWhiteSpace(nodeVersion))
        {
            var systemNode = await TryFindSystemNodeAsync(cancellationToken);
            nodeVersion = systemNode?.Version ?? (File.Exists(NodeExe) ? NodeVersion : "未安装");
        }

        return new RuntimeVersionInfo(harnessVersion, nodeVersion);
    }

    private static void StopProcess(ref Process? process)
    {
        if (process is not { HasExited: false }) return;
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); process = null; }
    }

    private void DisposeExitedDshProcess()
    {
        _dshJob?.Dispose();
        _dshJob = null;
        _dshProcess?.Dispose();
        _dshProcess = null;
    }

    private static int GetAvailableLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static async Task DownloadAsync(string url, string destination, IProgress<BootstrapUpdate> progress, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(destination);
        var buffer = new byte[1024 * 128];
        long received = 0;
        var lastReportedMegabytes = -1L;
        while (true)
        {
            int count;
            using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idleTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                count = await source.ReadAsync(buffer, idleTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("下载在 45 秒内未收到新数据，将切换下载源。");
            }
            if (count == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            received += count;
            var downloadedMegabytes = received / 1024 / 1024;
            if (downloadedMegabytes == lastReportedMegabytes && (length is null || received != length.Value))
                continue;
            lastReportedMegabytes = downloadedMegabytes;
            var detail = length is null ? $"已下载 {downloadedMegabytes} MB" : $"已下载 {downloadedMegabytes} / {length.Value / 1024 / 1024} MB";
            progress.Report(new("正在下载 Node.js", detail));
        }
    }

    private static void EnsurePnpmWorkspacePolicy()
    {
        Directory.CreateDirectory(AppPaths.HarnessDirectory);
        File.WriteAllText(Path.Combine(AppPaths.HarnessDirectory, "pnpm-workspace.yaml"), PnpmWorkspacePolicy);
    }

    private static async Task DownloadNodeArchiveAsync(string archiveName, string archivePath,
        IProgress<BootstrapUpdate> progress, CancellationToken cancellationToken)
    {
        var urls = new[]
        {
            $"https://npmmirror.com/mirrors/node/v{NodeVersion}/{archiveName}",
            $"https://nodejs.org/dist/v{NodeVersion}/{archiveName}",
        };

        Exception? lastError = null;
        foreach (var url in urls)
        {
            try
            {
                progress.Report(new("正在下载 Node.js", url.Contains("npmmirror.com") ? "正在使用阿里云 npmmirror 国内镜像。" : "国内镜像不可用，正在使用 Node.js 官方源。", $"GET {url}"));
                await DownloadAsync(url, archivePath, progress, cancellationToken);
                progress.Report(new("正在校验 Node.js", "正在使用官方 SHA-256 校验下载文件。", $"SHA-256 {archiveName}"));
                await using var stream = File.OpenRead(archivePath);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                if (!string.Equals(actual, NodeArchiveSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Node.js 下载文件的 SHA-256 校验失败。");
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException)
            {
                lastError = exception;
                RuntimeLog.Write($"Node.js download source failed ({url}): {exception.Message}");
                if (File.Exists(archivePath)) File.Delete(archivePath);
            }
        }
        throw new InvalidOperationException("无法从国内镜像或 Node.js 官方源下载 Node.js。", lastError);
    }

    private async Task RunProcessAsync(string fileName, string arguments, string workingDirectory, string? nodeDirectory,
        IProgress<BootstrapUpdate> progress, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(nodeDirectory))
            info.Environment["PATH"] = $"{nodeDirectory};{info.Environment["PATH"]}";
        info.Environment["COREPACK_NPM_REGISTRY"] = "https://registry.npmmirror.com";
        info.Environment["COREPACK_ENABLE_DOWNLOAD_PROMPT"] = "0";
        _bootstrapProcess = Process.Start(info) ?? throw new InvalidOperationException($"无法启动 {Path.GetFileName(fileName)}。");
        _bootstrapJob = WindowsJob.CreateKillOnClose();
        _bootstrapJob.Assign(_bootstrapProcess);
        try
        {
            var stdout = PumpInstallationOutputAsync(_bootstrapProcess.StandardOutput, progress, cancellationToken);
            var stderr = PumpInstallationOutputAsync(_bootstrapProcess.StandardError, progress, cancellationToken);
            await _bootstrapProcess.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);
            if (_bootstrapProcess.ExitCode != 0)
                throw new InvalidOperationException($"DeepSeek Harness 安装失败（退出码 {_bootstrapProcess.ExitCode}）。请打开日志目录查看详情。");
        }
        finally
        {
            _bootstrapProcess?.Dispose();
            _bootstrapProcess = null;
            _bootstrapJob?.Dispose();
            _bootstrapJob = null;
        }
    }

    private static async Task PumpOutputAsync(StreamReader reader, string logPath)
    {
        while (await reader.ReadLineAsync() is { } line)
            File.AppendAllText(logPath, $"[{DateTimeOffset.Now:O}] {line}{Environment.NewLine}");
    }

    private static async Task PumpInstallationOutputAsync(StreamReader reader, IProgress<BootstrapUpdate> progress,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            RuntimeLog.Write(line);
            if (!string.IsNullOrWhiteSpace(line))
            {
                var detail = line.Length > 160 ? line[..160] + "…" : line;
                progress.Report(new("正在安装 DeepSeek Harness", detail, line));
            }
        }
    }

    private static async Task<NodeInstallation?> TryFindSystemNodeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var nodeVersion = await GetProcessOutputAsync("node", "--version", cancellationToken);
            var npmVersion = await GetProcessOutputAsync("npm", "--version", cancellationToken);
            var corepackVersion = await GetProcessOutputAsync("corepack", "--version", cancellationToken);
            var version = nodeVersion.Trim().TrimStart('v');
            if (string.IsNullOrEmpty(npmVersion) || string.IsNullOrEmpty(corepackVersion) || !IsCompatibleNodeVersion(version)) return null;
            var nodeLocation = await GetProcessOutputAsync("where.exe", "node", cancellationToken);
            var nodeExe = nodeLocation.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(nodeExe)) return null;
            return new NodeInstallation(nodeExe, "npm", "corepack", Path.GetDirectoryName(nodeExe)!, version, true);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsCompatibleNodeVersion(string version)
    {
        var match = Regex.Match(version, "^(\\d+)\\.(\\d+)\\.(\\d+)");
        if (!match.Success) return false;
        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);
        return major > 22 || (major == 22 && minor >= 19);
    }

    private static async Task<string> GetProcessOutputAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"无法启动 {fileName}。");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) return string.Empty;
        return await output;
    }

    private sealed record NodeInstallation(string NodeCommand, string NpmCommand, string CorepackCommand, string Directory, string Version, bool IsSystem);
}

internal sealed record RuntimeVersionInfo(string HarnessVersion, string NodeVersion);

internal sealed class LauncherRestartRequiredException : Exception
{
    public LauncherRestartRequiredException(string message) : base(message)
    {
    }
}
