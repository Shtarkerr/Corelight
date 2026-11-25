using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

public sealed partial class AdvancedTweakItemViewModel(TweakDefinition tweak, Func<AdvancedTweakItemViewModel, Task> apply)
    : TweakCardViewModel(tweak)
{
    public string ActionText => Tweak.Category switch
    {
        TweakCategory.Apps => "Удалить",
        TweakCategory.Services => "Отключить",
        _ => "Применить",
    };

    [RelayCommand]
    private Task ApplyAsync() => apply(this);
}

/// <summary>Карточка пункта в результатах сканирования.</summary>
public sealed partial class ScanItemViewModel : TweakCardViewModel
{
    public ScanItemViewModel(TweakStatus status, bool? selected = null) : base(status.Tweak) =>
        IsSelected = selected ?? status.IsPreselected;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
