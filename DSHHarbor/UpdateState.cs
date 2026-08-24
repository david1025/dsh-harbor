using System.Text.Json;

namespace DSHHarbor;

internal sealed class UpdateState
{
    public DateTimeOffset? LastHarnessUpdateCheckUtc { get; set; }

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
