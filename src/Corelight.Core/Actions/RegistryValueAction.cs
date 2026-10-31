using System.Text.Json;
using Microsoft.Win32;
using Corelight.Core.Platform;
using Corelight.Core.Tweaks;

namespace Corelight.Core.Actions;

/// <summary>Устанавливает одно значение реестра.</summary>
public sealed class RegistryValueAction : TweakAction
{
    public required string Path { get; init; }

    public required string Name { get; init; }

    public RegistryValueKind Kind { get; init; } = RegistryValueKind.DWord;

    public required string Value { get; init; }

    /// <summary>Значение Windows по умолчанию; null — значения по умолчанию нет (удаляется).</summary>
    public string? Default { get; init; }

    private RegValue Target => new(Kind, Value);

    public override Task<ActionState> DetectAsync(PlatformServices p, CancellationToken ct) =>
        Task.FromResult(Target.SameAs(p.Registry.GetValue(Path, Name)) ? ActionState.Applied : ActionState.NotApplied);

    public override Task<string?> CaptureAsync(PlatformServices p, CancellationToken ct) =>
        Task.FromResult(Serialize(p.Registry.GetValue(Path, Name)));

    public override Task ApplyAsync(PlatformServices p, CancellationToken ct)
    {
        p.Registry.SetValue(Path, Name, Target);
        return Task.CompletedTask;
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
