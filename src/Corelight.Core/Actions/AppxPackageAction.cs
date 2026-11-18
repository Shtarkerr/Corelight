using Corelight.Core.Platform;
using Corelight.Core.Tweaks;

namespace Corelight.Core.Actions;

/// <summary>
/// Удаляет предустановленное приложение. «Применено» означает, что приложения нет в системе.
/// Возврат — переустановка из Microsoft Store через winget.
/// </summary>
public sealed class AppxPackageAction : TweakAction
{
    private const string Installed = "installed";
    private const string ProvisionedPrefix = "provisioned:";

    public required string Package { get; init; }

    /// <summary>ID приложения в Microsoft Store; без него вернуть приложение можно только вручную.</summary>
    public string? StoreId { get; init; }

    public override Task<ActionState> DetectAsync(PlatformServices p, CancellationToken ct) =>
        Task.FromResult(p.Apps.IsInstalled(Package) ? ActionState.NotApplied : ActionState.Applied);

    public override Task<string?> CaptureAsync(PlatformServices p, CancellationToken ct)
    {
        if (!p.Apps.IsInstalled(Package)) return Task.FromResult<string?>(null);
        var family = StoreId is null ? null : p.Apps.GetProvisionedFamily(Package);
        return Task.FromResult<string?>(family is null ? Installed : ProvisionedPrefix + family);
    }

    public override bool CapturedStateMatches(string? current, string? original) =>
        (original == Installed && current?.StartsWith(ProvisionedPrefix, StringComparison.Ordinal) == true)
        || string.Equals(current, original, StringComparison.OrdinalIgnoreCase);

    public override Task ApplyAsync(PlatformServices p, CancellationToken ct) => p.Apps.RemoveAsync(Package, ct);

    public override async Task RevertAsync(PlatformServices p, string? captured, CancellationToken ct)
    {
        if (captured is null) return;
        await ReinstallAsync(p, ct);
        if (captured.StartsWith(ProvisionedPrefix, StringComparison.Ordinal))
            await p.Apps.ProvisionAsync(Package, captured[ProvisionedPrefix.Length..], ct);
    }

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
