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
public sealed record StartupScan(IReadOnlyList<StartupItem> Items, IReadOnlyList<string> Problems);

public sealed class StartupManager
{
    private readonly IRegistryAccess _registry;
    private readonly Func<Environment.SpecialFolder, string> _getFolder;
    private const string EdgeAutoLaunch = "MicrosoftEdgeAutoLaunch";
    private const string ApprovedRoot = @"\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    // Автозапуск этих программ обычно не нужен для работы: они догружают обновления или держат окно в фоне.
    private static readonly string[] KnownUpdaters =
    [
        "OneDrive", "Teams", "com.squirrel.Teams", "MsTeams", "Skype", "Cortana",
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

    private sealed record FolderSource(Environment.SpecialFolder Folder, string ApprovedKey, string Location);

    private static readonly FolderSource[] FolderSources =
    [
        new(Environment.SpecialFolder.Startup, "HKCU" + ApprovedRoot + "StartupFolder", "Папка «Автозагрузка» (пользователь)"),
        new(Environment.SpecialFolder.CommonStartup, "HKLM" + ApprovedRoot + "StartupFolder", "Папка «Автозагрузка» (все пользователи)"),
    ];

    public StartupManager(IRegistryAccess registry) : this(registry, Environment.GetFolderPath) { }

    internal StartupManager(IRegistryAccess registry, Func<Environment.SpecialFolder, string> getFolder)
        => (_registry, _getFolder) = (registry, getFolder);

    public StartupScan GetItems()
    {
        var items = new List<StartupItem>();
        var problems = new List<string>();
        foreach (var source in RegistrySources)
        {
            try
            {
                foreach (var name in _registry.GetValueNames(source.RunKey))
                {
                    if (name.Length == 0 || Protected.Contains(name)) continue;
                    try
                    {
                        if (ReadCommand(source, name) is { } command)
                            items.Add(Create(name, command, source.Location, source.ApprovedKey));
                    }
                    catch (Exception ex) when (IsReadError(ex))
                    {
                        problems.Add($"{source.Location}, {name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (IsReadError(ex))
            {
                problems.Add($"{source.Location}: {ex.Message}");
            }
        }

        foreach (var source in FolderSources)
        {
            try
            {
                var folder = _getFolder(source.Folder);
                if (string.IsNullOrEmpty(folder)) continue;
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var name = Path.GetFileName(file);
                    if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    try { items.Add(Create(name, file, source.Location, source.ApprovedKey)); }
                    catch (Exception ex) when (IsReadError(ex)) { problems.Add($"{source.Location}, {name}: {ex.Message}"); }
                }
            }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) when (IsReadError(ex)) { problems.Add($"{source.Location}: {ex.Message}"); }
        }
        return new(items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), problems);
    }

    public bool SetEnabled(StartupItem item, bool enabled)
    {
        var command = RegistrySources.FirstOrDefault(source => source.ApprovedKey == item.ApprovedKey) is { } registrySource
            ? ReadCommand(registrySource, item.Name)
            : FolderSources.FirstOrDefault(source => source.ApprovedKey == item.ApprovedKey) is { } folderSource
                ? ReadFolderCommand(folderSource, item.Name) : null;
        if (command is null || !string.Equals(command, item.Command, StringComparison.Ordinal))
            throw new InvalidOperationException("Запись автозагрузки изменилась. Откройте раздел заново.");

        // Первые четыре байта содержат флаг, следующие восемь — время отключения FILETIME.
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled)
            BitConverter.TryWriteBytes(data.AsSpan(4), DateTime.UtcNow.ToFileTimeUtc());
        _registry.SetValue(item.ApprovedKey, item.Name, new RegValue(RegistryValueKind.Binary, Convert.ToHexString(data)));
        return ReadEnabled(item.ApprovedKey, item.Name);
    }

    private string? ReadCommand(Source source, string name) =>
        !Protected.Contains(name) && _registry.GetValue(source.RunKey, name) is
            { Kind: RegistryValueKind.String or RegistryValueKind.ExpandString } value && !string.IsNullOrWhiteSpace(value.Data)
                ? value.Data : null;

    private string? ReadFolderCommand(FolderSource source, string name)
    {
        if (name != Path.GetFileName(name) || name.Length == 0) return null;
        var folder = _getFolder(source.Folder);
        if (string.IsNullOrEmpty(folder)) return null;
        var path = Path.Combine(folder, name);
        return File.Exists(path) ? path : null;
    }

    private bool ReadEnabled(string key, string name)
    {
        var approved = _registry.GetValue(key, name);
        return approved is not { Kind: RegistryValueKind.Binary, Data.Length: >= 2 }
            || (Convert.FromHexString(approved.Data[..2])[0] & 1) == 0;
    }

    private StartupItem Create(string name, string command, string location, string approvedKey)
    {
        var known = KnownUpdaters.Contains(name, StringComparer.OrdinalIgnoreCase) || IsEdge(name);
        return new StartupItem(name, command, location, approvedKey, ReadEnabled(approvedKey, name), known) { DisplayName = ToDisplayName(name) };
    }

    private static bool IsReadError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or FormatException or NotSupportedException;

    private static bool IsEdge(string name) => name.Equals(EdgeAutoLaunch, StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(EdgeAutoLaunch + "_", StringComparison.OrdinalIgnoreCase);

    private static string ToDisplayName(string name)
    {
        if (IsEdge(name))
            return "Microsoft Edge (фоновый автозапуск)";
        if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return name[..^4];
        return name;
    }
}
