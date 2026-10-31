using Microsoft.Win32;
using Corelight.Core.Platform;
using Corelight.Core.Tweaks;

namespace Corelight.Core.Actions;

/// <summary>
/// Меняет одну пару "ключ=значение" в строке вида "A=1;B=0;" и не трогает остальные пары.
/// Нужно, например, для DirectXUserGlobalSettings, где рядом лежат настройки Auto HDR и VRR.
/// </summary>
public sealed class RegistryListEntryAction : TweakAction
{
    public required string Path { get; init; }

    public required string Name { get; init; }

    public required string Key { get; init; }

    public required string Value { get; init; }

    public override Task<ActionState> DetectAsync(PlatformServices p, CancellationToken ct)
    {
        var entries = Parse(p.Registry.GetValue(Path, Name)?.Data);
        var applied = entries.TryGetValue(Key, out var v) && v == Value;
        return Task.FromResult(applied ? ActionState.Applied : ActionState.NotApplied);
    }

    public override Task<string?> CaptureAsync(PlatformServices p, CancellationToken ct) =>
        Task.FromResult(RegistryValueAction.Serialize(p.Registry.GetValue(Path, Name)));

    public override Task ApplyAsync(PlatformServices p, CancellationToken ct)
    {
        var entries = Parse(p.Registry.GetValue(Path, Name)?.Data);
        entries[Key] = Value;
        p.Registry.SetValue(Path, Name, new RegValue(RegistryValueKind.String, Format(entries)));
        return Task.CompletedTask;
    }

    public override Task RevertAsync(PlatformServices p, string? captured, CancellationToken ct)
    {
        RegistryValueAction.Restore(p.Registry, Path, Name, RegistryValueAction.Deserialize(captured));
        return Task.CompletedTask;
    }

    public override Task RevertToDefaultAsync(PlatformServices p, CancellationToken ct)
    {
        var entries = Parse(p.Registry.GetValue(Path, Name)?.Data);
        if (!entries.Remove(Key))
            return Task.CompletedTask;

        if (entries.Count == 0)
            p.Registry.DeleteValue(Path, Name);
        else
            p.Registry.SetValue(Path, Name, new RegValue(RegistryValueKind.String, Format(entries)));
        return Task.CompletedTask;
    }

    public override string Describe() => $@"{Path}\{Name} [{Key}]";

    // Порядок пар сохраняется, чтобы не менять строку сильнее, чем нужно.
    internal static OrderedDictionary<string, string> Parse(string? data)
    {
        var result = new OrderedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(data))
            return result;

        foreach (var part in data.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0)
                result[part[..eq]] = part[(eq + 1)..];
        }
        return result;
    }

    internal static string Format(OrderedDictionary<string, string> entries) =>
        string.Concat(entries.Select(e => $"{e.Key}={e.Value};"));
}
