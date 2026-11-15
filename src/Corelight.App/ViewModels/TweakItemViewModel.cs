using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Corelight.Core.Actions;
using Corelight.Core.Scanning;
using Corelight.Core.Tweaks;
using Corelight.App.Services;

namespace Corelight.App.ViewModels;

/// <summary>Общие тексты для карточки твика.</summary>
public abstract class TweakCardViewModel(TweakDefinition tweak) : ObservableObject
{
    public TweakDefinition Tweak { get; } = tweak;
    public string Title => Tweak.Title;
    public string Description => Tweak.Description;
    public string? AvailabilityNote => Tweak.AvailabilityNote;
    public bool HasAvailabilityNote => !string.IsNullOrWhiteSpace(AvailabilityNote);
    public string? Warning => Tweak.Warning;
    public bool HasWarning => !string.IsNullOrEmpty(Tweak.Warning);
    public TweakRisk Risk => Tweak.Risk;
    public bool RequiresReboot => Tweak.RequiresReboot;
    public TweakImpact Impact => Tweak.Impact;
    public bool HasNotableImpact => Impact is TweakImpact.High or TweakImpact.Medium;
    public string CategoryText => CategoryInfo.For(Tweak.Category).Title;

    public string RiskText => Tweak.Risk switch
    {
        TweakRisk.Safe => "Безопасно",
        TweakRisk.Caution => "Осторожно",
        _ => "Для опытных",
    };

    public string ImpactText => Tweak.Impact switch
    {
        TweakImpact.High => "Эффект: высокий",
        TweakImpact.Medium => "Эффект: средний",
        _ => "Эффект: низкий",
    };
}

/// <summary>Твик в разделе «Расширенное». IsOn = оптимизация применена.</summary>
public partial class TweakItemViewModel(TweakDefinition tweak, Func<TweakItemViewModel, bool, Task> onToggle) : TweakCardViewModel(tweak)
{
    private bool _suppressToggle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable), nameof(CanToggle))]
    public partial string? UnavailableReason { get; set; }

    public bool IsAvailable => UnavailableReason is null;

    public virtual bool CanToggle => IsAvailable && !IsBusy;

    /// <summary>Показывает фактическое состояние, не запуская применение или откат.</summary>
    public void SetStatus(TweakStatus status)
    {
        _suppressToggle = true;
        IsOn = status.State == TweakState.Applied;
        _suppressToggle = false;
        UnavailableReason = status.UnavailableReason
            ?? (status.State == TweakState.NotApplicable ? "Компонентов этого твика нет в вашей системе." : null);
        OnStatusChanged();
    }

    protected virtual void OnStatusChanged() { }

    partial void OnIsOnChanged(bool value)
    {
        OnStatusChanged();
        if (!_suppressToggle)
            _ = onToggle(this, value);
    }
}

/// <summary>Приложение в разделе «Приложения»: вместо переключателя кнопка «Удалить» / «Вернуть».</summary>
public sealed partial class AppItemViewModel(TweakDefinition tweak, Func<TweakItemViewModel, bool, Task> onToggle)
    : TweakItemViewModel(tweak, onToggle)
{
    public bool IsRemoved => IsOn;

    public string StatusText => IsOn ? "Удалено" : "Установлено";

    public string ActionText => IsOn ? "Вернуть" : "Удалить";

    public bool CanRestore => Tweak.Actions.OfType<AppxPackageAction>().All(a => a.StoreId is not null);

    public override bool CanToggle => base.CanToggle && (!IsOn || CanRestore);

    public string? ActionHint => IsOn && !CanRestore ? "Вернуть можно только вручную через Microsoft Store" : null;

    [RelayCommand]
    private void Toggle() => IsOn = !IsOn;

    protected override void OnStatusChanged()
    {
        foreach (var name in (string[])[nameof(IsRemoved), nameof(StatusText), nameof(ActionText), nameof(CanToggle), nameof(ActionHint)])
            OnPropertyChanged(name);
    }
}

/// <summary>Рекомендация в результатах сканирования.</summary>
public sealed partial class ScanItemViewModel : TweakCardViewModel
{
    public ScanItemViewModel(TweakStatus status) : base(status.Tweak) => IsSelected = status.IsPreselected;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
