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
    private const string RecycleBinId = "recycle-bin";
    private IReadOnlyDictionary<string, CategoryRows> _items = new Dictionary<string, CategoryRows>();
    private IEnumerable<CleanupItemViewModel> AllRows => _items.Values.SelectMany(group => group.Rows);
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
        && Categories.Any(category => category.Category.Id == RecycleBinId && category.IsSelected) ? bin : null;

    partial void OnSelectedCategoryChanged(CleanupCategoryViewModel? value) => UpdateVisibleItems();

    private void RefreshAnalysis()
    {
        UpdateVisibleItems();
        UpdateSelection();
        OnPropertyChanged(nameof(HasAnalysis));
    }

    private void UpdateVisibleItems()
    {
        var id = SelectedCategory?.Category.Id;
        IsRecycleBinCategory = id == RecycleBinId;
        VisibleItems = id is not null && _items.TryGetValue(id, out var group) ? group.Rows : [];
    }

    private void UpdateSelection()
    {
        var files = _items.Values.Sum(group => group.SelectedFiles);
        var directories = _items.Values.Sum(group => group.SelectedDirectories);
        var recycle = SelectedRecycleBin;
        var bytes = _items.Values.Sum(group => group.SelectedBytes) + (recycle?.Bytes ?? 0);
        var counts = recycle is null ? FormatCounts(files, directories) : $"файлов {recycle.Items + files + directories}";
        SelectionSummary = $"Выбрано: {counts} · {CleanupSize.Format(bytes)}";
        const int bytesPerMegabyte = 1024 * 1024;
        var megabytes = Math.Ceiling((decimal)bytes / bytesPerMegabyte);
        CleanButtonText = megabytes < 1024
            ? $"Очистить {megabytes:0} МБ"
            : $"Очистить {megabytes / 1024:0.00} ГБ";
        CanClean = _scan is not null && (files + directories > 0 || recycle?.Items > 0);
    }

    private void SetCategorySelections(IReadOnlyList<CleanupCategoryViewModel> categories, bool selected)
    {
        foreach (var category in categories)
            category.UpdateCheckBox(selected);
        _updatingSelection = true;
        try
        {
            foreach (var category in categories)
                if (_items.TryGetValue(category.Category.Id, out var group))
                    foreach (var row in group.Rows)
                        row.IsSelected = selected && (!row.Item.IsDirectory || row.IsSelected);
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
        foreach (var group in _items.Values) group.SelectionChanged = null;
        _items = new Dictionary<string, CategoryRows>();
        RecycleBinItems = [];
        foreach (var category in Categories)
        {
            category.UpdateCheckBox(false);
            category.Summary = summary;
        }
    }

    private static string FormatCounts(int files, int directories) =>
        directories == 0 ? $"файлов {files}" : $"файлов {files}, папок {directories}";

    private sealed class CategoryRows
    {
        public CategoryRows(IEnumerable<CleanupItem> items, CancellationToken ct)
        {
            var rows = new List<CleanupItemViewModel>();
            foreach (var entry in items.OrderByDescending(item => item.Bytes))
            {
                ct.ThrowIfCancellationRequested();
                var row = new CleanupItemViewModel(entry, !entry.IsDirectory);
                row.PropertyChanged += OnRowChanged;
                rows.Add(row);
                if (entry.IsDirectory) Directories++;
                else Files++;
                Bytes += entry.Bytes;
            }
            Rows = rows;
            SelectedFiles = Files;
            SelectedBytes = Bytes;
        }

        public IReadOnlyList<CleanupItemViewModel> Rows { get; }
        public int Files { get; }
        public int Directories { get; }
        public long Bytes { get; }
        public int SelectedFiles { get; private set; }
        public int SelectedDirectories { get; private set; }
        public long SelectedBytes { get; private set; }
        public Action? SelectionChanged { get; set; }

        private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(CleanupItemViewModel.IsSelected) || sender is not CleanupItemViewModel row) return;
            var delta = row.IsSelected ? 1 : -1;
            if (row.Item.IsDirectory) SelectedDirectories += delta;
            else SelectedFiles += delta;
            SelectedBytes += delta * row.Item.Bytes;
            SelectionChanged?.Invoke();
        }
    }

    private sealed record PreparedAnalysis(IReadOnlyDictionary<string, CategoryRows> Groups, IReadOnlyList<RecycleBinItemViewModel> RecycleItems);

    private static PreparedAnalysis PrepareAnalysis(CleanupScan scan, CancellationToken ct)
    {
        var groups = scan.Items.GroupBy(item => item.CategoryId)
            .ToDictionary(group => group.Key, group => new CategoryRows(group, ct), StringComparer.Ordinal);
        var recycle = scan.RecycleBinItems.OrderByDescending(item => item.Bytes)
            .Select(item => new RecycleBinItemViewModel(item)).ToArray();
        return new(groups, recycle);
    }

    private void ShowAnalysis(PreparedAnalysis analysis)
    {
        _items = analysis.Groups;
        RecycleBinItems = analysis.RecycleItems;
        var bin = _scan?.RecycleBin;
        foreach (var category in Categories)
        {
            category.UpdateCheckBox(true);
            if (category.Category.Id == RecycleBinId)
            {
                category.Summary = bin is null ? "Не удалось проверить" :
                    $"Элементов: {bin.Items} · {CleanupSize.Format(bin.Bytes)}" +
                    (bin.Items > 0 && RecycleBinItems.Count == 0 ? " · Список недоступен" : "");
                continue;
            }

            if (_items.TryGetValue(category.Category.Id, out var group))
            {
                group.SelectionChanged = () =>
                {
                    if (_updatingSelection) return;
                    category.UpdateCheckBox(group.SelectedFiles + group.SelectedDirectories > 0);
                    UpdateSelection();
                };
            }
            category.Summary = $"{FormatCounts(group?.Files ?? 0, group?.Directories ?? 0)} · {CleanupSize.Format(group?.Bytes ?? 0)}";
        }
        var counts = FormatCounts(_items.Values.Sum(group => group.Files), _items.Values.Sum(group => group.Directories));
        Status = $"Найдено: {counts} · {CleanupSize.Format(_items.Values.Sum(group => group.Bytes))}";
        if (bin is { Items: > 0 })
            Status += $" · В корзине {bin.Items} · {CleanupSize.Format(bin.Bytes)}";
        RefreshAnalysis();
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
                var scan = await _cleanup.AnalyzeAsync(progress, _cancellation.Token);
                var prepared = await Task.Run(() => PrepareAnalysis(scan, _cancellation.Token), _cancellation.Token);
                _cancellation.Token.ThrowIfCancellationRequested();
                _scan = scan;
                ShowAnalysis(prepared);
            }
        }
        catch (OperationCanceledException) { Status = "Анализ отменён."; ClearAnalysis("Ожидает анализа"); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка анализа диска");
            ClearAnalysis("Ожидает анализа"); Status = "Не удалось завершить анализ.";
            await _dialogs.ShowInfoAsync("Ошибка анализа", ex.Message);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            if (_scan is null) RefreshAnalysis();
        }
    }

    [RelayCommand]
    private async Task CleanAsync()
    {
        if (!CanClean || _scan is null) return;
        var selected = _items.Where(group => group.Value.SelectedFiles + group.Value.SelectedDirectories > 0).Select(group => group.Key).ToHashSet();
        if (SelectedRecycleBin is not null) selected.Add(RecycleBinId);
        var message = SelectionSummary + ".\n\nВыбранные файлы будут удалены безвозвратно.";
        if (!await _dialogs.ConfirmAsync("Очистить выбранное?", message, "Удалить выбранное")) return;
        _cancellation = new();
        try
        {
            CleanupResult result;
            using (_state.BeginBusy("Очистка…", () => _cancellation?.Cancel()))
                result = await _cleanup.CleanAsync(_scan, selected, new Progress<string>(text => _state.BusyText = text), _cancellation.Token,
                    AllRows.Where(item => !item.IsSelected).Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    AllRows.Where(item => item.IsSelected && item.Item.IsDirectory).Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase));
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
