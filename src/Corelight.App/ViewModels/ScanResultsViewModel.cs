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
    Scanner scanner,
    ApplyCoordinator coordinator,
    NavigationService navigation,
    DialogService dialogs,
    ILogger<ScanResultsViewModel> log) : ObservableObject, INavigationAware
{
    private int _renderedVersion = -1;
    private ScanReport? _renderedReport;

    public ObservableCollection<ScanGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasManualItems), nameof(HasAdvancedOnlyItems), nameof(ManualItemsHeader))]
    public partial IReadOnlyList<ScanItemViewModel> ManualItems { get; set; } = [];

    [ObservableProperty]
    public partial bool IsManualExpanded { get; set; }

    public bool HasManualItems => ManualItems.Count > 0;
    public bool HasAdvancedOnlyItems => ManualItems.Any(i => i.IsAdvancedOnly);
    public string ManualItemsHeader
    {
        get
        {
            var selected = ManualItems.Count(i => i.CanSelect && i.IsSelected);
            return selected == 0 ? $"Для ручной настройки ({ManualItems.Count})"
                : $"Для ручной настройки ({ManualItems.Count}, выбрано {selected})";
        }
    }

    private IEnumerable<ScanItemViewModel> SelectableItems =>
        Groups.SelectMany(g => g.Items).Concat(ManualItems.Where(i => i.CanSelect));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSuggestions), nameof(HasNoScan))]
    public partial bool HasScan { get; set; }

    public bool HasNoScan => !HasScan;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSuggestions))]
    public partial int TotalCount { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial int SelectedCount { get; set; }

    public bool HasNoSuggestions => HasScan && TotalCount == 0;

    public async Task OnNavigatedToAsync()
    {
        // Если систему меняли в других разделах, пересканируем, чтобы не предлагать уже применённое.
        if (state.LastScan is not null && _renderedVersion != -1 && _renderedVersion != state.ChangeVersion)
            await RescanAsync();
        Rebuild();
    }

    [RelayCommand]
    private void GoHome() => navigation.NavigateTo<HomeViewModel>();

    [RelayCommand]
    private void OpenAdvanced() => navigation.NavigateTo<AdvancedViewModel>();

    [RelayCommand]
    private void SelectAll() => SetAll(true);

    [RelayCommand]
    private void SelectNone() => SetAll(false);

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
                state.LastScan = await scanner.ScanAsync(await state.GetHardwareAsync());
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Ошибка повторного сканирования");
        }
    }

    private void Rebuild()
    {
        var report = state.LastScan;
        if (_renderedVersion == state.ChangeVersion && ReferenceEquals(report, _renderedReport))
            return;

        foreach (var item in SelectableItems)
            item.PropertyChanged -= OnItemChanged;
        Groups.Clear();

        _renderedReport = report;
        _renderedVersion = state.ChangeVersion;
        HasScan = report is not null;
        IsManualExpanded = false;
        ManualItems = report?.ManualItems.Select(s => new ScanItemViewModel(s)).ToArray() ?? [];
        if (report is not null)
        {
            foreach (var group in report.Suggestions.Where(s => s.IsPreselected).GroupBy(s => s.Tweak.Impact))
            {
                var items = group.Select(s => new ScanItemViewModel(s)).ToList();
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
        UpdateCounts();
    }

    private void SetAll(bool selected)
    {
        foreach (var item in SelectableItems)
            item.IsSelected = selected;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScanItemViewModel.IsSelected))
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
