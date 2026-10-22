using Microsoft.Win32;
using System.IO;

namespace PersonalAgent.App;

// Reads are inert. Only the explicit Settings save calls SetEnabled.
public sealed class WindowsLoginStartup(string? executablePath,
    string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
{
    private const string ValueName = "PersonalAgent.App";
    public bool CanEnable => OwnedPath(executablePath) && File.Exists(executablePath);

    public bool ReadEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return ReadOwned(key) is not null;
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled && !CanEnable) throw new InvalidOperationException("Use the installed PersonalAgent.App.exe to enable login startup.");
        using var existing = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        _ = ReadOwned(existing); // Never overwrite/delete an unexpected foreign value.
        if (!enabled) { existing?.DeleteValue(ValueName, throwOnMissingValue: false); return; }
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(ValueName, "\"" + Path.GetFullPath(executablePath!) + "\"", RegistryValueKind.String);
    }

    private static string? ReadOwned(RegistryKey? key)
    {
        var value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return null;
        if (value is not string command || command.Length < 3 || command[0] != '"' || command[^1] != '"'
            || !OwnedPath(command[1..^1])) throw new InvalidOperationException("Unexpected login startup registration; review it locally.");
        return command;
    }

    private static bool OwnedPath(string? path) => !string.IsNullOrWhiteSpace(path) && !path.Contains('"') && !path.Any(char.IsControl)
        && Path.IsPathFullyQualified(path) && string.Equals(Path.GetFileName(path), "PersonalAgent.App.exe", StringComparison.OrdinalIgnoreCase);
}
