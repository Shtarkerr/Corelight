using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Corelight.App.Services;
using Corelight.Core.Cleanup;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace Corelight.App.ViewModels;

public sealed partial class CleanupCategoryViewModel(CleanupCategory category) : ObservableObject
{
    private bool _isSelected;

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
    public bool IsSelected
    {
        get => _isSelected;
        set => SelectionRequested?.Invoke(this, value);
    }
    [ObservableProperty] public partial string Summary { get; set; } = "Ожидает анализа";

    internal event Action<CleanupCategoryViewModel, bool>? SelectionRequested;
    internal void UpdateCheckBox(bool selected) => SetProperty(ref _isSelected, selected, nameof(IsSelected));
}

public sealed partial class CleanupItemViewModel(CleanupItem item, bool isSelected) : ObservableObject
{
    public CleanupItem Item { get; } = item;
    public string Path => Item.Path;
    public string Name => System.IO.Path.GetFileName(Path) + (Item.IsDirectory ? " (папка)" : "");
    public string Size => Item.IsDirectory ? "—" : CleanupSize.Format(Item.Bytes);
    [ObservableProperty] public partial bool IsSelected { get; set; } = isSelected;
}

public sealed record RecycleBinItemViewModel(RecycleBinItem Item)
{
    public string Name => Item.IsDirectory ? Item.Name + " (папка)" : Item.Name;
    public string Size => Item.Bytes is { } bytes ? CleanupSize.Format(bytes) : "—";
}

public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly DiskCleanupService _cleanup;
    private readonly AppState _state;
    private readonly DialogService _dialogs;
    private readonly ILogger<CleanupViewModel> _log;
    private CleanupScan? _scan;
    private readonly List<CleanupItemViewModel> _items = [];
    private CancellationTokenSource? _cancellation;
    private bool _updatingSelection;

    public CleanupViewModel(DiskCleanupService cleanup, AppState state, DialogService dialogs, ILogger<CleanupViewModel> log)
    {
        _cleanup = cleanup; _state = state; _dialogs = dialogs; _log = log;
        foreach (var category in cleanup.Categories)
        {
            var item = new CleanupCategoryViewModel(category);
            item.SelectionRequested += SelectCategory;
            Categories.Add(item);
        }
        SelectedCategory = Categories[0];
    }

    public ObservableCollection<CleanupCategoryViewModel> Categories { get; } = [];
    [ObservableProperty] public partial CleanupCategoryViewModel? SelectedCategory { get; set; }
    [ObservableProperty] public partial IReadOnlyList<CleanupItemViewModel> VisibleItems { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<RecycleBinItemViewModel> RecycleBinItems { get; set; } = [];
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial string SelectionSummary { get; set; } = "Ничего не выбрано";
    [ObservableProperty] public partial string CleanButtonText { get; set; } = "Очистить 0 МБ";
    public bool HasAnalysis => _scan is not null;
    [ObservableProperty] public partial bool CanClean { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowsItemSelection))]
    public partial bool IsRecycleBinCategory { get; set; }
    public bool ShowsItemSelection => !IsRecycleBinCategory;

    private RecycleBinState? SelectedRecycleBin => _scan?.RecycleBin is { Items: > 0 } bin
        && Categories.Any(category => category.Category.Id == "recycle-bin" && category.IsSelected) ? bin : null;

    partial void OnSelectedCategoryChanged(CleanupCategoryViewModel? value) => UpdateVisibleItems();

    private void RefreshAnalysis()
    {
        RecycleBinItems = _scan?.RecycleBinItems.OrderByDescending(item => item.Bytes)
            .Select(item => new RecycleBinItemViewModel(item)).ToArray() ?? [];
        UpdateVisibleItems();
        UpdateSelection();
        OnPropertyChanged(nameof(HasAnalysis));
    }

    private void UpdateVisibleItems()
    {
        var id = SelectedCategory?.Category.Id;
        IsRecycleBinCategory = id == "recycle-bin";
        VisibleItems = _items.Where(item => item.Item.CategoryId == id).ToArray();
    }

    private void UpdateSelection()
    {
        var items = _items.Where(item => item.IsSelected).ToArray();
        var recycle = SelectedRecycleBin;
        var bytes = items.Sum(item => item.Item.Bytes) + (recycle?.Bytes ?? 0);
        SelectionSummary = $"Выбрано: {FormatCounts(items)} · {CleanupSize.Format(bytes)}" + (recycle?.Items > 0 ? " + корзина" : "");
        const int bytesPerMegabyte = 1024 * 1024;
        var megabytes = Math.Ceiling((decimal)bytes / bytesPerMegabyte);
        CleanButtonText = megabytes < 1024
            ? $"Очистить {megabytes:0} МБ"
            : $"Очистить {megabytes / 1024:0.00} ГБ";
        CanClean = _scan is not null && (items.Length > 0 || recycle?.Items > 0);
    }

    private void SetCategorySelections(IReadOnlyList<CleanupCategoryViewModel> categories, bool selected)
    {
        foreach (var category in categories)
            category.UpdateCheckBox(selected);
        var categoryIds = categories.Select(category => category.Category.Id).ToHashSet();

        _updatingSelection = true;
        try
        {
            foreach (var item in _items)
                if (categoryIds.Contains(item.Item.CategoryId))
                    item.IsSelected = selected && (!item.Item.IsDirectory || item.IsSelected);
        }
        finally { _updatingSelection = false; }

        UpdateSelection();
    }

    private void SelectCategory(CleanupCategoryViewModel category, bool selected)
    {
        SelectedCategory = category;
        SetCategorySelections([category], selected);
    }

    private void ClearAnalysis(string summary)
    {
        _scan = null;
        _items.Clear();
        foreach (var category in Categories)
        {
            category.UpdateCheckBox(false);
            category.Summary = summary;
        }
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
        ClearAnalysis("Ожидает анализа");
        RefreshAnalysis();
        try
        {
            using (_state.BeginBusy("Анализ…", () => _cancellation?.Cancel()))
            {
                var progress = new Progress<string>(text => _state.BusyText = "Анализ: " + text);
                _scan = await _cleanup.AnalyzeAsync(progress, _cancellation.Token);
                foreach (var entry in _scan.Items.OrderByDescending(entry => entry.Bytes))
                {
                    var item = new CleanupItemViewModel(entry, !entry.IsDirectory);
                    item.PropertyChanged += (_, e) =>
                    {
                        if (_updatingSelection || e.PropertyName != nameof(item.IsSelected)) return;
                        var group = Categories.First(category => category.Category.Id == entry.CategoryId);
                        group.UpdateCheckBox(_items.Any(row => row.IsSelected && row.Item.CategoryId == entry.CategoryId));
                        UpdateSelection();
                    };
                    _items.Add(item);
                }
                foreach (var category in Categories)
                {
                    category.UpdateCheckBox(true);
                    if (category.Category.Id == "recycle-bin")
                    {
                        category.Summary = _scan.RecycleBin is { } bin
                            ? $"Элементов: {bin.Items} · {CleanupSize.Format(bin.Bytes)}" +
                              (bin.Items > 0 && _scan.RecycleBinItems.Count == 0 ? " · Список недоступен" : "")
                            : "Не удалось проверить";
                        continue;
                    }
                    var found = _items.Where(item => item.Item.CategoryId == category.Category.Id).ToArray();
                    category.Summary = $"{FormatCounts(found)} · {CleanupSize.Format(found.Sum(item => item.Item.Bytes))}";
                }
                var bytes = _scan.Items.Sum(item => item.Bytes) + (_scan.RecycleBin?.Bytes ?? 0);
                Status = $"Найдено: {FormatCounts(_items)} · {CleanupSize.Format(bytes)}";
            }
        }
        catch (OperationCanceledException) { Status = "Анализ отменён."; ClearAnalysis("Ожидает анализа"); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка анализа диска");
            ClearAnalysis("Ожидает анализа"); Status = "Не удалось завершить анализ.";
            await _dialogs.ShowInfoAsync("Ошибка анализа", ex.Message);
        }
        finally { _cancellation.Dispose(); _cancellation = null; RefreshAnalysis(); }
    }

    [RelayCommand]
    private async Task CleanAsync()
    {
        if (!CanClean || _scan is null) return;
        var selected = _items.Where(item => item.IsSelected).Select(item => item.Item.CategoryId).ToHashSet();
        if (SelectedRecycleBin is not null) selected.Add("recycle-bin");
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
            var removed = result.DeletedDirectories > 0 ? $"файлов {result.DeletedFiles}, папок {result.DeletedDirectories}" : $"файлов {result.DeletedFiles}";
            var summary = $"{(result.Cancelled ? "Остановлено" : "Готово")}: удалено {removed} · {CleanupSize.Format(result.DeletedBytes)}";
            if (result.RecycleBinEmptied) summary += $" · корзина очищена (≈{CleanupSize.Format(result.RecycleBinBytes)})";
            Status = $"{summary}. Пропущено: {result.SkippedFiles + result.SkippedDirectories}.";
            await _dialogs.ShowInfoAsync(result.Cancelled ? "Очистка остановлена" : "Очистка завершена", summary + ".");
        }
        catch (OperationCanceledException) { Status = "Очистка отменена до начала удаления."; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка очистки диска"); Status = "Очистка не завершена. Повторите анализ.";
            await _dialogs.ShowInfoAsync("Ошибка очистки", ex.Message);
        }
        finally
        {
            ClearAnalysis("Нужен новый анализ");
            _cancellation.Dispose(); _cancellation = null; RefreshAnalysis();
        }
    }

    [RelayCommand] private void SelectNone() => SetCategorySelections(Categories, false);
    [RelayCommand] private void SelectAll() => SetCategorySelections(Categories, true);
}
