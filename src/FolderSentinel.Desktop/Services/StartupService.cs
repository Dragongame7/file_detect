using Microsoft.Win32;

namespace FolderSentinel.Desktop.Services;

public static class StartupService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FolderSentinel";
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) is string value && value.StartsWith('"' + Environment.ProcessPath + '"', StringComparison.OrdinalIgnoreCase);
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (!enabled) { key.DeleteValue(ValueName, false); return; }
        var command = $"\"{Environment.ProcessPath}\" --background";
        if (command.Length > 260) throw new InvalidOperationException("自動起動の登録先パスが長すぎます。短いパスへアプリを配置してください。");
        key.SetValue(ValueName, command);
    }
}
