namespace DSHHarbor;

internal static class RuntimeLog
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        lock (Gate)
        {
            File.AppendAllText(Path.Combine(AppPaths.LogDirectory, "launcher.log"),
                $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
    }
}
