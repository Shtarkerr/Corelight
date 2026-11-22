using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Input;
using Corelight.Core.Scanning;
using Corelight.Core.SystemInfo;

namespace Corelight.App.Services;

/// <summary>Общее состояние приложения, которое видят все страницы.</summary>
public sealed partial class AppState(Scanner scanner) : ObservableObject
{
    private Task<HardwareProfile>? _hardware;
    private int _lastScanVersion;

    public ScanReport? LastScan { get; private set; }
    public int UserScanVersion { get; private set; }
    public bool NeedsScanRefresh => LastScan is not null && _lastScanVersion != ChangeVersion;

    [ObservableProperty]
    public partial bool RebootRequired { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? BusyText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancelBusy))]
    public partial ICommand? BusyCancelCommand { get; set; }

    public bool CanCancelBusy => BusyCancelCommand is not null;

    /// <summary>Увеличивается после каждого изменения системы, чтобы страницы знали, что пора перечитать состояние.</summary>
    public int ChangeVersion { get; private set; }

    public Task<HardwareProfile> GetHardwareAsync() =>
        _hardware is null || _hardware.IsFaulted || _hardware.IsCanceled ? RefreshHardwareAsync() : _hardware;

    public Task<HardwareProfile> RefreshHardwareAsync() => _hardware = scanner.ProbeHardwareAsync();

    public void SetUserScan(ScanReport report)
    {
        UserScanVersion++;
        LastScan = report;
        _lastScanVersion = ChangeVersion;
    }

    public void ClearLastScan() => LastScan = null;

    public async Task<ScanReport> RefreshTweakStatusesAsync()
    {
        var version = ChangeVersion;
        var userScanVersion = UserScanVersion;
        var report = await scanner.ScanAsync(await GetHardwareAsync());
        if (version == ChangeVersion && userScanVersion == UserScanVersion && LastScan is { } previous)
        {
            if (!previous.Statuses.SequenceEqual(report.Statuses))
                LastScan = report;
            _lastScanVersion = version;
        }
        return report;
    }

    public void MarkSystemChanged() => ChangeVersion++;

    /// <summary>Показывает индикатор занятости. Вложенные вызовы допустимы: индикатор скроется после последнего.</summary>
    public IDisposable BeginBusy(string text, Action? cancel = null)
    {
        _busyDepth++;
        BusyText = text;
        IsBusy = true;
        var previous = BusyCancelCommand;
        BusyCancelCommand = cancel is null ? null : new RelayCommand(cancel);
        return new BusyScope(this, previous);
    }

    private int _busyDepth;

    private sealed class BusyScope(AppState state, ICommand? previousCancel) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            state.BusyCancelCommand = previousCancel;
            if (--state._busyDepth > 0)
                return;
            state.IsBusy = false;
            state.BusyText = null;
        }
    }
}
