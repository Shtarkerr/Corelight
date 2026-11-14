using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Corelight.App.Services;
using Corelight.Core.Catalog;
using Corelight.Core.Scanning;
using Corelight.Core.Startup;
using Corelight.Core.Tweaks;

namespace Corelight.App.ViewModels;

public sealed class AdvancedSectionViewModel(CategoryInfo info)
{
    public CategoryInfo Info { get; } = info;
    public string Title => Info.Title;
    public ObservableCollection<object> Items { get; } = [];
    public bool IsEmpty => Items.Count == 0;

    public string Hint => Info.Category switch
    {
        null => "Включённый переключатель — программа запускается вместе с Windows. Сама программа не удаляется.",
        TweakCategory.Apps => "Показаны только приложения, которые есть в системе или были удалены этой программой.",
        _ => "Включённый переключатель — оптимизация применена. Выключите его, чтобы вернуть как было.",
    };

    public string EmptyText => Info.Category == TweakCategory.Apps
        ? "Предустановленных приложений из списка в системе нет."
        : "В этом разделе ничего не найдено.";
}

public sealed partial class AdvancedViewModel(
    TweakCatalog catalog,
    TweakEngine engine,
    Scanner scanner,
    StartupManager startup,
    ApplyCoordinator coordinator,
    AppState state,
    DialogService dialogs,
    ILogger<AdvancedViewModel> log) : ObservableObject, INavigationAware
{
    private int _renderedVersion = -1;

    public ObservableCollection<AdvancedSectionViewModel> Sections { get; } = [];

    [ObservableProperty]
    public partial AdvancedSectionViewModel? SelectedSection { get; set; }

    public async Task OnNavigatedToAsync()
    {
        if (_renderedVersion == state.ChangeVersion)
            return;

        try
        {
            using (state.BeginBusy("Проверка текущих настроек…"))
                await LoadAsync();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Не удалось загрузить раздел «Расширенное»");
            await dialogs.ShowInfoAsync("Ошибка", ex.Message);
        }
    }

    private async Task LoadAsync()
    {
        engine.RefreshExternalState();
        var hardware = await state.GetHardwareAsync();
        var selectedTitle = SelectedSection?.Title;
        var sections = new List<AdvancedSectionViewModel>();

        foreach (var info in CategoryInfo.All)
        {
            var section = new AdvancedSectionViewModel(info);
            if (info.Category is { } category)
            {
                var items = new List<TweakItemViewModel>();
                foreach (var tweak in catalog.InCategory(category))
                {
                    var status = await scanner.EvaluateAsync(tweak, hardware);
                    if (category == TweakCategory.Apps && status.State == TweakState.Applied && engine.Backups.Get(tweak.Id) is null)
                        continue; // приложения нет и не было удалено нами — показывать нечего

                    TweakItemViewModel item = category == TweakCategory.Apps
                        ? new AppItemViewModel(tweak, OnToggleAsync)
                        : new TweakItemViewModel(tweak, OnToggleAsync);
                    item.SetStatus(status);
                    items.Add(item);
                }
                // Недоступные на этом ПК пункты — в конец списка.
                foreach (var item in items.OrderBy(i => !i.IsAvailable))
                    section.Items.Add(item);
            }
            else
            {
                var items = await Task.Run(startup.GetItems);
                foreach (var item in items)
                    section.Items.Add(new StartupItemViewModel(item, OnStartupToggle));
            }
            sections.Add(section);
        }

        Sections.Clear();
        foreach (var section in sections)
            Sections.Add(section);
        SelectedSection = Sections.FirstOrDefault(s => s.Title == selectedTitle) ?? Sections.FirstOrDefault();
        _renderedVersion = state.ChangeVersion;
    }

    private async Task OnToggleAsync(TweakItemViewModel item, bool turnOn)
    {
        item.IsBusy = true;
        try
        {
            if (turnOn && !await ConfirmAsync(item))
                return;

            var tweaks = new[] { item.Tweak };
            if (turnOn)
                await coordinator.ApplyAsync(tweaks, showSummary: false);
            else
                await coordinator.RevertAsync(tweaks, showSummary: false);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Ошибка переключения твика {Tweak}", item.Tweak.Id);
            await dialogs.ShowInfoAsync("Ошибка", ex.Message);
        }
        finally
        {
            // Показываем то, что на самом деле в системе, а не то, на что нажали. Твики той же
            // взаимоисключающей группы тоже перечитываем: применение одного откатывает другой.
            var group = item.Tweak.ExclusiveGroup;
            IReadOnlyList<TweakItemViewModel> affected = group is null
                ? [item]
                : Sections.SelectMany(s => s.Items).OfType<TweakItemViewModel>().Where(i => i.Tweak.ExclusiveGroup == group).ToList();
            foreach (var target in affected)
            {
                try
                {
                    target.SetStatus(await scanner.EvaluateAsync(target.Tweak, await state.GetHardwareAsync()));
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Не удалось перечитать состояние {Tweak}", target.Tweak.Id);
                }
            }
            item.IsBusy = false;
            _renderedVersion = state.ChangeVersion;
        }
    }

    private async Task<bool> ConfirmAsync(TweakItemViewModel item)
    {
        var isApp = item is AppItemViewModel;
        if (!item.HasWarning && item.Risk != TweakRisk.Advanced && !isApp)
            return true;

        var text = isApp ? $"Приложение «{item.Title}» будет удалено для всех пользователей этого ПК." : item.Description;
        if (item.HasWarning)
            text += "\n\n" + item.Warning;
        if (item.HasAvailabilityNote)
            text += "\n\n" + item.AvailabilityNote;
        return await dialogs.ConfirmAsync(isApp ? "Удалить приложение?" : item.Title, text, isApp ? "Удалить" : "Применить");
    }

    private void OnStartupToggle(StartupItemViewModel item, bool enabled)
    {
        try
        {
            startup.SetEnabled(item.Item, enabled);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Не удалось изменить автозагрузку {Item}", item.Item.Name);
            item.SetEnabledSilently(!enabled);
            _ = dialogs.ShowInfoAsync("Ошибка", $"Не удалось изменить автозагрузку «{item.Title}»: {ex.Message}");
        }
    }
}
