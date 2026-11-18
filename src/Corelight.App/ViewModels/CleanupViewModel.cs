using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Corelight.App.Services;
using Corelight.Core.Cleanup;
using Microsoft.Extensions.Logging;

namespace Corelight.App.ViewModels;

public sealed partial class CleanupCategoryViewModel(CleanupCategory category) : ObservableObject
{
    public CleanupCategory Category { get; } = category;
    public string Title => Category.Title;
    public string Description => Category.Description;
    [ObservableProperty] public partial bool IsSelected { get; set; } = category.DefaultSelected;
    [ObservableProperty] public partial string Summary { get; set; } = "Ожидает анализа";
}

public sealed partial class CleanupFileViewModel(CleanupFile file) : ObservableObject
{
    public CleanupFile File { get; } = file;
    public string Path => File.Path;
    public string Size => CleanupSize.Format(File.Bytes);
    [ObservableProperty] public partial bool IsSelected { get; set; } = true;
}

public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly DiskCleanupService _cleanup;
    private readonly AppState _state;
    private readonly DialogService _dialogs;
    private readonly ILogger<CleanupViewModel> _log;
    private CleanupScan? _scan;
    private readonly List<CleanupFileViewModel> _files = [];
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
    [ObservableProperty] public partial IReadOnlyList<CleanupFileViewModel> VisibleFiles { get; set; } = [];
    [ObservableProperty] public partial string Status { get; set; } = "Нажмите «Анализировать», чтобы найти файлы для очистки.";
    [ObservableProperty] public partial string SelectionSummary { get; set; } = "Ничего не выбрано";
    [ObservableProperty] public partial bool CanClean { get; set; }
    [ObservableProperty] public partial bool HasIssues { get; set; }
    [ObservableProperty] public partial string DetailsHint { get; set; } = "";
    [ObservableProperty] public partial bool IsRecycleBinCategory { get; set; }

    partial void OnSelectedCategoryChanged(CleanupCategoryViewModel? value) => UpdateVisibleFiles();

    private void UpdateVisibleFiles()
    {
        var id = SelectedCategory?.Category.Id;
        IsRecycleBinCategory = id == "recycle-bin";
        VisibleFiles = _files.Where(f => f.File.CategoryId == id).ToArray();
        DetailsHint = id == "recycle-bin"
            ? "Корзина очищается целиком средствами Windows. Новые элементы тоже будут удалены. Проверьте её содержимое в Проводнике."
            : _scan is null ? "Список появится после анализа." : VisibleFiles.Count == 0 ? "Подходящих файлов нет." : "Снимите галочки с файлов, которые хотите сохранить.";
    }

    private void UpdateSelection()
    {
        var ids = Categories.Where(c => c.IsSelected).Select(c => c.Category.Id).ToHashSet();
        var files = _files.Where(f => f.IsSelected && ids.Contains(f.File.CategoryId)).ToArray();
        var recycle = ids.Contains("recycle-bin") ? _scan?.RecycleBin : null;
        var bytes = files.Sum(f => f.File.Bytes) + (recycle?.Bytes ?? 0);
        SelectionSummary = $"Выбрано: {files.Length} файлов · {CleanupSize.Format(bytes)}" + (recycle?.Items > 0 ? " + корзина" : "");
        CanClean = _scan is not null && (files.Length > 0 || recycle?.Items > 0);
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        _cancellation = new();
        _scan = null; CanClean = false;
        _files.Clear(); SetIssues([]);
        foreach (var category in Categories) category.Summary = "Ожидает анализа";
        try
        {
            using (_state.BeginBusy("Анализ файлов…", () => _cancellation?.Cancel()))
            {
                var progress = new Progress<string>(text => _state.BusyText = "Анализ: " + text);
                _scan = await _cleanup.AnalyzeAsync(progress, _cancellation.Token);
                _files.Clear();
                foreach (var file in _scan.Files)
                {
                    var item = new CleanupFileViewModel(file);
                    item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(item.IsSelected)) UpdateSelection(); };
                    _files.Add(item);
                }
                foreach (var category in Categories)
                {
                    var found = _files.Where(f => f.File.CategoryId == category.Category.Id).ToArray();
                    category.Summary = category.Category.Id == "recycle-bin"
                        ? _scan.RecycleBin is { } bin ? $"{bin.Items} элементов · {CleanupSize.Format(bin.Bytes)}" : "Не удалось проверить"
                        : $"{found.Length} файлов · {CleanupSize.Format(found.Sum(f => f.File.Bytes))}";
                }
                SetIssues(_scan.Issues);
                Status = $"Анализ завершён · найдено {CleanupSize.Format(_scan.Files.Sum(f => f.Bytes) + (_scan.RecycleBin?.Bytes ?? 0))}. Проверьте список и выбор.";
            }
        }
        catch (OperationCanceledException) { Status = "Анализ отменён. Файлы не удалялись."; _files.Clear(); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка анализа диска");
            _files.Clear(); Status = "Не удалось завершить анализ.";
            await _dialogs.ShowInfoAsync("Ошибка анализа", ex.Message);
        }
        finally { _cancellation.Dispose(); _cancellation = null; UpdateVisibleFiles(); UpdateSelection(); }
    }

    [RelayCommand]
    private async Task CleanAsync()
    {
        if (!CanClean || _scan is null) return;
        var selected = Categories.Where(c => c.IsSelected).Select(c => c.Category.Id).ToArray();
        var message = SelectionSummary + ".\n\nФайлы будут удалены безвозвратно. История Corelight и точка восстановления не вернут их.";
        if (selected.Contains("downloads"))
        {
            var count = _files.Count(f => f.IsSelected && f.File.CategoryId == "downloads");
            if (count > 0) message += $"\n\nИз папки «Загрузки» будет удалено файлов: {count}.";
        }
        if (selected.Contains("recycle-bin")) message += "\n\nКорзина будет очищена целиком на всех дисках, включая элементы, появившиеся после анализа.";
        if (!await _dialogs.ConfirmAsync("Очистить выбранное?", message, "Удалить файлы")) return;
        _cancellation = new();
        try
        {
            CleanupResult result;
            using (_state.BeginBusy("Очистка…", () => _cancellation?.Cancel()))
                result = await _cleanup.CleanAsync(_scan, selected, new Progress<string>(text => _state.BusyText = text), _cancellation.Token,
                    _files.Where(f => !f.IsSelected).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase));
            SetIssues(result.Issues);
            Status = $"{(result.Cancelled ? "Остановлено" : "Готово")}: удалено {result.DeletedFiles} файлов · {CleanupSize.Format(result.DeletedBytes)}. Пропущено: {result.SkippedFiles}.";
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
            _scan = null; CanClean = false; _files.Clear();
            foreach (var category in Categories) category.Summary = "Нужен новый анализ";
            _cancellation.Dispose(); _cancellation = null; UpdateVisibleFiles(); UpdateSelection();
        }
    }

    [RelayCommand] private void SelectNone() { foreach (var c in Categories) c.IsSelected = false; }
    [RelayCommand] private void ResetSelection() { foreach (var c in Categories) c.IsSelected = c.Category.DefaultSelected; }
    [RelayCommand] private void OpenRecycleBin() => Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
    [RelayCommand] private Task ShowIssuesAsync() => _dialogs.ShowInfoAsync("Пропуски и замечания",
        string.Join("\n\n", _issues.Take(100).Select(i => $"{i.Path}\n{i.Reason}")) + (_issues.Count > 100 ? "\n\nПоказаны первые 100 записей." : ""));
    private void SetIssues(IReadOnlyList<CleanupIssue> issues) { _issues = issues; HasIssues = issues.Count > 0; }
}
