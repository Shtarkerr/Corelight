using System.Text.Json;
using Microsoft.Win32;
using Corelight.Core.Platform;
using Corelight.Core.Tweaks;

namespace Corelight.Core.Actions;

internal sealed class RegistryWriteDeniedException(string target, Exception inner)
    : UnauthorizedAccessException($"Windows запретила изменение «{target}». Настройка может быть защищена системой или политикой организации. Наличие прав администратора не гарантирует доступ к ней.", inner);

/// <summary>Устанавливает одно значение реестра.</summary>
public sealed class RegistryValueAction : TweakAction
{
    public required string Path { get; init; }

    public required string Name { get; init; }

    public RegistryValueKind Kind { get; init; } = RegistryValueKind.DWord;

    public required string Value { get; init; }

    /// <summary>Значение Windows по умолчанию; null — значения по умолчанию нет (удаляется).</summary>
    public string? Default { get; init; }
    /// <summary>Действующее значение при отсутствии записи. Используется только при обнаружении.</summary>
    public string? EffectiveWhenMissing { get; init; }
    public WindowsComponent? EffectiveWhenMissingRequires { get; init; }

    /// <summary>Не менять настройку отсутствующего компонента AppX. Откат сохранённого значения от этого условия не зависит.</summary>
    public string? RequiredPackage { get; init; }

    public string? RequiredService { get; init; }

    public WindowsComponent? RequiredComponent { get; init; }

    private RegValue Target => new(Kind, Value);

    public override async Task<ActionState> DetectAsync(PlatformServices p, CancellationToken ct)
    {
        if (!await IsApplicableAsync(p, ct)) return ActionState.NotApplicable;
        var current = p.Registry.GetValue(Path, Name);
        if (current is null && EffectiveWhenMissing is not null &&
            (EffectiveWhenMissingRequires is not { } component || await IsComponentPresentAsync(p, component, ct)))
            current = new RegValue(Kind, EffectiveWhenMissing);
        return Target.SameAs(current) ? ActionState.Applied : ActionState.NotApplied;
    }

    private async Task<bool> IsApplicableAsync(PlatformServices p, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (RequiredPackage is not null && !p.Apps.IsInstalled(RequiredPackage)) return false;
        if (RequiredService is not null && p.Services.GetStartType(RequiredService) is null) return false;
        return RequiredComponent is not { } component || await IsComponentPresentAsync(p, component, ct);
    }

    private static Task<bool> IsComponentPresentAsync(PlatformServices p, WindowsComponent component, CancellationToken ct) =>
        p.Components?.IsPresentAsync(component, ct) ?? throw new InvalidOperationException("Проверка компонентов Windows недоступна.");

    public override Task<string?> CaptureAsync(PlatformServices p, CancellationToken ct) =>
        Task.FromResult(Serialize(p.Registry.GetValue(Path, Name)));

    public override async Task ApplyAsync(PlatformServices p, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Компонент мог быть удалён после сканирования. Не создавать для него политику.
        if (!await IsApplicableAsync(p, ct)) return;

        try
        {
            p.Registry.SetValue(Path, Name, Target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new RegistryWriteDeniedException(Describe(), ex);
        }
    }

    public override Task RevertAsync(PlatformServices p, string? captured, CancellationToken ct)
    {
        Restore(p.Registry, Path, Name, Deserialize(captured));
        return Task.CompletedTask;
    }

    public override Task RevertToDefaultAsync(PlatformServices p, CancellationToken ct)
    {
        Restore(p.Registry, Path, Name, Default is null ? null : new RegValue(Kind, Default));
        return Task.CompletedTask;
    }

    public override string Describe() => $@"{Path}\{Name}";

    internal static void Restore(IRegistryAccess registry, string path, string name, RegValue? value)
    {
        if (value is null)
            registry.DeleteValue(path, name);
        else
            registry.SetValue(path, name, value);
    }

    internal static string? Serialize(RegValue? value) => value is null ? null : JsonSerializer.Serialize(value);

    internal static RegValue? Deserialize(string? captured) =>
        captured is null ? null : JsonSerializer.Deserialize<RegValue>(captured);
}
