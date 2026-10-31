using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Corelight.App.Services;
using Corelight.Core.Scanning;

namespace Corelight.App.ViewModels;

public sealed record ScanGroupViewModel(CategoryInfo Info, IReadOnlyList<ScanItemViewModel> Items);

public sealed partial class ScanResultsViewModel(
    AppState state,
    Scanner scanner,
    ApplyCoordinator coordinator,
    NavigationService navigation,
    DialogService dialogs,
    ILogger<ScanResultsViewModel> log) : ObservableObject, INavigationAware
{
    private int _renderedVersion = -1;

    public ObservableCollection<ScanGroupViewModel> Groups { get; } = [];

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
    private void SelectAll() => SetAll(true);

    [RelayCommand]
    private void SelectNone() => SetAll(false);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var selected = Groups.SelectMany(g => g.Items).Where(i => i.IsSelected).Select(i => i.Tweak).ToList();
        var confirmed = await dialogs.ConfirmAsync(
            "Применить изменения?",
            $"Будет применено изменений: {selected.Count}.\n\n" +
            "Перед этим программа создаст точку восстановления Windows. Любое изменение можно откатить в разделе «История и откат».",
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
        foreach (var item in Groups.SelectMany(g => g.Items))
            item.PropertyChanged -= OnItemChanged;
        Groups.Clear();

        var report = state.LastScan;
        HasScan = report is not null;
        _renderedVersion = state.ChangeVersion;
        if (report is not null)
        {
            foreach (var info in CategoryInfo.All.Where(c => c.Category is not null))
            {
                var items = report.Suggestions
                    .Where(s => s.Tweak.Category == info.Category)
                    .Select(s => new ScanItemViewModel(s))
                    .ToList();
                if (items.Count == 0)
                    continue;
                foreach (var item in items)
                    item.PropertyChanged += OnItemChanged;
                Groups.Add(new ScanGroupViewModel(info, items));
            }
        }
        UpdateCounts();
    }

    private void SetAll(bool selected)
    {
        foreach (var item in Groups.SelectMany(g => g.Items))
            item.IsSelected = selected;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScanItemViewModel.IsSelected))
            UpdateCounts();
    }

    private void UpdateCounts()
    {
        var items = Groups.SelectMany(g => g.Items).ToList();
        TotalCount = items.Count;
        SelectedCount = items.Count(i => i.IsSelected);
    }
}
