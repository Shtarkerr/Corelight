using Microsoft.Extensions.Logging;

namespace Corelight.Core.Cleanup;

/// <summary>Очистка отделена от обратимых твиков: удаляются только файлы последнего анализа после выбора пользователя.</summary>
public sealed class DiskCleanupService
{
    private readonly IReadOnlyList<CleanupRule> _rules;
    private readonly ICleanupShell _shell;
    private readonly ILogger<DiskCleanupService> _log;
    private readonly Func<string?>? _getDownloadsPath;
    private readonly SemaphoreSlim _gate = new(1);
    private CleanupScan? _lastScan;
    public IReadOnlyList<CleanupCategory> Categories => CleanupCatalog.Categories;

    public DiskCleanupService(ILogger<DiskCleanupService> log)
        : this(CleanupCatalog.Rules(), new WindowsCleanupShell(), log, WindowsCleanupShell.GetDownloadsPath) { }
    internal DiskCleanupService(IReadOnlyList<CleanupRule> rules, ICleanupShell shell, ILogger<DiskCleanupService> log,
        Func<string?>? getDownloadsPath = null)
    { _rules = rules; _shell = shell; _log = log; _getDownloadsPath = getDownloadsPath; }

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
        var files = new List<CleanupFile>(); var issues = new List<CleanupIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        IEnumerable<CleanupRule> rules = _rules;
        try
        {
            if (GetDownloadsRule() is { } downloads) rules = rules.Append(downloads);
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
                if (!Directory.Exists(rule.Root)) continue;
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
                                if (seen.Add(path)) files.Add(new(rule.CategoryId, path, stamp.Length) { Stamp = stamp, Rule = rule, ShortcutTarget = target });
                            }
                            catch (Exception ex) when (IsFileError(ex)) { AddIssue(issues, path, ex.Message); }
                        }
                    }
                    catch (Exception ex) when (IsFileError(ex)) { AddIssue(issues, directory, ex.Message); }
                }
            }
            catch (Exception ex) when (IsFileError(ex)) { AddIssue(issues, rule.Root, ex.Message); }
        }
        RecycleBinState? recycle = null;
        try { recycle = _shell.QueryRecycleBin(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { AddIssue(issues, "Корзина", ex.Message); }
        _log.LogInformation("Анализ очистки: {Files} файлов, {Bytes} байт, {Issues} замечаний", files.Count, files.Sum(f => f.Bytes), issues.Count);
        return new(files.AsReadOnly(), issues.AsReadOnly(), recycle, now);
    }

    public async Task<CleanupResult> CleanAsync(CleanupScan scan, IReadOnlyCollection<string> selectedCategories,
        IProgress<string>? progress = null, CancellationToken ct = default, IReadOnlySet<string>? excludedPaths = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!ReferenceEquals(scan, _lastScan)) throw new InvalidOperationException("Нужен новый анализ перед очисткой.");
            if (DateTime.UtcNow - scan.CreatedUtc > TimeSpan.FromMinutes(30)) throw new InvalidOperationException("Результаты устарели. Повторите анализ.");
            var selected = selectedCategories.ToHashSet(StringComparer.Ordinal);
            if (selected.Except(Categories.Select(c => c.Id)).Any()) throw new ArgumentException("Неизвестная категория очистки.");
            if (selected.Contains("recycle-bin") && scan.RecycleBin is null) throw new InvalidOperationException("Корзина не проверена. Снимите её выбор или повторите анализ.");
            if (selected.Contains(CleanupCatalog.DownloadsId) && scan.Files.FirstOrDefault(f =>
                    f.CategoryId == CleanupCatalog.DownloadsId && excludedPaths?.Contains(f.Path) != true) is { } download &&
                !string.Equals(GetDownloadsRule()?.Root, download.Rule.Root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Расположение папки «Загрузки» изменилось. Повторите анализ.");
            _lastScan = null; // Один подтверждённый анализ разрешает только одну попытку очистки.
            return await Task.Run(() => Clean(scan, selected, excludedPaths, progress, ct), CancellationToken.None);
        }
        finally { _gate.Release(); }
    }

    private CleanupRule? GetDownloadsRule() => CleanupCatalog.DownloadsRule(_getDownloadsPath?.Invoke(), _rules);

    private CleanupResult Clean(CleanupScan scan, HashSet<string> selected, IReadOnlySet<string>? excluded, IProgress<string>? progress, CancellationToken ct)
    {
        int deleted = 0, skipped = 0; long bytes = 0, released = 0;
        var issues = new List<CleanupIssue>(); var emptied = false; long recycleBytes = 0;
        foreach (var file in scan.Files.Where(f => selected.Contains(f.CategoryId) && excluded?.Contains(f.Path) != true))
        {
            if (ct.IsCancellationRequested) break;
            if ((deleted + skipped) % 64 == 0) progress?.Report($"Удалено файлов: {deleted}");
            try
            {
                released += CleanupFiles.Delete(file, () => file.ShortcutTarget is null || _shell.IsMissingTarget(file.ShortcutTarget));
                deleted++; bytes += file.Bytes;
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                skipped++; AddIssue(issues, file.Path, ex.Message);
                if (skipped <= 20) _log.LogWarning("Пропуск очистки {Path}: {Reason}", file.Path, ex.Message);
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
        _log.LogInformation("Очистка: удалено {Files}, данных {Bytes}, выделено на диске {Released}; пропущено {Skipped}; корзина {Recycle}; отменено {Cancelled}", deleted, bytes, released, skipped, emptied, ct.IsCancellationRequested);
        return new(deleted, bytes, released, skipped, issues.AsReadOnly(), ct.IsCancellationRequested, emptied, recycleBytes);
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
