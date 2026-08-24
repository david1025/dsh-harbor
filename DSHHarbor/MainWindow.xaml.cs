using System.Diagnostics;
using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using WinForms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;

namespace DSHHarbor;

public partial class MainWindow : Window
{
    private readonly DshRuntime _runtime = new();
    private const int MaximumDiagnosticLines = 300;
    private readonly Queue<string> _diagnosticLines = new();
    private readonly WinForms.NotifyIcon _trayIcon;
    private readonly WinForms.ToolStripMenuItem _toggleWindowMenuItem;
    private readonly WinForms.ToolStripMenuItem _checkForUpdatesMenuItem;
    private bool _allowExit;
    private bool _updateFlowRunning;

    public MainWindow()
    {
        InitializeComponent();
        _toggleWindowMenuItem = new WinForms.ToolStripMenuItem("隐藏主窗口", null, (_, _) => ToggleMainWindow());
        _checkForUpdatesMenuItem = new WinForms.ToolStripMenuItem("检查版本更新", null, async (_, _) => await CheckForUpdatesAsync());
        var aboutMenuItem = new WinForms.ToolStripMenuItem("关于", null, async (_, _) => await ShowAboutAsync());
        var exitMenuItem = new WinForms.ToolStripMenuItem("退出", null, (_, _) => ExitApplication());
        var trayMenu = new WinForms.ContextMenuStrip();
        trayMenu.Items.Add(_toggleWindowMenuItem);
        trayMenu.Items.Add(new WinForms.ToolStripSeparator());
        trayMenu.Items.Add(_checkForUpdatesMenuItem);
        trayMenu.Items.Add(aboutMenuItem);
        trayMenu.Items.Add(new WinForms.ToolStripSeparator());
        trayMenu.Items.Add(exitMenuItem);
        _trayIcon = new WinForms.NotifyIcon
        {
            Text = "DSH Harbor",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty) ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = trayMenu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
        Loaded += async (_, _) => await StartAsync();
        Closing += MainWindow_Closing;
        Closed += (_, _) => _trayIcon.Dispose();
        System.Windows.Application.Current.SessionEnding += (_, _) => _allowExit = true;
    }

    private async Task StartAsync()
    {
        // 上次已下载但被推迟的更新：进入程序直接开始安装。
        if (TryLaunchPendingUpdate())
            return;

        FailureActions.Visibility = Visibility.Collapsed;
        Progress.IsIndeterminate = true;
        StatusText.Text = "正在检查运行环境…";
        DetailText.Text = "首次启动会下载 Node.js 和 DeepSeek Harness。";

        var progress = new Progress<BootstrapUpdate>(update =>
        {
            StatusText.Text = update.Title;
            DetailText.Text = update.Detail;
            AppendDiagnostic(update.Command ?? update.Detail);
        });

        try
        {
            var url = await _runtime.StartAsync(progress);
            // WebView2 defaults to "<exe directory>\<exe name>.exe.WebView2", which is
            // not writable when installed under Program Files (E_ACCESSDENIED).
            // Pin it to our per-user data root instead.
            Directory.CreateDirectory(AppPaths.WebView2DataDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: AppPaths.WebView2DataDirectory);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.Source = new Uri(url);
            Browser.Visibility = Visibility.Visible;
            BootstrapView.Visibility = Visibility.Collapsed;

            // 运行环境就绪后在后台静默检查并下载应用更新，完成后弹框询问。
            _ = RunAutomaticUpdateCheckAsync();
        }
        catch (LauncherRestartRequiredException exception)
        {
            StatusText.Text = "安装完成，正在重新启动…";
            DetailText.Text = exception.Message;
            AppendDiagnostic("安装进程即将退出；新进程会等待它完全结束后再启动 Harness。");
            RuntimeLog.Write("Harness installation completed; restarting only after the installer launcher fully exits.");
            _runtime.Stop();
            var executable = Environment.ProcessPath ??
                             Process.GetCurrentProcess().MainModule?.FileName ??
                             throw new InvalidOperationException("无法确定 DSH Harbor 的程序路径。");
            var restartInfo = new ProcessStartInfo(executable) { UseShellExecute = true };
            restartInfo.ArgumentList.Add("--wait-for");
            restartInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(restartInfo);
            _allowExit = true;
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            Progress.IsIndeterminate = false;
            StatusText.Text = "无法启动 DeepSeek Harness";
            DetailText.Text = exception.Message;
            AppendDiagnostic($"错误: {exception}");
            FailureActions.Visibility = Visibility.Visible;
            RuntimeLog.Write(exception.ToString());
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        _runtime.Stop();
        await StartAsync();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.LogDirectory) { UseShellExecute = true });
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowExit)
        {
            _runtime.Stop();
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void ToggleMainWindow()
    {
        if (IsVisible) HideToTray();
        else ShowMainWindow();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        _toggleWindowMenuItem.Text = "显示主窗口";
    }

    private void ShowMainWindow()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _toggleWindowMenuItem.Text = "隐藏主窗口";
    }

    private void ExitApplication()
    {
        _allowExit = true;
        _trayIcon.Visible = false;
        _runtime.Stop();
        System.Windows.Application.Current.Shutdown();
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_updateFlowRunning)
            return;
        _updateFlowRunning = true;
        _checkForUpdatesMenuItem.Enabled = false;
        _checkForUpdatesMenuItem.Text = "正在检查更新…";
        try
        {
            // 上次已下载、被推迟的更新：直接开始安装。
            var pending = AppUpdateService.GetValidPendingUpdate();
            if (pending is not null)
            {
                LaunchUpdateInstaller(pending.InstallerPath, pending.Version);
                return;
            }

            var result = await AppUpdateService.CheckAsync();
            if (!result.IsUpdateAvailable)
            {
                WpfMessageBox.Show(
                    $"当前版本：{result.CurrentVersion}\n最新版本：{result.LatestVersion}\n\n当前已是最新版本。",
                    "检查更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (result.Asset is null)
            {
                // API 限流降级等场景拿不到安装包信息，回退为打开网页手动下载。
                var openChoice = WpfMessageBox.Show(
                    $"发现新版本 {result.LatestVersion}。\n当前版本：{result.CurrentVersion}\n\n暂时无法自动下载，是否打开 GitHub Releases 手动下载？",
                    "发现新版本",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (openChoice == MessageBoxResult.Yes) OpenUrl(result.ReleaseUrl);
                return;
            }

            var totalSuffix = result.Asset.Size >= 0 ? $" / {FormatMegabytes(result.Asset.Size)}" : string.Empty;
            var progress = new Progress<long>(bytes =>
                _checkForUpdatesMenuItem.Text = $"正在下载 {result.LatestVersion}… {FormatMegabytes(bytes)}{totalSuffix}");
            _checkForUpdatesMenuItem.Text = $"正在下载 {result.LatestVersion}…";
            var installerPath = await AppUpdateService.DownloadInstallerAsync(result.Asset, progress);
            OfferDownloadedUpdate(result, installerPath);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or TaskCanceledException or Win32Exception)
        {
            RuntimeLog.Write($"Application update check failed: {exception}");
            WpfMessageBox.Show(
                $"暂时无法完成更新。\n\n{exception.Message}",
                "检查更新失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _checkForUpdatesMenuItem.Text = "检查版本更新";
            _checkForUpdatesMenuItem.Enabled = true;
            _updateFlowRunning = false;
        }
    }

    /// <summary>后台静默检查并下载新版本，完成后弹框询问是否安装；失败只记日志。</summary>
    private async Task RunAutomaticUpdateCheckAsync()
    {
        if (_updateFlowRunning)
            return;
        _updateFlowRunning = true;
        try
        {
            var result = await AppUpdateService.CheckAsync();
            if (!result.IsUpdateAvailable || result.Asset is null)
                return;
            var installerPath = await AppUpdateService.DownloadInstallerAsync(result.Asset);
            OfferDownloadedUpdate(result, installerPath);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or TaskCanceledException or Win32Exception)
        {
            // 后台自动检查失败不打扰用户，只记日志。
            RuntimeLog.Write($"Automatic update check failed: {exception}");
        }
        finally
        {
            _updateFlowRunning = false;
        }
    }

    /// <summary>新版本下载完成后询问用户是否立即安装；拒绝则保留安装包，下次启动或检查更新时直接安装。</summary>
    private void OfferDownloadedUpdate(AppUpdateResult result, string installerPath)
    {
        // 先持久化，保证即使本次不安装，安装包与状态也不会丢。
        AppUpdateService.SavePendingUpdate(result.LatestVersion, installerPath);
        var choice = WpfMessageBox.Show(
            $"新版本 {result.LatestVersion} 已下载完成。\n当前版本：{result.CurrentVersion}\n\n是否立即安装？\n（选择“否”将保留安装包，下次启动程序或检查更新时直接开始安装。）",
            "更新已就绪",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (choice == MessageBoxResult.Yes)
            LaunchUpdateInstaller(installerPath, result.LatestVersion);
    }

    private bool TryLaunchPendingUpdate()
    {
        try
        {
            var pending = AppUpdateService.GetValidPendingUpdate();
            if (pending is null)
                return false;
            RuntimeLog.Write($"Pending update {pending.Version} found; launching installer {pending.InstallerPath}.");
            return LaunchUpdateInstaller(pending.InstallerPath, pending.Version);
        }
        catch (Exception exception) when (exception is IOException or JsonException or Win32Exception)
        {
            RuntimeLog.Write($"Unable to launch pending update: {exception}");
            return false;
        }
    }

    /// <summary>启动更新安装程序，成功启动后退出当前应用（安装器需要写入程序目录）。返回是否已成功启动。</summary>
    private bool LaunchUpdateInstaller(string installerPath, string version)
    {
        try
        {
            AppUpdateService.LaunchInstaller(installerPath);
        }
        catch (Win32Exception exception)
        {
            // 常见于用户在 UAC 提示中选择了“否”。保留待安装状态，下次再试。
            RuntimeLog.Write($"Update installer launch cancelled or failed for {version}: {exception.Message}");
            if (!File.Exists(installerPath))
                AppUpdateService.ClearPendingUpdate();
            return false;
        }

        RuntimeLog.Write($"Update installer for {version} launched; exiting application.");
        _allowExit = true;
        _trayIcon.Visible = false;
        _runtime.Stop();
        System.Windows.Application.Current.Shutdown();
        return true;
    }

    private static string FormatMegabytes(long bytes) => $"{bytes / 1024d / 1024d:F1} MB";

    private async Task ShowAboutAsync()
    {
        RuntimeVersionInfo versions;
        try
        {
            versions = await _runtime.GetVersionInfoAsync();
        }
        catch (Exception exception)
        {
            RuntimeLog.Write($"Unable to collect About dialog versions: {exception}");
            versions = new RuntimeVersionInfo("未知", "未知");
        }
        var webViewVersion = "未初始化";
        try
        {
            webViewVersion = Browser.CoreWebView2?.Environment.BrowserVersionString ??
                             CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception exception)
        {
            RuntimeLog.Write($"Unable to read WebView2 version: {exception.Message}");
            webViewVersion = "未安装";
        }

        WpfMessageBox.Show(
            $"DSH Harbor：{AppUpdateService.CurrentVersion}\n" +
            $"DeepSeek Harness：{versions.HarnessVersion}\n" +
            $"Node.js：{versions.NodeVersion}\n" +
            $".NET Runtime：{Environment.Version}\n" +
            $"WebView2 Runtime：{webViewVersion}\n\n" +
            "DSH Harbor 是 DeepSeek Harness 的社区维护 Windows 桌面启动器。",
            "关于 DSH Harbor",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void AppendDiagnostic(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        _diagnosticLines.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (_diagnosticLines.Count > MaximumDiagnosticLines) _diagnosticLines.Dequeue();
        DiagnosticOutput.Text = string.Join(Environment.NewLine, _diagnosticLines);
        DiagnosticOutput.ScrollToEnd();
    }
}
