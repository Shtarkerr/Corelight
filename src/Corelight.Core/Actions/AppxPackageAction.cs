using Corelight.Core.Platform;
using Corelight.Core.Tweaks;

namespace Corelight.Core.Actions;

/// <summary>
/// Удаляет предустановленное приложение. «Применено» означает, что приложения нет в системе.
/// Возврат — переустановка из Microsoft Store через winget.
/// </summary>
public sealed class AppxPackageAction : TweakAction
{
    public required string Package { get; init; }

    /// <summary>ID приложения в Microsoft Store; без него вернуть приложение можно только вручную.</summary>
    public string? StoreId { get; init; }

    public override Task<ActionState> DetectAsync(PlatformServices p, CancellationToken ct) =>
        Task.FromResult(p.Apps.IsInstalled(Package) ? ActionState.NotApplied : ActionState.Applied);

    public override Task<string?> CaptureAsync(PlatformServices p, CancellationToken ct) =>
        Task.FromResult<string?>(p.Apps.IsInstalled(Package) ? "installed" : null);

    public override Task ApplyAsync(PlatformServices p, CancellationToken ct) => p.Apps.RemoveAsync(Package, ct);

    public override Task RevertAsync(PlatformServices p, string? captured, CancellationToken ct) =>
        captured is null ? Task.CompletedTask : ReinstallAsync(p, ct);

    public override Task RevertToDefaultAsync(PlatformServices p, CancellationToken ct) => ReinstallAsync(p, ct);

    public override string Describe() => $"приложение {Package}";

    private Task ReinstallAsync(PlatformServices p, CancellationToken ct)
    {
        p.Apps.Refresh();
        if (p.Apps.IsInstalled(Package))
            return Task.CompletedTask;
        if (StoreId is null)
            throw new InvalidOperationException("Это приложение можно вернуть только вручную через Microsoft Store.");
        return p.Apps.InstallFromStoreAsync(StoreId, ct);
    }
}
