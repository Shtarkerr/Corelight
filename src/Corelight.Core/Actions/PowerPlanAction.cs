using Corelight.Core.Platform;
using Corelight.Core.Tweaks;

namespace Corelight.Core.Actions;

/// <summary>
/// Активирует штатную схему питания Windows. Если схема скрыта (например, на системах с Modern Standby),
/// создаётся её копия с фиксированным GUID, чтобы состояние можно было надёжно определить.
/// </summary>
public sealed class PowerPlanAction : TweakAction
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    /// <summary>GUID штатной схемы Windows (высокая или максимальная производительность).</summary>
    public required Guid Scheme { get; init; }

    /// <summary>GUID, под которым создаётся копия схемы, если оригинал в системе отсутствует.</summary>
    public required Guid CopyGuid { get; init; }

    public override async Task<ActionState> DetectAsync(PlatformServices p, CancellationToken ct)
    {
        var active = await p.Power.GetActiveSchemeAsync(ct);
        return active == Scheme || active == CopyGuid ? ActionState.Applied : ActionState.NotApplied;
    }

    public override async Task<string?> CaptureAsync(PlatformServices p, CancellationToken ct) =>
        (await p.Power.GetActiveSchemeAsync(ct)).ToString();

    public override async Task ApplyAsync(PlatformServices p, CancellationToken ct)
    {
        var schemes = await p.Power.ListSchemesAsync(ct);
        if (schemes.Contains(Scheme))
        {
            await p.Power.SetActiveSchemeAsync(Scheme, ct);
            return;
        }
        if (!schemes.Contains(CopyGuid))
            await p.Power.DuplicateSchemeAsync(Scheme, CopyGuid, ct);
        await p.Power.SetActiveSchemeAsync(CopyGuid, ct);
    }

    public override async Task RevertAsync(PlatformServices p, string? captured, CancellationToken ct)
    {
        var target = captured is null ? Balanced : Guid.Parse(captured);
        var schemes = await p.Power.ListSchemesAsync(ct);
        await p.Power.SetActiveSchemeAsync(schemes.Contains(target) ? target : Balanced, ct);
    }

    public override Task RevertToDefaultAsync(PlatformServices p, CancellationToken ct) =>
        p.Power.SetActiveSchemeAsync(Balanced, ct);

    public override string Describe() => $"схема питания {Scheme}";
}
