using Microsoft.Win32;
using System.Text.Json;
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

    public override Task<string?> CaptureAsync(PlatformServices p, CancellationToken ct)
    {
        var value = p.Registry.GetValue(Path, Name);
        if (value is not null && value.Kind != RegistryValueKind.String)
            throw new InvalidOperationException($"Ожидалась строка настроек в {Path}\\{Name}.");
        var entry = Parse(value?.Data).GetValueOrDefault(Key);
        return Task.FromResult(entry is null ? null : JsonSerializer.Serialize(entry));
    }

    private string? OriginalEntry(string? captured) => captured is null ? null
        : captured.StartsWith('{') ? Parse(RegistryValueAction.Deserialize(captured)?.Data).GetValueOrDefault(Key)
        : JsonSerializer.Deserialize<string>(captured);

    public override bool CapturedStateMatches(string? current, string? original) => OriginalEntry(current) == OriginalEntry(original);

    public override Task ApplyAsync(PlatformServices p, CancellationToken ct)
    {
        var entries = Parse(p.Registry.GetValue(Path, Name)?.Data);
        entries[Key] = Value;
        p.Registry.SetValue(Path, Name, new RegValue(RegistryValueKind.String, Format(entries)));
        return Task.CompletedTask;
    }

    public override Task RevertAsync(PlatformServices p, string? captured, CancellationToken ct)
    {
        var entries = Parse(p.Registry.GetValue(Path, Name)?.Data);
        var original = OriginalEntry(captured);
        if (original is null) entries.Remove(Key);
        else entries[Key] = original;
        if (entries.Count == 0) p.Registry.DeleteValue(Path, Name);
        else p.Registry.SetValue(Path, Name, RegValue.String(Format(entries)));
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
