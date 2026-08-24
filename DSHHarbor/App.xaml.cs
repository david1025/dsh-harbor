using System.Diagnostics;
using System.Windows;

namespace DSHHarbor;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var waitArgumentIndex = Array.IndexOf(e.Args, "--wait-for");
        if (waitArgumentIndex >= 0 && waitArgumentIndex + 1 < e.Args.Length &&
            int.TryParse(e.Args[waitArgumentIndex + 1], out var previousProcessId))
        {
            try
            {
                using var previousProcess = Process.GetProcessById(previousProcessId);
                if (!previousProcess.WaitForExit(30_000))
                    throw new TimeoutException("等待安装进程退出超时。");
            }
            catch (ArgumentException)
            {
                // It exited before the new process attached to it.
            }

            Thread.Sleep(1_000);
        }

        base.OnStartup(e);
    }
}
