using Microsoft.Win32;

namespace NativeWidget.Services;

public static class AutoStartService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NativeWidget";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(ValueName) != null;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
                         ?? Registry.CurrentUser.CreateSubKey(KeyPath);
        if (enabled)
        {
            // Must be quoted: Windows splits the Run value on the first space when
            // launching it, which silently breaks any install path containing spaces
            // (this one lives under "Agent antigrav\desktop shit\...").
            var path = Environment.ProcessPath ?? "";
            key.SetValue(ValueName, $"\"{path}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
