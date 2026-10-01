using System.Text.Json;

namespace LocalBridge.WindowsShared;

public sealed record TransferHistoryEntry(
    DateTimeOffset Timestamp,
    string FileName,
    long Size,
    bool Success,
    string? Error,
    double? MegabytesPerSecond,
    string DeviceIdShort);

public static class TransferHistory
{
    private static readonly object Sync = new();

    public static void Append(TransferHistoryEntry entry)
    {
        lock (Sync)
        {
            AppPaths.EnsureDirectories();
            var line = JsonSerializer.Serialize(entry, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            File.AppendAllText(AppPaths.HistoryFile, line + Environment.NewLine);
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            if (File.Exists(AppPaths.HistoryFile)) File.Delete(AppPaths.HistoryFile);
        }
    }
}
