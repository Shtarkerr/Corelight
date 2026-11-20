using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Corelight.App.Services;
using Corelight.Core.Scanning;
using Corelight.Core.Tweaks;

namespace Corelight.App.ViewModels;

public sealed record ScanGroupViewModel(string Title, IReadOnlyList<ScanItemViewModel> Items);

public sealed partial class ScanResultsViewModel(
    AppState state,
    ApplyCoordinator coordinator,
    NavigationService navigation,
    DialogService dialogs,
    ILogger<ScanResultsViewModel> log) : ObservableObject, INavigationAware
{
    private ScanReport? _renderedReport;
    private int _renderedUserScanVersion;

    public ObservableCollection<ScanGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoManualItems), nameof(ManualItemsHeader))]
    public partial IReadOnlyList<ScanItemViewModel> ManualItems { get; set; } = [];

    [ObservableProperty]
    public partial bool IsManualExpanded { get; set; }

    public bool HasNoManualItems => ManualItems.Count == 0;
    public bool HasRecommendedItems => Groups.Count > 0;
    public bool HasNoRecommendedItems => !HasRecommendedItems;
    public string ManualItemsHeader
    {
        get
        {
            var selected = ManualItems.Count(i => i.IsSelected);
            return selected == 0 ? $"Для ручной настройки ({ManualItems.Count})"
                : $"Для ручной настройки ({ManualItems.Count}, выбрано {selected})";
        }
    }

    private IEnumerable<ScanItemViewModel> RecommendedItems => Groups.SelectMany(g => g.Items);
    private IEnumerable<ScanItemViewModel> SelectableItems => RecommendedItems.Concat(ManualItems);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoScan))]
    public partial bool HasScan { get; set; }

    public bool HasNoScan => !HasScan;

    [ObservableProperty]
    public partial int TotalCount { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial int SelectedCount { get; set; }

    public async Task OnNavigatedToAsync()
    {
        // Если систему меняли в других разделах, пересканируем, чтобы не предлагать уже применённое.
        if (state.NeedsScanRefresh)
            await RescanAsync();
        Rebuild();
    }

    [RelayCommand]
    private void GoHome() => navigation.NavigateTo<HomeViewModel>();

    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (var item in RecommendedItems)
            item.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in SelectableItems)
            item.IsSelected = false;
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var selected = SelectableItems.Where(i => i.IsSelected).Select(i => i.Tweak).ToList();
        var confirmed = await dialogs.ConfirmAsync(
            "Применить изменения?",
            $"Будет применено изменений: {selected.Count}.\n\n" +
            "Настройки сохранятся в истории; Corelight попробует создать точку восстановления. Возврат приложений может требовать интернета и ручной установки. AppX удаляются для всех пользователей, возвращаются текущему.",
            "Применить");
        if (!confirmed)
            return;

        // Если пользователь отменил операцию на шаге точки восстановления, система не менялась —
        // оставляем список и его галочки как есть.
        var version = state.ChangeVersion;
        await coordinator.ApplyAsync(selected, showSummary: true);
        if (state.ChangeVersion == version)
            return;
        await RescanAsync();
        Rebuild();
    }

    private bool CanApply() => SelectedCount > 0;

    private async Task RescanAsync()
    {
        try
        {
            using (state.BeginBusy("Обновление результатов…"))
                await state.RefreshTweakStatusesAsync();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Ошибка повторного сканирования");
            state.ClearLastScan();
        }
    }

    private void Rebuild()
    {
        var report = state.LastScan;
        var sameUserScan = _renderedUserScanVersion == state.UserScanVersion;
        if (ReferenceEquals(report, _renderedReport) && sameUserScan)
            return;

        var selection = sameUserScan && _renderedReport is not null && report is not null
            ? SelectableItems.Where(i => i.IsSelected).Select(i => i.Tweak.Id).ToHashSet(StringComparer.Ordinal)
            : null;
        ScanItemViewModel CreateItem(TweakStatus status) => new(status, selection?.Contains(status.Tweak.Id));

        foreach (var item in SelectableItems)
            item.PropertyChanged -= OnItemChanged;
        Groups.Clear();

        _renderedReport = report;
        _renderedUserScanVersion = state.UserScanVersion;
        HasScan = report is not null;
        if (selection is null) IsManualExpanded = false;
        ManualItems = report?.ManualItems.Select(CreateItem).ToArray() ?? [];
        if (report is not null)
        {
            foreach (var group in report.RecommendedItems.GroupBy(s => s.Tweak.Impact))
            {
                var items = group.Select(CreateItem).ToArray();
                var title = group.Key switch
                {
                    TweakImpact.High => "Высокий предполагаемый эффект",
                    TweakImpact.Medium => "Средний предполагаемый эффект",
                    _ => "Остальные рекомендации",
                };
                Groups.Add(new ScanGroupViewModel(title, items));
            }
        }
        foreach (var item in SelectableItems)
            item.PropertyChanged += OnItemChanged;
        OnPropertyChanged(nameof(HasRecommendedItems));
        OnPropertyChanged(nameof(HasNoRecommendedItems));
        UpdateCounts();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ScanItemViewModel.IsSelected)) return;
        if (sender is ScanItemViewModel { IsSelected: true, Tweak.ExclusiveGroup: { } group } selected)
        {
            foreach (var other in SelectableItems.Where(i => i != selected && i.Tweak.ExclusiveGroup == group))
                other.IsSelected = false;
        }
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        var items = SelectableItems.ToList();
        TotalCount = items.Count;
        SelectedCount = items.Count(i => i.IsSelected);
        OnPropertyChanged(nameof(ManualItemsHeader));
    }
}
