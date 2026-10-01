namespace LocalBridge.WindowsShared;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalBridge");

    public static string LogsDirectory => Path.Combine(DataDirectory, "Logs");
    public static string TempDirectory => Path.Combine(DataDirectory, "Temp");
    public static string IdentityFile => Path.Combine(DataDirectory, "identity.json");
    public static string PairingFile => Path.Combine(DataDirectory, "pairing.json");
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string HistoryFile => Path.Combine(DataDirectory, "history.jsonl");
    public static string AgentExecutable => Path.Combine(AppContext.BaseDirectory, "LocalBridge.Agent.exe");
    public static string SettingsExecutable => Path.Combine(AppContext.BaseDirectory, "LocalBridge.Settings.exe");
    public const string PipeName = "LocalBridgeSender";

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
    }
}

