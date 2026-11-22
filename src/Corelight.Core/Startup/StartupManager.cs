using Corelight.Core.Platform;
using Microsoft.Win32;

namespace Corelight.Core.Startup;

/// <param name="Name">Имя значения в Run-ключе или имя файла в папке автозагрузки.</param>
public sealed record StartupItem(string Name, string Command, string Location, string ApprovedKey, bool IsEnabled, bool IsKnownUpdater)
{
    public string DisplayName { get; init; } = Name;
}

/// <summary>
/// Программы в автозагрузке. Включение и отключение идёт через ключи StartupApproved —
/// тот же механизм, что у вкладки «Автозагрузка» в Диспетчере задач, поэтому всё полностью обратимо
/// и сама запись программы не удаляется.
/// </summary>
public sealed class StartupManager(IRegistryAccess registry)
{
    private const string ApprovedRoot = @"\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    // Автозапуск этих программ обычно не нужен для работы: они догружают обновления или держат окно в фоне.
    private static readonly string[] KnownUpdaters =
    [
        "OneDrive", "MicrosoftEdgeAutoLaunch", "Teams", "com.squirrel.Teams", "MsTeams", "Skype", "Cortana",
        "AdobeGCInvoker", "Adobe Acrobat Synchronizer", "CCleaner", "GoogleUpdate", "Opera Browser Assistant",
    ];

    // Значок «Безопасность Windows» не показываем: по правилам проекта Defender и его части не трогаем.
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase) { "SecurityHealth" };

    private sealed record Source(string RunKey, string ApprovedKey, string Location);

    private static readonly Source[] RegistrySources =
    [
        new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "HKCU" + ApprovedRoot + "Run", "Реестр (пользователь)"),
        new(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "HKLM" + ApprovedRoot + "Run", "Реестр (все пользователи)"),
        new(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "HKLM" + ApprovedRoot + "Run32", "Реестр (все пользователи, 32-бит)"),
    ];

    public IReadOnlyList<StartupItem> GetItems()
    {
        var items = new List<StartupItem>();
        foreach (var source in RegistrySources)
        {
            foreach (var name in registry.GetValueNames(source.RunKey))
            {
                if (name.Length == 0 || Protected.Contains(name))
                    continue;
                var command = registry.GetValue(source.RunKey, name);
                if (command is { Kind: RegistryValueKind.String or RegistryValueKind.ExpandString }
                    && !string.IsNullOrWhiteSpace(command.Data))
                    items.Add(Create(name, command.Data, source.Location, source.ApprovedKey));
            }
        }

        AddFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "HKCU" + ApprovedRoot + "StartupFolder", "Папка «Автозагрузка» (пользователь)");
        AddFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "HKLM" + ApprovedRoot + "StartupFolder", "Папка «Автозагрузка» (все пользователи)");

        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void SetEnabled(StartupItem item, bool enabled)
    {
        // Формат как у Диспетчера задач: 4 байта флага (чётный — включено, нечётный — отключено) и 8 байт FILETIME.
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled)
            BitConverter.TryWriteBytes(data.AsSpan(4), DateTime.UtcNow.ToFileTimeUtc());
        registry.SetValue(item.ApprovedKey, item.Name, new RegValue(RegistryValueKind.Binary, Convert.ToHexString(data)));
    }

    private void AddFolder(List<StartupItem> items, string folder, string approvedKey, string location)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return;

        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                continue;
            items.Add(Create(fileName, file, location, approvedKey));
        }
    }

    private StartupItem Create(string name, string command, string location, string approvedKey)
    {
        // Значение не того типа или пустое Windows игнорирует — программа при этом запускается.
        var approved = registry.GetValue(approvedKey, name);
        var enabled = approved is not { Kind: RegistryValueKind.Binary, Data.Length: >= 2 }
            || (Convert.FromHexString(approved.Data[..2])[0] & 1) == 0;
        var known = KnownUpdaters.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
        return new StartupItem(name, command, location, approvedKey, enabled, known) { DisplayName = ToDisplayName(name) };
    }

    private static string ToDisplayName(string name)
    {
        if (name.StartsWith("MicrosoftEdgeAutoLaunch", StringComparison.OrdinalIgnoreCase))
            return "Microsoft Edge (фоновый автозапуск)";
        if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return name[..^4];
        return name;
    }
}
