using Microsoft.Win32;

namespace LocalBridge.WindowsShared;

public sealed record AppSettings(
    string MenuTitle,
    bool StartWithWindows,
    int RetrySeconds,
    bool CompletionNotifications)
{
    public static AppSettings Default { get; } = new("iPhoneに送る", true, 30, true);
}

public static class SettingsStore
{
    public static AppSettings Load()
    {
        var settings = AtomicJsonFile.Read<AppSettings>(AppPaths.SettingsFile) ?? AppSettings.Default;
        return settings with
        {
            MenuTitle = SanitizeTitle(settings.MenuTitle),
            RetrySeconds = Math.Clamp(settings.RetrySeconds, 5, 300),
        };
    }

    public static void Save(AppSettings settings)
    {
        settings = settings with
        {
            MenuTitle = SanitizeTitle(settings.MenuTitle),
            RetrySeconds = Math.Clamp(settings.RetrySeconds, 5, 300),
        };
        AtomicJsonFile.Write(AppPaths.SettingsFile, settings);
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\LocalBridge", writable: true);
        key.SetValue("MenuTitle", settings.MenuTitle, RegistryValueKind.String);
        key.SetValue("InstallPath", AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), RegistryValueKind.String);
        StartupRegistration.SetEnabled(settings.StartWithWindows);
    }

    private static string SanitizeTitle(string value)
    {
        var cleaned = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return string.IsNullOrEmpty(cleaned) ? AppSettings.Default.MenuTitle : cleaned[..Math.Min(cleaned.Length, 80)];
    }
}

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LocalBridgeAgent";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{AppPaths.AgentExecutable}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
