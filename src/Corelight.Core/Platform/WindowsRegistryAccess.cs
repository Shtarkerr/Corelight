using Microsoft.Win32;

namespace Corelight.Core.Platform;

public sealed class WindowsRegistryAccess : IRegistryAccess
{
    public RegValue? GetValue(string keyPath, string name)
    {
        using var key = OpenKey(keyPath, writable: false);
        if (key is null)
            return null;

        var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null)
            return null;

        var kind = key.GetValueKind(name);
        return kind is RegistryValueKind.Unknown or RegistryValueKind.None
            ? null
            : RegValue.FromObject(kind, raw);
    }

    public void SetValue(string keyPath, string name, RegValue value)
    {
        var (hive, subKey) = Split(keyPath);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.CreateSubKey(subKey, writable: true)
            ?? throw new InvalidOperationException($"Не удалось открыть ключ {keyPath}.");
        key.SetValue(name, value.ToObject(), value.Kind);
    }

    public void DeleteValue(string keyPath, string name)
    {
        using var key = OpenKey(keyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public IReadOnlyList<string> GetValueNames(string keyPath)
    {
        using var key = OpenKey(keyPath, writable: false);
        return key?.GetValueNames() ?? [];
    }

    public IReadOnlyList<string> GetSubKeyNames(string keyPath)
    {
        using var key = OpenKey(keyPath, writable: false);
        return key?.GetSubKeyNames() ?? [];
    }

    private static RegistryKey? OpenKey(string keyPath, bool writable)
    {
        var (hive, subKey) = Split(keyPath);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        return root.OpenSubKey(subKey, writable);
    }

    internal static (RegistryHive Hive, string SubKey) Split(string keyPath)
    {
        var slash = keyPath.IndexOf('\\');
        if (slash <= 0)
            throw new ArgumentException($"Некорректный путь реестра: {keyPath}", nameof(keyPath));

        var hive = keyPath[..slash].ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            _ => throw new ArgumentException($"Поддерживаются только HKLM и HKCU: {keyPath}", nameof(keyPath)),
        };
        return (hive, keyPath[(slash + 1)..]);
    }
}
