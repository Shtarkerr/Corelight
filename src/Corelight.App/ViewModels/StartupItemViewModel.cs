using CommunityToolkit.Mvvm.ComponentModel;
using Corelight.Core.Startup;

namespace Corelight.App.ViewModels;

/// <summary>Программа в автозагрузке. IsEnabled = запускается при входе (как в Диспетчере задач).</summary>
public sealed partial class StartupItemViewModel : ObservableObject
{
    private readonly Action<StartupItemViewModel, bool> _onToggle;
    private bool _suppressToggle;

    public StartupItemViewModel(StartupItem item, Action<StartupItemViewModel, bool> onToggle)
    {
        Item = item;
        _onToggle = onToggle;
        _suppressToggle = true;
        IsEnabled = item.IsEnabled;
        _suppressToggle = false;
    }

    public StartupItem Item { get; }
    public string Title => Item.DisplayName;
    public string Location => Item.Location;
    public string Command => Item.Command;
    public bool IsKnownUpdater => Item.IsKnownUpdater;
    public string StartupHint => Item.Name.Equals("OneDrive", StringComparison.OrdinalIgnoreCase)
        ? "OneDrive начнёт синхронизацию только после запуска." : "Обычно можно отключить";

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    public void SetEnabledSilently(bool value)
    {
        _suppressToggle = true;
        IsEnabled = value;
        _suppressToggle = false;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_suppressToggle)
            _onToggle(this, value);
    }
}
