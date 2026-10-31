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
    public string AppliedAtText => record.AppliedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm");

    // Приложение без ID в Microsoft Store переустановить автоматически нельзя.
    private bool NeedsManualRestore => Tweak?.Actions.OfType<AppxPackageAction>().Any(a => a.StoreId is null) == true;

    public bool CanRevert => Tweak is not null && !NeedsManualRestore;

    public string CategoryText => Tweak is null
        ? "Твик больше не входит в каталог"
        : NeedsManualRestore
            ? $"{CategoryInfo.For(Tweak.Category).Title} · вернуть можно только вручную через Microsoft Store"
            : CategoryInfo.For(Tweak.Category).Title;
}

public sealed partial class HistoryViewModel(
    TweakCatalog catalog,
    TweakEngine engine,
    ApplyCoordinator coordinator,
    DialogService dialogs,
    ILogger<HistoryViewModel> log) : ObservableObject, INavigationAware
{
    public ObservableCollection<HistoryItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevertAllCommand))]
    [NotifyPropertyChangedFor(nameof(HasNoItems))]
    public partial bool HasItems { get; set; }

    public bool HasNoItems => !HasItems;

    public Task OnNavigatedToAsync()
    {
        Reload();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RevertAsync(HistoryItemViewModel item)
    {
        if (!item.CanRevert)
            return;
        await coordinator.RevertAsync([item.Tweak!], showSummary: false);
        Reload();
    }

    [RelayCommand(CanExecute = nameof(HasItems))]
    private async Task RevertAllAsync()
    {
        var tweaks = Items.Where(i => i.CanRevert).Select(i => i.Tweak!).ToList();
        var confirmed = await dialogs.ConfirmAsync(
            "Откатить всё?",
            $"Все изменения ({tweaks.Count}) будут возвращены к состоянию до оптимизации. Удалённые приложения будут заново установлены из Microsoft Store — для этого нужен интернет.",
            "Откатить всё");
        if (!confirmed)
            return;

        await coordinator.RevertAsync(tweaks, showSummary: true);
        Reload();
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
    }
}
