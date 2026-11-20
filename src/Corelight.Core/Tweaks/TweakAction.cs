using System.Text.Json.Serialization;
using Corelight.Core.Actions;
using Corelight.Core.Platform;

namespace Corelight.Core.Tweaks;

public enum ActionState
{
    /// <summary>Цели действия нет в системе (служба, задача, пакет отсутствуют).</summary>
    NotApplicable,
    NotApplied,
    Applied,
}

/// <summary>
/// Одно атомарное изменение системы. Каждое действие умеет определить своё состояние,
/// сохранить исходное значение, применить изменение и вернуть всё обратно.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RegistryValueAction), "registry")]
[JsonDerivedType(typeof(RegistryListEntryAction), "registryListEntry")]
[JsonDerivedType(typeof(ServiceStartupAction), "service")]
[JsonDerivedType(typeof(ScheduledTaskAction), "task")]
[JsonDerivedType(typeof(AppxPackageAction), "appx")]
[JsonDerivedType(typeof(CopilotDesktopAction), "copilotDesktop")]
[JsonDerivedType(typeof(OneDriveAction), "oneDrive")]
[JsonDerivedType(typeof(PowerPlanAction), "powerPlan")]
[JsonDerivedType(typeof(TcpNoDelayAction), "tcpNoDelay")]
public abstract class TweakAction
{
    /// <summary>Ошибка такого действия записывается в журнал, но не отменяет весь твик.</summary>
    public bool Optional { get; init; }

    public abstract Task<ActionState> DetectAsync(PlatformServices p, CancellationToken ct);

    /// <summary>Снимок текущего состояния для бэкапа.</summary>
    public abstract Task<string?> CaptureAsync(PlatformServices p, CancellationToken ct);

    /// <summary>Сравнивает снимки, в том числе совместимые форматы ранних версий.</summary>
    public virtual bool CapturedStateMatches(string? current, string? original) => current == original;

    public abstract Task ApplyAsync(PlatformServices p, CancellationToken ct);

    /// <summary>Возвращает состояние из снимка, сделанного <see cref="CaptureAsync"/>.</summary>
    public abstract Task RevertAsync(PlatformServices p, string? captured, CancellationToken ct);

    /// <summary>Человекочитаемое описание цели для журнала и валидатора.</summary>
    public abstract string Describe();
}
