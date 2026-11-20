using Corelight.Core.Catalog;
using Corelight.Core.SystemInfo;
using Corelight.Core.Tweaks;

namespace Corelight.Core.Scanning;

/// <param name="UnavailableReason">Почему твик нельзя применить на этом ПК; null, если можно.</param>
/// <param name="Recommended">Рекомендуется ли твик для этого ПК.</param>
public sealed record TweakStatus(TweakDefinition Tweak, TweakState State, string? UnavailableReason, bool Recommended)
{
    public bool IsAvailable => UnavailableReason is null && State is not (TweakState.NotApplicable or TweakState.Unknown);
    public bool CanApply => IsAvailable && State == TweakState.NotApplied;

    /// <summary>Что попадает в результаты сканирования: рекомендовано и ещё не применено.</summary>
    public bool IsSuggestion => CanApply && Recommended;

    /// <summary>Выбор каталога имеет приоритет над уровнем риска; Advanced всегда выбирается вручную.</summary>
    public bool IsPreselected => IsSuggestion && Tweak.Risk != TweakRisk.Advanced
        && (Tweak.Preselected ?? Tweak.Risk == TweakRisk.Safe);
}

public sealed record ScanReport(HardwareProfile Hardware, IReadOnlyList<TweakStatus> Statuses)
{
    public IEnumerable<TweakStatus> Suggestions => Statuses.Where(s => s.IsSuggestion).OrderByDescending(s => s.Tweak.Impact);
    public IEnumerable<TweakStatus> RecommendedItems => Suggestions.Where(s => s.IsPreselected);
    public IEnumerable<TweakStatus> ManualItems => Statuses
        .Where(s => s.CanApply && !s.IsPreselected)
        .OrderByDescending(s => s.IsSuggestion)
        .ThenByDescending(s => s.Tweak.Impact);
}

public sealed class Scanner(TweakCatalog catalog, TweakEngine engine, HardwareProbe probe)
{
    private static readonly Dictionary<string, string> ConditionReasons = new()
    {
        ["desktop"] = "Только для настольных ПК.",
        ["laptop"] = "Только для ноутбуков.",
        ["noPrinter"] = "В системе есть принтер.",
        ["noBluetooth"] = "В системе есть Bluetooth-адаптер.",
        ["ssd"] = "Системный диск не SSD.",
        ["hdd"] = "Системный диск не HDD.",
        ["hagsSupported"] = "Видеокарта или драйвер не поддерживают аппаратное планирование GPU.",
    };

    public Task<HardwareProfile> ProbeHardwareAsync(CancellationToken ct = default) => Task.Run(probe.Probe, ct);

    public async Task<ScanReport> ScanAsync(HardwareProfile hardware, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        engine.RefreshExternalState();
        var statuses = new List<TweakStatus>(catalog.Tweaks.Count);
        for (var i = 0; i < catalog.Tweaks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            statuses.Add(await EvaluateAsync(catalog.Tweaks[i], hardware, ct));
            progress?.Report((i + 1) / (double)catalog.Tweaks.Count);
        }
        return new ScanReport(hardware, statuses);
    }

    public Task<TweakStatus> EvaluateAsync(TweakDefinition tweak, HardwareProfile hardware, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            var reason = UnavailableReason(tweak, hardware);
            var detection = reason is null ? await engine.DetectDetailedAsync(tweak, ct) : new TweakDetection(TweakState.NotApplicable);
            if (detection.State == TweakState.Unknown)
                reason = "Не удалось проверить состояние: " + detection.Error;
            else if (detection.State == TweakState.NotApplied)
                reason = engine.GetApplyConflictReason(tweak, catalog.Tweaks);
            var recommended = tweak.Recommended && tweak.RecommendWhen.All(hardware.Satisfies);
            return new TweakStatus(tweak, detection.State, reason, recommended);
        }, ct);

    private static string? UnavailableReason(TweakDefinition tweak, HardwareProfile hardware)
    {
        if (hardware.OsBuild > 0 && hardware.OsBuild < tweak.MinBuild)
            return $"Требуется Windows сборки {tweak.MinBuild} или новее.";

        var missing = tweak.Requires.FirstOrDefault(c => !hardware.Satisfies(c));
        return missing is null ? null : ConditionReasons.GetValueOrDefault(missing, "Не поддерживается на этом ПК.");
    }
}
