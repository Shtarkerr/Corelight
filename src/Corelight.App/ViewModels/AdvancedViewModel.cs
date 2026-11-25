using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Corelight.App.Services;
using Corelight.Core.Actions;
using Corelight.Core.Scanning;
using Corelight.Core.Startup;
using Corelight.Core.Tweaks;

namespace Corelight.App.ViewModels;

public sealed record AdvancedSectionViewModel(CategoryInfo Info, IReadOnlyList<AdvancedTweakItemViewModel> Items)
{
    public string Title => Info.Title;
    public bool IsEmpty => Items.Count == 0;
}

public sealed partial class AdvancedViewModel(
    StartupManager startup,
    ApplyCoordinator coordinator,
    AppState state,
    DialogService dialogs,
    ILogger<AdvancedViewModel> log) : ObservableObject, INavigationAware
{
    public ObservableCollection<AdvancedSectionViewModel> Sections { get; } = [];

    [ObservableProperty]
    public partial AdvancedSectionViewModel? SelectedSection { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoStartupItems))]
    public partial IReadOnlyList<StartupItemViewModel> StartupItems { get; set; } = [];

    public bool HasNoStartupItems => StartupItems.Count == 0;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasStartupWarning))]
    public partial string? StartupWarning { get; set; }
    public bool HasStartupWarning => StartupWarning is not null;
    [ObservableProperty] public partial string? TweakWarning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPendingOptions), nameof(ShowsStartup))]
    public partial int SelectedBlockIndex { get; set; }

    public bool ShowsPendingOptions => SelectedBlockIndex == 0;
    public bool ShowsStartup => SelectedBlockIndex == 1;

    public async Task OnNavigatedToAsync()
    {
        try
        {
            using (state.BeginBusy("Проверка текущих настроек…"))
            {
                await LoadTweakSectionsAsync();
                var startupScan = await Task.Run(startup.GetItems);
                StartupItems = startupScan.Items.Select(item => new StartupItemViewModel(item, OnStartupToggle)).ToArray();
                StartupWarning = startupScan.Problems.Count == 0 ? null : "Некоторые записи автозагрузки недоступны.";
                foreach (var problem in startupScan.Problems)
                    log.LogWarning("Неполный список автозагрузки: {Problem}", problem);
            }
        }
        catch (Exception ex)
        {
            StartupItems = [];
            StartupWarning = "Не удалось обновить автозагрузку.";
            log.LogError(ex, "Не удалось загрузить раздел «Расширенное»");
            await dialogs.ShowInfoAsync("Ошибка", ex.Message);
        }
    }

    private async Task LoadTweakSectionsAsync()
    {
        ScanReport report;
        try
        {
            report = await state.RefreshTweakStatusesAsync();
        }
        catch
        {
            Sections.Clear();
            SelectedSection = null;
            TweakWarning = null;
            throw;
        }
        var pending = report.Statuses.Where(s => s.CanApply).OrderByDescending(s => s.Tweak.Impact)
            .ToLookup(s => s.Tweak.Category);
        TweakWarning = report.UnknownItems.Count > 0 ? $"Не удалось проверить настроек: {report.UnknownItems.Count}." : null;
        var selectedCategory = SelectedSection?.Info.Category;

        Sections.Clear();
        foreach (var info in CategoryInfo.All)
        {
            var items = pending[info.Category].Select(s => new AdvancedTweakItemViewModel(s.Tweak, ApplyAsync)).ToArray();
            Sections.Add(new AdvancedSectionViewModel(info, items));
        }
        SelectedSection = Sections.FirstOrDefault(s => s.Info.Category == selectedCategory)
            ?? Sections.FirstOrDefault(s => !s.IsEmpty)
            ?? Sections.FirstOrDefault();
    }

    private async Task ApplyAsync(AdvancedTweakItemViewModel item)
    {
        try
        {
            if (!await ConfirmAsync(item))
                return;

            var version = state.ChangeVersion;
            await coordinator.ApplyAsync([item.Tweak], showSummary: false);
            if (state.ChangeVersion != version)
            {
                using (state.BeginBusy("Обновление списка…"))
                    await LoadTweakSectionsAsync();
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Не удалось применить твик {Tweak}", item.Tweak.Id);
            await dialogs.ShowInfoAsync("Ошибка", ex.Message);
        }
    }

    private async Task<bool> ConfirmAsync(AdvancedTweakItemViewModel item)
    {
        var isApp = item.Tweak.Category == TweakCategory.Apps;
        if (!item.HasWarning && item.Risk != TweakRisk.Advanced && !isApp)
            return true;

        var text = isApp
            ? $"Приложение «{item.Title}» будет удалено."
            : item.Description;
        if (item.Tweak.Actions.OfType<AppxPackageAction>().Any())
            text += " Перед удалением Corelight проверит доступность через Microsoft Store. Изменение затронет всех пользователей этого ПК.";
        if (item.HasWarning)
            text += "\n\n" + item.Warning;
        if (item.HasAvailabilityNote)
            text += "\n\n" + item.AvailabilityNote;
        return await dialogs.ConfirmAsync(isApp ? "Удалить приложение?" : item.Title, text, item.ActionText);
    }

    private void OnStartupToggle(StartupItemViewModel item, bool enabled)
    {
        try
        {
            var actual = startup.SetEnabled(item.Item, enabled);
            if (actual != enabled)
            {
                item.SetEnabledSilently(actual);
                _ = dialogs.ShowInfoAsync("Изменение не подтверждено", $"Windows не подтвердила новый режим автозагрузки «{item.Title}».");
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Не удалось изменить автозагрузку {Item}", item.Item.Name);
            item.SetEnabledSilently(!enabled);
            _ = dialogs.ShowInfoAsync("Ошибка", $"Не удалось изменить автозагрузку «{item.Title}»: {ex.Message}");
        }
    }
}
