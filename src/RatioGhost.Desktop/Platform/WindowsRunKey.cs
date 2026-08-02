using System.Runtime.Versioning;
using Microsoft.Win32;

namespace RatioGhost.Desktop.Platform;

[SupportedOSPlatform("windows")]
internal sealed class WindowsRunKey : IWindowsRunKey
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RatioGhost";

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        return key?.GetValue(ValueName) as string;
    }

    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
