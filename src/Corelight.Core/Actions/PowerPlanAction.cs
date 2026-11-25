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

    public override Task ApplyAsync(PlatformServices p, CancellationToken ct) => ApplyAsync(p, null, ct);

    internal async Task ApplyAsync(PlatformServices p, Action<string>? recordCopy, CancellationToken ct)
    {
        var schemes = await p.Power.ListSchemesAsync(ct);
        if (schemes.Contains(Scheme))
        {
            await p.Power.SetActiveSchemeAsync(Scheme, ct);
            return;
        }
        if (schemes.Contains(CopyGuid))
        {
            await p.Power.SetActiveSchemeAsync(CopyGuid, ct);
            return;
        }

        await p.Power.DuplicateSchemeAsync(Scheme, CopyGuid, ct);
        var copy = await p.Power.GetSchemeFingerprintAsync(CopyGuid, ct);
        try
        {
            recordCopy?.Invoke(copy);
            await p.Power.SetActiveSchemeAsync(CopyGuid, ct);
        }
        catch
        {
            if (await p.Power.GetActiveSchemeAsync(CancellationToken.None) != CopyGuid &&
                await p.Power.GetSchemeFingerprintAsync(CopyGuid, CancellationToken.None) == copy)
                await p.Power.DeleteSchemeAsync(CopyGuid, CancellationToken.None);
            throw;
        }
    }

    public override async Task RevertAsync(PlatformServices p, string? captured, CancellationToken ct)
    {
        var target = captured is null ? Balanced : Guid.Parse(captured);
        var schemes = await p.Power.ListSchemesAsync(ct);
        if (!schemes.Contains(target))
            throw new InvalidOperationException("Исходная схема питания удалена. Выберите другую схему в Windows. Запись отката сохранена.");
        await p.Power.SetActiveSchemeAsync(target, ct);
    }

    internal async Task RemoveUnchangedCopyAsync(PlatformServices p, string? fingerprint, CancellationToken ct)
    {
        if (fingerprint is null || await p.Power.GetActiveSchemeAsync(ct) == CopyGuid ||
            !(await p.Power.ListSchemesAsync(ct)).Contains(CopyGuid))
            return;
        if (await p.Power.GetSchemeFingerprintAsync(CopyGuid, ct) == fingerprint)
            await p.Power.DeleteSchemeAsync(CopyGuid, ct);
    }

    public override string Describe() => $"схема питания {Scheme}";
}
