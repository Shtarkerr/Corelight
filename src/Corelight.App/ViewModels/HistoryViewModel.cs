using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Corelight.App.Services;
using Corelight.Core.Actions;
using Corelight.Core.Backup;
using Corelight.Core.Catalog;
using Corelight.Core.Tweaks;

namespace Corelight.App.ViewModels;

public sealed class HistoryItemViewModel(BackupRecord record, TweakDefinition? tweak)
{
    public TweakDefinition? Tweak { get; } = tweak;
    public string Title => record.Title;
    public string? AvailabilityNote => Tweak?.AvailabilityNote;
    public bool HasAvailabilityNote => !string.IsNullOrWhiteSpace(AvailabilityNote);
    public string AppliedAtText => record.AppliedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm");

    // Приложение без ID в Microsoft Store переустановить автоматически нельзя.
    public bool NeedsManualRestore => Tweak?.Actions.OfType<AppxPackageAction>().Any(a => a.StoreId is null) == true;

    public bool CanRevert => Tweak is not null && !NeedsManualRestore;

    public bool CanInteract => Tweak is not null;

    public string RevertButtonText => NeedsManualRestore ? "Проверить возврат" : "Откатить";

    public bool HasRestoreHelp => Tweak?.RestoreHelpUrl is not null;

    public string? StoreId => Tweak?.Actions.OfType<AppxPackageAction>()
        .Select(a => a.StoreId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));

    public bool CanOpenStore => StoreId is not null;

    public string CategoryText => Tweak is null
        ? "Твик больше не входит в каталог"
        : NeedsManualRestore
            ? $"{CategoryInfo.For(Tweak.Category).Title} · требуется ручной возврат"
            : CategoryInfo.For(Tweak.Category).Title;
}

public sealed partial class HistoryViewModel(
    TweakCatalog catalog,
    TweakEngine engine,
    ApplyCoordinator coordinator,
    DialogService dialogs,
    AppState state,
    ILogger<HistoryViewModel> log) : ObservableObject, INavigationAware
{
    public ObservableCollection<HistoryItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevertAllCommand))]
    [NotifyPropertyChangedFor(nameof(HasNoItems))]
    public partial bool HasItems { get; set; }

    public bool HasNoItems => !HasItems;

    public string SummaryText => $"В истории: {Items.Count} · Автоматический откат: {Items.Count(i => i.CanRevert)} · Ручной возврат: {Items.Count(i => !i.CanRevert)}";

    public Task OnNavigatedToAsync()
    {
        Reload();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RevertAsync(HistoryItemViewModel item)
    {
        if (item.Tweak is null)
            return;
        if (item.NeedsManualRestore)
        {
            bool restored;
            using (state.BeginBusy("Проверка возврата приложения…"))
                restored = await Task.Run(() => engine.TryConfirmManualAppRestoreAsync(item.Tweak));
            if (restored)
                state.MarkSystemChanged();
            else
                await dialogs.ShowInfoAsync("Возврат не подтверждён", "Установите приложение по инструкции «Как вернуть» и повторите проверку. Запись остаётся в истории.");
            Reload();
            return;
        }
        await coordinator.RevertAsync([item.Tweak!], showSummary: false);
        Reload();
    }

    [RelayCommand(CanExecute = nameof(HasItems))]
    private async Task RevertAllAsync()
    {
        var tweaks = Items.Where(i => i.CanRevert).Select(i => i.Tweak!).ToList();
        var manualItems = Items.Where(i => !i.CanRevert).ToList();
        var message = $"Записей в истории: {Items.Count}.\nБудет выполнен автоматический откат: {tweaks.Count}.";
        if (tweaks.Any(t => t.Actions.OfType<AppxPackageAction>().Any()))
            message += "\n\nДля переустановки приложений из Store нужен интернет.";
        if (manualItems.Count > 0)
        {
            message += $"\n\nРучной возврат: {manualItems.Count}. Останутся в истории:";
            foreach (var item in manualItems)
                message += $"\n• {item.Title} — {item.CategoryText}";
        }
        if (tweaks.Count == 0)
        {
            await dialogs.ShowInfoAsync("Автоматический откат недоступен", message);
            return;
        }
        var confirmed = await dialogs.ConfirmAsync(
            "Откатить доступные изменения?",
            message,
            "Откатить");
        if (!confirmed)
            return;

        await coordinator.RevertAsync(tweaks, showSummary: true);
        Reload();
    }

    [RelayCommand]
    private async Task ShowRestoreHelpAsync(HistoryItemViewModel item)
    {
        if (item.Tweak?.RestoreHelpUrl is not { } url)
            return;
        var choice = await dialogs.ShowAsync($"Как вернуть {item.Title}",
            item.Tweak.RestoreHelpText ?? "Установите приложение с официальной страницы, затем нажмите «Проверить возврат».",
            "Открыть страницу", "Отмена");
        if (choice != DialogChoice.Primary)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Не удалось открыть инструкцию возврата {Tweak}", item.Tweak.Id);
            await dialogs.ShowInfoAsync("Страница не открылась", "Не удалось открыть официальную страницу. Проверьте подключение к интернету и повторите попытку.");
        }
    }

    [RelayCommand]
    private async Task OpenStoreAsync(HistoryItemViewModel item)
    {
        if (item.StoreId is not { } storeId)
            return;
        try
        {
            Process.Start(new ProcessStartInfo($"ms-windows-store://pdp/?ProductId={Uri.EscapeDataString(storeId)}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Не удалось открыть страницу Store для {Tweak}", item.Tweak?.Id);
            await dialogs.ShowInfoAsync("Microsoft Store не открылся", "Откройте Microsoft Store вручную и найдите приложение по названию. После установки повторите откат в Corelight.");
        }
    }

    [RelayCommand]
    private async Task OpenSystemRestoreAsync()
    {
        try
        {
            Process.Start(new ProcessStartInfo("rstrui.exe") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Не удалось открыть восстановление системы");
            await dialogs.ShowInfoAsync("Ошибка", "Не удалось открыть «Восстановление системы». Запустите rstrui.exe вручную.");
        }
    }

    private void Reload()
    {
        Items.Clear();
        foreach (var record in engine.Backups.GetAll())
            Items.Add(new HistoryItemViewModel(record, catalog.Find(record.TweakId)));
        HasItems = Items.Count > 0;
        OnPropertyChanged(nameof(SummaryText));
    }
}
