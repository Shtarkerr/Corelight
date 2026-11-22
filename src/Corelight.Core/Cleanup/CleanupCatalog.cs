namespace Corelight.Core.Cleanup;

internal static class CleanupCatalog
{
    internal const string DownloadsId = "downloads";
    internal const string ShortcutsId = "shortcuts";

    internal static readonly IReadOnlyList<CleanupCategory> Categories = Array.AsReadOnly(new CleanupCategory[]
    {
        new("user-temp", "Временные файлы пользователя", "Старше 7 дней. Закройте программы; нужные файлы из временной папки заранее сохраните.", true),
        new("windows-temp", "Временные файлы Windows", "Старше 7 дней. Занятые и защищённые файлы будут пропущены.", true),
        new("thumbnails", "Кэш эскизов", "Миниатюры изображений в Проводнике. После очистки создадутся заново.", false),
        new("shaders", "Кэш шейдеров DirectX", "После очистки первые запуски игр могут быть медленнее из-за пересоздания кэша.", false),
        new("error-reports", "Отчёты об ошибках Windows", "Старше 7 дней. Удалятся сохранённые отчёты, которые могут понадобиться для диагностики.", false),
        new("crash-dumps", "Дампы сбоев", "Старше 7 дней. Эти файлы помогают выяснять причины сбоев программ и Windows.", false),
        new(ShortcutsId, "Неработающие ярлыки и пустые папки", "Ярлыки на рабочем столе и в меню «Пуск». Пустые папки на рабочем столе и в «Документах» выберите вручную.", false),
        new(DownloadsId, "Загрузки", "Файлы из «Загрузок» и вложенных папок. Снимите галочки с нужных файлов.", false),
        new("recycle-bin", "Корзина", "Полное удаление содержимого корзины текущей учётной записи на всех дисках, включая новые элементы.", false),
    });

    internal static IReadOnlyList<CleanupRule> Rules()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var week = TimeSpan.FromDays(7); var day = TimeSpan.FromDays(1);
        var rules = new List<CleanupRule>
        {
            new("user-temp", Path.Combine(local, "Temp"), week),
            new("windows-temp", Path.Combine(windows, "Temp"), week),
            new("thumbnails", Path.Combine(local, "Microsoft", "Windows", "Explorer"), day, CleanupFilter.Thumbnails),
            new("shaders", Path.Combine(local, "D3DSCache"), day),
            new("crash-dumps", Path.Combine(local, "CrashDumps"), week, CleanupFilter.Dumps),
            new("crash-dumps", Path.Combine(windows, "Minidump"), week, CleanupFilter.Dumps),
        };
        foreach (var basePath in new[] { local, common })
            foreach (var folder in new[] { "ReportArchive", "ReportQueue" })
                rules.Add(new("error-reports", Path.Combine(basePath, "Microsoft", "Windows", "WER", folder), week));
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(path)) rules.Add(new(ShortcutsId, path, day, CleanupFilter.BrokenShortcuts));
        }
        return rules.AsReadOnly();
    }

    internal static IReadOnlyList<CleanupDirectoryScope> GetDirectoryScopes()
    {
        const Environment.SpecialFolderOption option = Environment.SpecialFolderOption.DoNotVerify;
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory, option);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, option);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, option);
        var protectedPaths = Enum.GetValues<Environment.SpecialFolder>()
            .Where(folder => folder is not (Environment.SpecialFolder.Desktop or Environment.SpecialFolder.DesktopDirectory
                or Environment.SpecialFolder.MyDocuments or Environment.SpecialFolder.UserProfile))
            .Select(folder => Environment.GetFolderPath(folder, option))
            .Concat(new[] { WindowsCleanupShell.GetDownloadsPath() })
            .Concat(new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }.Select(Environment.GetEnvironmentVariable));
        return DirectoryScopes(profile, desktop, documents, protectedPaths);
    }

    internal static IReadOnlyList<CleanupDirectoryScope> DirectoryScopes(string profile, string desktop, string documents,
        IEnumerable<string?> protectedPaths)
    {
        if (!Path.IsPathFullyQualified(profile) || profile.StartsWith(@"\\", StringComparison.Ordinal)) return [];

        var paths = protectedPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!).ToArray();
        var scopes = new List<CleanupDirectoryScope>(2);
        if (DirectoryScope(desktop, Path.Combine(profile, "Desktop"), paths.Append(documents)) is { } desktopScope)
            scopes.Add(desktopScope);
        var standardDocuments = Path.Combine(profile, "Documents");
        string[] appFolders = ["My Games", "PowerShell", "WindowsPowerShell"];
        var protectedDocuments = paths.Append(desktop).Concat(appFolders.Select(name => Path.Combine(standardDocuments, name)));
        if (DirectoryScope(documents, standardDocuments, protectedDocuments) is { } documentsScope)
            scopes.Add(documentsScope);
        return scopes;
    }

    private static CleanupDirectoryScope? DirectoryScope(string location, string expected, IEnumerable<string> protectedPaths)
    {
        if (!Path.IsPathFullyQualified(location) || location.StartsWith(@"\\", StringComparison.Ordinal))
            return null;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
        if (!string.Equals(root, Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase)
            || new DriveInfo(Path.GetPathRoot(root)!).DriveType != DriveType.Fixed)
            return null;

        var paths = protectedPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Any(path => IsSameOrWithin(root, path))) return null;
        return new(root, paths.Where(path => CleanupFiles.IsWithin(path, root)).ToArray());
    }

    internal static CleanupRule? DownloadsRule(string? path, IReadOnlyList<CleanupRule> existingRules)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new IOException("Папка «Загрузки» должна находиться на локальном диске.");

        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var driveRoot = Path.GetPathRoot(folder)!;
        if (folder.Length == driveRoot.Length || folder[driveRoot.Length..].Contains(':') || new DriveInfo(driveRoot).DriveType != DriveType.Fixed)
            throw new IOException("Для очистки «Загрузок» нужна отдельная папка на локальном диске.");

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Environment.SpecialFolder[] protectedFolders =
        [
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
        ];
        if ((!string.IsNullOrEmpty(profile) && IsSameOrWithin(profile, folder)) ||
            protectedFolders.Select(Environment.GetFolderPath).Concat(existingRules.Select(rule => rule.Root))
                .Any(protectedPath => !string.IsNullOrEmpty(protectedPath) && (IsSameOrWithin(folder, protectedPath) || IsSameOrWithin(protectedPath, folder))))
            throw new IOException("Папка «Загрузки» пересекается с профилем, системной папкой или другой категорией очистки.");

        return new(DownloadsId, folder, TimeSpan.Zero);
    }

    internal static bool IsSameOrWithin(string path, string root) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase)
        || CleanupFiles.IsWithin(path, root);
}
