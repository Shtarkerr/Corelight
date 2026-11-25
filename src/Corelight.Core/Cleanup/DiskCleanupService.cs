using Microsoft.Extensions.Logging;
using Corelight.Core.Platform;

namespace Corelight.Core.Cleanup;

/// <summary>Очистка удаляет только выбранные элементы последнего анализа.</summary>
public sealed class DiskCleanupService
{
    private readonly Func<IReadOnlyList<CleanupRule>> _getRules;
    private readonly ICleanupShell _shell;
    private readonly ILogger<DiskCleanupService> _log;
    private readonly Func<string?>? _getDownloadsPath;
    private readonly Func<IReadOnlyList<CleanupDirectoryScope>>? _getDirectoryScopes;
    private readonly Func<IEnumerable<string>> _getSyncRoots;
    private readonly SemaphoreSlim _gate = new(1);
    private CleanupScan? _lastScan;
    public IReadOnlyList<CleanupCategory> Categories => CleanupCatalog.Categories;

    public DiskCleanupService(ILogger<DiskCleanupService> log)
        : this(CleanupCatalog.Rules, new WindowsCleanupShell(), log, WindowsCleanupShell.GetDownloadsPath,
            CleanupCatalog.GetDirectoryScopes, () => WindowsOneDriveControl.GetSyncRoots(new WindowsRegistryAccess(),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))) { }
    internal DiskCleanupService(IReadOnlyList<CleanupRule> rules, ICleanupShell shell, ILogger<DiskCleanupService> log,
        Func<string?>? getDownloadsPath = null, Func<IReadOnlyList<CleanupDirectoryScope>>? getDirectoryScopes = null,
        Func<IEnumerable<string>>? getSyncRoots = null)
        : this(() => rules, shell, log, getDownloadsPath, getDirectoryScopes, getSyncRoots ?? (() => [])) { }

    private DiskCleanupService(Func<IReadOnlyList<CleanupRule>> getRules, ICleanupShell shell, ILogger<DiskCleanupService> log,
        Func<string?>? getDownloadsPath, Func<IReadOnlyList<CleanupDirectoryScope>>? getDirectoryScopes, Func<IEnumerable<string>> getSyncRoots)
    {
        _getRules = getRules;
        _shell = shell;
        _log = log;
        _getDownloadsPath = getDownloadsPath;
        _getDirectoryScopes = getDirectoryScopes;
        _getSyncRoots = getSyncRoots;
    }

    public async Task<CleanupScan> AnalyzeAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _lastScan = null;
            return _lastScan = await Task.Run(() => Analyze(progress, ct), ct);
        }
        finally { _gate.Release(); }
    }

    private CleanupScan Analyze(IProgress<string>? progress, CancellationToken ct)
    {
        var items = new List<CleanupItem>(); var issues = new List<CleanupIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var currentRules = _getRules();
        IEnumerable<CleanupRule> rules = currentRules;
        try
        {
            if (GetDownloadsRule(currentRules) is { } downloads) rules = rules.Append(downloads);
        }
        catch (Exception ex) when (IsFileError(ex) || ex is System.Runtime.InteropServices.COMException)
        {
            AddIssue(issues, "Загрузки", ex.Message);
        }
        foreach (var rule in rules)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(Categories.First(c => c.Id == rule.CategoryId).Title);
            try
            {
                if (!Path.IsPathFullyQualified(rule.Root) || !Directory.Exists(rule.Root)) continue;
                CleanupFiles.EnsurePlainDirectory(rule.Root);
                var pending = new Stack<string>(); pending.Push(rule.Root);
                while (pending.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var directory = pending.Pop();
                    try
                    {
                        CleanupFiles.EnsurePlainDirectory(directory);
                        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                        {
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                var attributes = File.GetAttributes(path);
                                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) { AddIssue(issues, path, "Ссылка или облачный файл пропущен."); continue; }
                                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                                if (!MatchesName(rule, path)) continue;
                                var stamp = CleanupFiles.Inspect(path, rule.Root);
                                if (DateTime.FromFileTimeUtc(stamp.Modified) > now - rule.MinimumAge || DateTime.FromFileTimeUtc(stamp.Creation) > now - rule.MinimumAge) continue;
                                var target = rule.Filter == CleanupFilter.BrokenShortcuts ? _shell.BrokenShortcutTarget(path) : null;
                                if (rule.Filter == CleanupFilter.BrokenShortcuts && target is null) continue;
                                if (seen.Add(path)) items.Add(new(rule.CategoryId, path, stamp.Length) { Stamp = stamp, Rule = rule, ShortcutTarget = target });
                            }
                            catch (Exception ex) when (IsFileError(ex)) { AddIssue(issues, path, ex.Message); }
                        }
                    }
                    catch (Exception ex) when (IsFileError(ex)) { AddIssue(issues, directory, ex.Message); }
                }
            }
            catch (Exception ex) when (IsFileError(ex)) { AddIssue(issues, rule.Root, ex.Message); }
        }
        AnalyzeEmptyDirectories(items, issues, ct);
        RecycleBinState? recycle = null;
        try { recycle = _shell.QueryRecycleBin(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { AddIssue(issues, "Корзина", ex.Message); }
        IReadOnlyList<RecycleBinItem> recycleBinItems = [];
        if (recycle?.Items > 0)
        {
            try { recycleBinItems = _shell.ListRecycleBin(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddIssue(issues, "Корзина", ex.Message);
                _log.LogWarning(ex, "Не удалось прочитать содержимое корзины для предварительного просмотра");
            }
        }
        _log.LogInformation("Анализ очистки: {Items} элементов, {Bytes} байт, {Issues} замечаний", items.Count, items.Sum(item => item.Bytes), issues.Count);
        return new(items.AsReadOnly(), issues.AsReadOnly(), recycle, recycleBinItems, now);
    }

    private void AnalyzeEmptyDirectories(List<CleanupItem> items, List<CleanupIssue> issues, CancellationToken ct)
    {
        foreach (var scope in GetDirectoryScopes(issues))
        {
            ct.ThrowIfCancellationRequested();
            var rule = new CleanupRule(CleanupCatalog.ShortcutsId, scope.Root, TimeSpan.Zero);
            try
            {
                if (!Directory.Exists(scope.Root)) continue;
                CleanupFiles.EnsurePlainDirectory(scope.Root);
                foreach (var path in Directory.EnumerateDirectories(scope.Root))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (!scope.Includes(path) || CleanupFiles.InspectEmptyDirectory(path, scope.Root) is not { } stamp) continue;
                        items.Add(new(rule.CategoryId, path, 0) { Stamp = stamp, Rule = rule, IsDirectory = true });
                    }
                    catch (Exception ex) when (IsFileError(ex)) { AddIssue(issues, path, ex.Message); }
                }
            }
            catch (Exception ex) when (IsFileError(ex)) { AddIssue(issues, scope.Root, ex.Message); }
        }
    }

    private IReadOnlyList<CleanupDirectoryScope> GetDirectoryScopes(List<CleanupIssue> issues)
    {
        try { return _getDirectoryScopes?.Invoke() ?? []; }
        catch (Exception ex) when (IsFileError(ex) || ex is System.Runtime.InteropServices.COMException)
        {
            AddIssue(issues, "Пустые папки", ex.Message);
            return [];
        }
    }

    public async Task<CleanupResult> CleanAsync(CleanupScan scan, IReadOnlyCollection<string> selectedCategories,
        IProgress<string>? progress = null, CancellationToken ct = default, IReadOnlySet<string>? excludedPaths = null,
        IReadOnlySet<string>? selectedDirectories = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!ReferenceEquals(scan, _lastScan)) throw new InvalidOperationException("Нужен новый анализ перед очисткой.");
            if (DateTime.UtcNow - scan.CreatedUtc > TimeSpan.FromMinutes(30)) throw new InvalidOperationException("Результаты устарели. Повторите анализ.");
            var selected = selectedCategories.ToHashSet(StringComparer.Ordinal);
            if (selected.Except(Categories.Select(c => c.Id)).Any()) throw new ArgumentException("Неизвестная категория очистки.");
            if (selected.Contains("recycle-bin") && scan.RecycleBin is null) throw new InvalidOperationException("Корзина не проверена. Снимите её выбор или повторите анализ.");
            _lastScan = null; // Один подтверждённый анализ разрешает только одну попытку очистки.
            return await Task.Run(() => Clean(scan, selected, excludedPaths, selectedDirectories, progress, ct), CancellationToken.None);
        }
        finally { _gate.Release(); }
    }

    private CleanupRule? GetDownloadsRule(IReadOnlyList<CleanupRule> rules) =>
        CleanupCatalog.DownloadsRule(_getDownloadsPath?.Invoke(), rules, _getSyncRoots());

    private CleanupResult Clean(CleanupScan scan, HashSet<string> selected, IReadOnlySet<string>? excluded,
        IReadOnlySet<string>? selectedDirectories, IProgress<string>? progress, CancellationToken ct)
    {
        int deleted = 0, skipped = 0, deletedDirectories = 0, skippedDirectories = 0; long bytes = 0, released = 0;
        var issues = new List<CleanupIssue>(); var emptied = false; long recycleBytes = 0;
        var currentRules = _getRules();
        if (selected.Contains(CleanupCatalog.DownloadsId) && scan.Items.FirstOrDefault(f =>
                f.CategoryId == CleanupCatalog.DownloadsId && excluded?.Contains(f.Path) != true) is { } download &&
            !string.Equals(GetDownloadsRule(currentRules)?.Root, download.Rule.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Расположение папки «Загрузки» изменилось. Повторите анализ.");
        var shortcutRoots = currentRules.Where(rule => rule.Filter == CleanupFilter.BrokenShortcuts)
            .Select(rule => rule.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scopes = selected.Contains(CleanupCatalog.ShortcutsId) && selectedDirectories?.Count > 0 ? GetDirectoryScopes(issues) : [];
        foreach (var item in scan.Items.Where(item => selected.Contains(item.CategoryId) && excluded?.Contains(item.Path) != true
            && (!item.IsDirectory || selectedDirectories?.Contains(item.Path) == true)))
        {
            if (ct.IsCancellationRequested) break;
            if ((deleted + skipped + deletedDirectories + skippedDirectories) % 64 == 0)
                progress?.Report($"Удалено: файлов {deleted}, папок {deletedDirectories}");
            try
            {
                if (item.IsDirectory && !scopes.Any(scope => scope.Includes(item.Path)))
                    throw new IOException("Расположение папки изменилось или теперь защищено. Повторите анализ.");
                if (item.Rule.Filter == CleanupFilter.BrokenShortcuts && !shortcutRoots.Contains(item.Rule.Root))
                    throw new IOException("Расположение папки ярлыков изменилось. Повторите анализ.");
                released += CleanupFiles.Delete(item, () => item.ShortcutTarget is null || _shell.IsMissingTarget(item.ShortcutTarget));
                if (item.IsDirectory) deletedDirectories++;
                else { deleted++; bytes += item.Bytes; }
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                if (item.IsDirectory) skippedDirectories++;
                else skipped++;
                AddIssue(issues, item.Path, ex.Message);
                if (skipped + skippedDirectories <= 20) _log.LogWarning("Пропуск очистки {Path}: {Reason}", item.Path, ex.Message);
            }
        }
        if (selected.Contains("recycle-bin") && !ct.IsCancellationRequested)
        {
            try
            {
                progress?.Report("Очистка корзины…");
                var before = _shell.QueryRecycleBin();
                _shell.EmptyRecycleBin(); emptied = true;
                recycleBytes = Math.Max(0, before.Bytes - _shell.QueryRecycleBin().Bytes);
            }
            catch (Exception ex) { AddIssue(issues, "Корзина", ex.Message); skipped++; _log.LogWarning(ex, "Не удалось завершить очистку корзины"); }
        }
        _log.LogInformation("Очистка: удалено {Files} файлов и {Directories} папок, данных {Bytes}, выделено на диске {Released}; пропущено {Skipped}; корзина {Recycle}; отменено {Cancelled}",
            deleted, deletedDirectories, bytes, released, skipped + skippedDirectories, emptied, ct.IsCancellationRequested);
        return new(deleted, bytes, released, skipped, issues.AsReadOnly(), ct.IsCancellationRequested, emptied, recycleBytes)
        {
            DeletedDirectories = deletedDirectories,
            SkippedDirectories = skippedDirectories,
        };
    }

    private static bool MatchesName(CleanupRule rule, string path) => rule.Filter switch
    {
        CleanupFilter.Thumbnails => Path.GetFileName(path).StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(path).Equals(".db", StringComparison.OrdinalIgnoreCase),
        CleanupFilter.Dumps => Path.GetExtension(path).Equals(".dmp", StringComparison.OrdinalIgnoreCase),
        CleanupFilter.BrokenShortcuts => Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };
    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException;
    private static void AddIssue(List<CleanupIssue> issues, string path, string reason)
    { if (issues.Count < 1000) issues.Add(new(path, reason)); }
}
