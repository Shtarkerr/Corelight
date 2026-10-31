using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Corelight.App.Services;
using Corelight.Core.Scanning;
using Corelight.Core.SystemInfo;
using Corelight.Core.Tweaks;

namespace Corelight.App.ViewModels;

public sealed partial class HomeViewModel(
    AppState state,
    Scanner scanner,
    NavigationService navigation,
    DialogService dialogs,
    ILogger<HomeViewModel> log) : ObservableObject, INavigationAware
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuText), nameof(GpuText), nameof(RamText), nameof(OsText), nameof(DiskText), nameof(FormFactorText), nameof(HasHardware))]
    public partial HardwareProfile? Hardware { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScanSummary))]
    public partial string? ScanSummary { get; set; }

    public bool HasScanSummary => ScanSummary is not null;
    public bool HasHardware => Hardware is not null;
    public string CpuText => Hardware?.CpuName ?? "";
    public string GpuText => Hardware is null ? "" : string.Join(", ", Hardware.Gpus.DefaultIfEmpty("Неизвестно"));
    public string RamText => Hardware is null ? "" : $"{Math.Round(Hardware.TotalMemoryBytes / 1024d / 1024 / 1024)} ГБ";
    public string OsText => Hardware is null ? "" : $"{Hardware.OsName} {Hardware.OsDisplayVersion} (сборка {Hardware.OsBuild}.{Hardware.OsRevision})";
    public string FormFactorText => Hardware is null ? "" : Hardware.HasBattery ? "Ноутбук" : "Настольный ПК";

    public string DiskText => Hardware?.SystemDisk switch
    {
        DiskKind.Ssd => "SSD",
        DiskKind.Hdd => "HDD",
        null => "",
        _ => "Не определён",
    };

    public async Task OnNavigatedToAsync()
    {
        try
        {
            Hardware ??= await state.GetHardwareAsync();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Не удалось получить сведения о системе");
        }
        UpdateSummary();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        try
        {
            using (state.BeginBusy("Сбор сведений о системе…"))
            {
                Hardware = await state.GetHardwareAsync();
                var progress = new Progress<double>(p => state.BusyText = $"Сканирование системы… {p:P0}");
                state.LastScan = await scanner.ScanAsync(Hardware, progress);
            }
            UpdateSummary();
            navigation.NavigateTo<ScanResultsViewModel>();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Ошибка сканирования");
            await dialogs.ShowInfoAsync("Ошибка сканирования", ex.Message);
        }
    }

    private void UpdateSummary()
    {
        var report = state.LastScan;
        if (report is null)
        {
            ScanSummary = null;
            return;
        }
        var suggestions = report.Suggestions.Count();
        // Приложения не считаем: «применено» у них значит и «никогда не было в системе».
        var applied = report.Statuses.Count(s =>
            s.Recommended && s.IsAvailable && s.State == TweakState.Applied && s.Tweak.Category != TweakCategory.Apps);
        ScanSummary = suggestions == 0
            ? "Рекомендаций нет — всё рекомендованное уже применено."
            : $"Найдено рекомендаций: {suggestions}. Настроек уже в порядке: {applied}.";
    }
}
