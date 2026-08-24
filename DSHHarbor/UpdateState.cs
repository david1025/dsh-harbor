using System.Text.Json;

namespace DSHHarbor;

internal sealed class UpdateState
{
    public DateTimeOffset? LastHarnessUpdateCheckUtc { get; set; }

    /// <summary>已下载、等待安装的应用更新版本号。</summary>
    public string? PendingAppUpdateVersion { get; set; }

    /// <summary>已下载的更新安装包完整路径。</summary>
    public string? PendingAppUpdateInstallerPath { get; set; }

    /// <summary>安装包下载完成的时间。</summary>
    public DateTimeOffset? PendingAppUpdateDownloadedUtc { get; set; }

    public static UpdateState Load()
    {
        try
        {
            return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(AppPaths.UpdateStateFile)) ?? new UpdateState();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            RuntimeLog.Write($"Unable to load update state: {exception.Message}");
            return new UpdateState();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(AppPaths.UpdateStateFile, JsonSerializer.Serialize(this));
    }
}
