using Microsoft.Win32;
using System.Text.Json;
using Corelight.Core.Platform;
using Corelight.Core.Tweaks;

namespace Corelight.Core.Actions;

/// <summary>Меняет только последнюю действующую пару «ключ=значение» в строке настроек.</summary>
public sealed class RegistryListEntryAction : TweakAction
{
    private sealed record Snapshot(bool Exists, string? Segment, bool HasContent = false);

    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Key { get; init; }
    public required string Value { get; init; }

    public override Task<ActionState> DetectAsync(PlatformServices p, CancellationToken ct)
    {
        var snapshot = ReadSnapshot(p);
        var value = snapshot.Segment is { } segment ? segment.Trim()[(segment.Trim().IndexOf('=') + 1)..] : null;
        return Task.FromResult(value == Value ? ActionState.Applied : ActionState.NotApplied);
    }

    public override Task<string?> CaptureAsync(PlatformServices p, CancellationToken ct) =>
        Task.FromResult<string?>(JsonSerializer.Serialize(ReadSnapshot(p)));

    public override bool CapturedStateMatches(string? current, string? original)
    {
        var now = Parse(current);
        var before = Parse(original);
        return now.Segment == before.Segment
            && (now.Exists == before.Exists || !before.Exists && now.HasContent);
    }

    public override Task ApplyAsync(PlatformServices p, CancellationToken ct)
    {
        var segments = ReadSegments(p);
        var index = segments.FindLastIndex(IsTarget);
        if (index >= 0)
            segments[index] = segments[index][..(segments[index].IndexOf('=') + 1)] + Value;
        else
            Insert(segments, $"{Key}={Value}");
        p.Registry.SetValue(Path, Name, RegValue.String(string.Join(';', segments)));
        return Task.CompletedTask;
    }

    public override Task RevertAsync(PlatformServices p, string? captured, CancellationToken ct)
    {
        var before = Parse(captured);
        var segments = ReadSegments(p);
        var index = segments.FindLastIndex(IsTarget);
        if (before.Segment is null)
        {
            if (index >= 0) segments.RemoveAt(index);
        }
        else if (index >= 0)
            segments[index] = before.Segment;
        else
            Insert(segments, before.Segment);

        var result = string.Join(';', segments);
        if (result.Length == 0 && !before.Exists) p.Registry.DeleteValue(Path, Name);
        else p.Registry.SetValue(Path, Name, RegValue.String(result));
        return Task.CompletedTask;
    }

    private List<string> ReadSegments(PlatformServices p)
    {
        var original = p.Registry.GetValue(Path, Name);
        if (original is null) return [];
        if (original.Kind != RegistryValueKind.String)
            throw new InvalidOperationException($"Ожидалась строка настроек в {Describe()}.");
        return original.Data.Split(';').ToList();
    }

    private Snapshot ReadSnapshot(PlatformServices p)
    {
        var segments = ReadSegments(p);
        return new(segments.Count != 0, segments.FindLast(IsTarget), segments.Count > 1 || segments.FirstOrDefault() is { Length: > 0 });
    }

    private static void Insert(List<string> segments, string segment)
    {
        var index = segments.Count > 0 && segments[^1].Length == 0 ? segments.Count - 1 : segments.Count;
        segments.Insert(index, segment);
    }

    private Snapshot Parse(string? captured)
    {
        if (captured is null) return new(false, null);

        using var json = JsonDocument.Parse(captured);
        if (json.RootElement.ValueKind == JsonValueKind.String)
            return new(true, $"{Key}={json.RootElement.GetString()}");
        if (json.RootElement.TryGetProperty(nameof(RegValue.Kind), out _))
        {
            var old = RegistryValueAction.Deserialize(captured);
            return new(old is not null, old?.Data.Split(';').LastOrDefault(IsTarget));
        }
        return JsonSerializer.Deserialize<Snapshot>(captured) ?? throw new InvalidDataException("Некорректный снимок графических настроек.");
    }

    private bool IsTarget(string segment)
    {
        var part = segment.AsSpan().TrimStart();
        var separator = part.IndexOf('=');
        return separator > 0 && part[..separator].Equals(Key, StringComparison.OrdinalIgnoreCase);
    }

    public override string Describe() => $@"{Path}\{Name} [{Key}]";
}
