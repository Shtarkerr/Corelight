using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;
using Corelight.App.Services;
using Corelight.Core.Platform;

namespace Corelight.App.ViewModels;

public sealed record NavItem(string Title, SymbolRegular Icon, object Page, string? ShortTitle = null)
{
    public string DisplayTitle => ShortTitle ?? Title;
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DialogService _dialogs;
    private readonly IProcessRunner _runner;
    private readonly ILogger<MainViewModel> _log;

    public MainViewModel(
        AppState state,
        NavigationService navigation,
        DialogService dialogs,
        IProcessRunner runner,
        ILogger<MainViewModel> log,
        HomeViewModel home,
        ScanResultsViewModel scan,
        AdvancedViewModel advanced,
        HistoryViewModel history,
        CleanupViewModel cleanup)
    {
        State = state;
        _dialogs = dialogs;
        _runner = runner;
        _log = log;
        Items =
        [
            new NavItem("Главная", SymbolRegular.Home24, home),
            new NavItem("Результаты сканирования", SymbolRegular.TaskListLtr24, scan, "Результаты"),
            new NavItem("Расширенное", SymbolRegular.Wrench24, advanced),
            new NavItem("История и откат", SymbolRegular.History24, history),
            new NavItem("Очистка диска", SymbolRegular.Delete24, cleanup),
        ];
        navigation.NavigationRequested += type => SelectedItem = Items.First(i => i.Page.GetType() == type);
    }

    public AppState State { get; }

    public ObservableCollection<NavItem> Items { get; }

    [ObservableProperty]
    public partial NavItem? SelectedItem { get; set; }

    [ObservableProperty]
    public partial object? CurrentPage { get; set; }

    partial void OnSelectedItemChanged(NavItem? value)
    {
        CurrentPage = value?.Page;
        if (value?.Page is INavigationAware page)
            _ = NavigateAsync(page);
    }

    private async Task NavigateAsync(INavigationAware page)
    {
        try
        {
            await page.OnNavigatedToAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка при открытии страницы");
            await _dialogs.ShowInfoAsync("Ошибка", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RebootAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Перезагрузить компьютер?",
            "Сохраните открытые документы. Компьютер перезагрузится сразу после подтверждения.",
            "Перезагрузить");
        if (!confirmed)
            return;

        var result = await _runner.RunAsync(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), ["/r", "/t", "0"], TimeSpan.FromSeconds(30));
        if (!result.Succeeded)
            await _dialogs.ShowInfoAsync("Перезагрузка не запущена", $"Windows вернула код {result.ExitCode}. {result.Error.Trim()}".TrimEnd());
    }

    [RelayCommand]
    private void DismissReboot() => State.RebootRequired = false;
}
