using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Corelight.App.Services;
using Corelight.Core.Cleanup;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace Corelight.App.ViewModels;

public sealed partial class CleanupCategoryViewModel(CleanupCategory category) : ObservableObject
{
    public CleanupCategory Category { get; } = category;
    public string Title => Category.Title;
    public string Description => Category.Description;
    public SymbolRegular Icon => Category.Id switch
    {
        "user-temp" => SymbolRegular.Folder24,
        "windows-temp" => SymbolRegular.Server24,
        "thumbnails" => SymbolRegular.Image24,
        "shaders" => SymbolRegular.Flash24,
        "error-reports" => SymbolRegular.TaskListLtr24,
        "shortcuts" => SymbolRegular.Link24,
        "downloads" => SymbolRegular.ArrowDownload24,
        "recycle-bin" => SymbolRegular.Delete24,
        _ => SymbolRegular.Document24,
    };
    [ObservableProperty] public partial bool IsSelected { get; set; } = category.DefaultSelected;
    [ObservableProperty] public partial string Summary { get; set; } = "Ожидает анализа";
}

public sealed partial class CleanupItemViewModel(CleanupItem item) : ObservableObject
{
    public CleanupItem Item { get; } = item;
    public string Path => Item.Path;
    public string Name => System.IO.Path.GetFileName(Path) + (Item.IsDirectory ? " (папка)" : "");
    public string Size => Item.IsDirectory ? "—" : CleanupSize.Format(Item.Bytes);
    [ObservableProperty] public partial bool IsSelected { get; set; } = !item.IsDirectory;
}

public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly DiskCleanupService _cleanup;
    private readonly AppState _state;
    private readonly DialogService _dialogs;
    private readonly ILogger<CleanupViewModel> _log;
    private CleanupScan? _scan;
    private readonly List<CleanupItemViewModel> _items = [];
    private IReadOnlyList<CleanupIssue> _issues = [];
    private CancellationTokenSource? _cancellation;

    public CleanupViewModel(DiskCleanupService cleanup, AppState state, DialogService dialogs, ILogger<CleanupViewModel> log)
    {
        _cleanup = cleanup; _state = state; _dialogs = dialogs; _log = log;
        foreach (var category in cleanup.Categories)
        {
            var item = new CleanupCategoryViewModel(category);
            item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(item.IsSelected)) UpdateSelection(); };
            Categories.Add(item);
        }
        SelectedCategory = Categories[0];
    }

    public ObservableCollection<CleanupCategoryViewModel> Categories { get; } = [];
    [ObservableProperty] public partial CleanupCategoryViewModel? SelectedCategory { get; set; }
    [ObservableProperty] public partial IReadOnlyList<CleanupItemViewModel> VisibleItems { get; set; } = [];
    [ObservableProperty] public partial string Status { get; set; } = "Нажмите «Анализировать», чтобы найти элементы для очистки.";
    [ObservableProperty] public partial string SelectionSummary { get; set; } = "Ничего не выбрано";
    [ObservableProperty] public partial bool CanClean { get; set; }
    [ObservableProperty] public partial bool HasIssues { get; set; }
    [ObservableProperty] public partial string DetailsHint { get; set; } = "";
    [ObservableProperty] public partial bool IsRecycleBinCategory { get; set; }

    partial void OnSelectedCategoryChanged(CleanupCategoryViewModel? value) => UpdateVisibleItems();

    private void UpdateVisibleItems()
    {
        var id = SelectedCategory?.Category.Id;
        IsRecycleBinCategory = id == "recycle-bin";
        VisibleItems = _items.Where(item => item.Item.CategoryId == id).ToArray();
        DetailsHint = id switch
        {
            "recycle-bin" => "Корзина очищается целиком средствами Windows. Новые элементы тоже будут удалены. Проверьте её содержимое в Проводнике.",
            _ when _scan is null => "Список появится после анализа.",
            _ when VisibleItems.Count == 0 => "Подходящих элементов нет.",
            _ => "Снимите галочки с элементов, которые хотите сохранить.",
        };
    }

    private void UpdateSelection()
    {
        var ids = Categories.Where(c => c.IsSelected).Select(c => c.Category.Id).ToHashSet();
        var items = _items.Where(item => item.IsSelected && ids.Contains(item.Item.CategoryId)).ToArray();
        var recycle = ids.Contains("recycle-bin") ? _scan?.RecycleBin : null;
        var bytes = items.Sum(item => item.Item.Bytes) + (recycle?.Bytes ?? 0);
        SelectionSummary = $"Выбрано: {FormatCounts(items)} · {CleanupSize.Format(bytes)}" + (recycle?.Items > 0 ? " + корзина" : "");
        CanClean = _scan is not null && (items.Length > 0 || recycle?.Items > 0);
    }

    private static string FormatCounts(IReadOnlyList<CleanupItemViewModel> items)
    {
        var directories = items.Count(item => item.Item.IsDirectory);
        var files = items.Count - directories;
        return directories == 0 ? $"файлов {files}" : $"файлов {files}, папок {directories}";
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        _cancellation = new();
        _scan = null; CanClean = false;
        _items.Clear(); SetIssues([]);
        foreach (var category in Categories) category.Summary = "Ожидает анализа";
        try
        {
            using (_state.BeginBusy("Анализ…", () => _cancellation?.Cancel()))
            {
                var progress = new Progress<string>(text => _state.BusyText = "Анализ: " + text);
                _scan = await _cleanup.AnalyzeAsync(progress, _cancellation.Token);
                foreach (var entry in _scan.Items)
                {
                    var item = new CleanupItemViewModel(entry);
                    item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(item.IsSelected)) UpdateSelection(); };
                    _items.Add(item);
                }
                foreach (var category in Categories)
                {
                    var found = _items.Where(item => item.Item.CategoryId == category.Category.Id).ToArray();
                    category.Summary = category.Category.Id == "recycle-bin"
                        ? _scan.RecycleBin is { } bin ? $"{bin.Items} элементов · {CleanupSize.Format(bin.Bytes)}" : "Не удалось проверить"
                        : $"{FormatCounts(found)} · {CleanupSize.Format(found.Sum(item => item.Item.Bytes))}";
                }
                SetIssues(_scan.Issues);
                var bytes = _scan.Items.Sum(item => item.Bytes) + (_scan.RecycleBin?.Bytes ?? 0);
                Status = $"Найдено: {FormatCounts(_items)} · {CleanupSize.Format(bytes)}. Проверьте список.";
            }
        }
        catch (OperationCanceledException) { Status = "Анализ отменён."; _items.Clear(); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка анализа диска");
            _items.Clear(); Status = "Не удалось завершить анализ.";
            await _dialogs.ShowInfoAsync("Ошибка анализа", ex.Message);
        }
        finally { _cancellation.Dispose(); _cancellation = null; UpdateVisibleItems(); UpdateSelection(); }
    }

    [RelayCommand]
    private async Task CleanAsync()
    {
        if (!CanClean || _scan is null) return;
        var selected = Categories.Where(c => c.IsSelected).Select(c => c.Category.Id).ToArray();
        var message = SelectionSummary + ".\n\nВыбранные элементы будут удалены безвозвратно. История Corelight и точка восстановления не вернут их.";
        if (selected.Contains("downloads"))
        {
            var count = _items.Count(item => item.IsSelected && item.Item.CategoryId == "downloads");
            if (count > 0) message += $"\n\nИз папки «Загрузки» будет удалено файлов: {count}.";
        }
        if (selected.Contains("recycle-bin")) message += "\n\nКорзина будет очищена целиком на всех дисках, включая элементы, появившиеся после анализа.";
        if (!await _dialogs.ConfirmAsync("Очистить выбранное?", message, "Удалить выбранное")) return;
        _cancellation = new();
        try
        {
            CleanupResult result;
            using (_state.BeginBusy("Очистка…", () => _cancellation?.Cancel()))
                result = await _cleanup.CleanAsync(_scan, selected, new Progress<string>(text => _state.BusyText = text), _cancellation.Token,
                    _items.Where(item => !item.IsSelected).Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    _items.Where(item => item.IsSelected && item.Item.IsDirectory).Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase));
            SetIssues(result.Issues);
            var removed = result.DeletedDirectories > 0 ? $"файлов {result.DeletedFiles}, папок {result.DeletedDirectories}" : $"файлов {result.DeletedFiles}";
            Status = $"{(result.Cancelled ? "Остановлено" : "Готово")}: удалено {removed} · {CleanupSize.Format(result.DeletedBytes)}. Пропущено: {result.SkippedFiles + result.SkippedDirectories}.";
            if (result.RecycleBinEmptied) Status += $" Корзина очищена: ≈{CleanupSize.Format(result.RecycleBinBytes)}.";
            await _dialogs.ShowInfoAsync(result.Cancelled ? "Очистка остановлена" : "Очистка завершена",
                Status + $"\n\nОсвобождено по удалённым файлам: {CleanupSize.Format(result.ReleasedBytes)} (без корзины)." +
                "\nДля обновления списка выполните новый анализ.");
        }
        catch (OperationCanceledException) { Status = "Очистка отменена до начала удаления."; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка очистки диска"); Status = "Очистка не завершена. Повторите анализ.";
            await _dialogs.ShowInfoAsync("Ошибка очистки", ex.Message);
        }
        finally
        {
            _scan = null; CanClean = false; _items.Clear();
            foreach (var category in Categories) category.Summary = "Нужен новый анализ";
            _cancellation.Dispose(); _cancellation = null; UpdateVisibleItems(); UpdateSelection();
        }
    }

    [RelayCommand] private void SelectNone() { foreach (var c in Categories) c.IsSelected = false; }
    [RelayCommand] private void ResetSelection() { foreach (var c in Categories) c.IsSelected = c.Category.DefaultSelected; }
    [RelayCommand] private void OpenRecycleBin() => Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
    [RelayCommand] private Task ShowIssuesAsync() => _dialogs.ShowInfoAsync("Пропуски и замечания",
        string.Join("\n\n", _issues.Take(100).Select(i => $"{i.Path}\n{i.Reason}")) + (_issues.Count > 100 ? "\n\nПоказаны первые 100 записей." : ""));
    private void SetIssues(IReadOnlyList<CleanupIssue> issues) { _issues = issues; HasIssues = issues.Count > 0; }
}
