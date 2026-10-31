using CommunityToolkit.Mvvm.ComponentModel;
using Corelight.Core.Scanning;
using Corelight.Core.SystemInfo;

namespace Corelight.App.Services;

/// <summary>Общее состояние приложения, которое видят все страницы.</summary>
public sealed partial class AppState(Scanner scanner) : ObservableObject
{
    private Task<HardwareProfile>? _hardware;

    [ObservableProperty]
    public partial ScanReport? LastScan { get; set; }

    [ObservableProperty]
    public partial bool RebootRequired { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? BusyText { get; set; }

    /// <summary>Увеличивается после каждого изменения системы, чтобы страницы знали, что пора перечитать состояние.</summary>
    public int ChangeVersion { get; private set; }

    public Task<HardwareProfile> GetHardwareAsync() => _hardware ??= scanner.ProbeHardwareAsync();

    public void MarkSystemChanged() => ChangeVersion++;

    /// <summary>Показывает индикатор занятости. Вложенные вызовы допустимы: индикатор скроется после последнего.</summary>
    public IDisposable BeginBusy(string text)
    {
        _busyDepth++;
        BusyText = text;
        IsBusy = true;
        return new BusyScope(this);
    }

    private int _busyDepth;

    private sealed class BusyScope(AppState state) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (--state._busyDepth > 0)
                return;
            state.IsBusy = false;
            state.BusyText = null;
        }
    }
}
