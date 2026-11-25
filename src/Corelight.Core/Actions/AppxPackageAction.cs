using Corelight.Core.Platform;
using Corelight.Core.Tweaks;
using System.Text.Json;

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

    public override Task<ActionState> DetectAsync(PlatformServices p, CancellationToken ct)
    {
        var state = p.Apps.GetPackageState(Package);
        return Task.FromResult(state.CurrentUser ? ActionState.NotApplied :
            state.Present ? ActionState.NotApplicable : ActionState.Applied);
    }

    public override Task<string?> CaptureAsync(PlatformServices p, CancellationToken ct)
    {
        var state = p.Apps.GetPackageState(Package);
        return Task.FromResult(state.Present ? JsonSerializer.Serialize(state) : null);
    }

    public override bool CapturedStateMatches(string? current, string? original)
    {
        if (current is null || original is null) return current == original;
        var now = Parse(current);
        var before = Parse(original);
        return now.CurrentUser == before.CurrentUser
            && (before.CurrentUser || now.AnyUser == before.AnyUser)
            && (before.ProvisionedFamily is null ||
                string.Equals(now.ProvisionedFamily, before.ProvisionedFamily, StringComparison.OrdinalIgnoreCase));
    }

    public override Task ApplyAsync(PlatformServices p, CancellationToken ct) => p.Apps.RemoveAsync(Package, ct);

    public override async Task RevertAsync(PlatformServices p, string? captured, CancellationToken ct)
    {
        if (captured is null) return;
        var state = Parse(captured);
        p.Apps.Refresh();
        if (state.CurrentUser && !p.Apps.GetPackageState(Package).CurrentUser)
        {
            if (StoreId is null)
                throw new InvalidOperationException("Верните приложение вручную и проверьте его установку.");
            await p.Apps.InstallFromStoreAsync(StoreId, ct);
        }
        if (state.ProvisionedFamily is { } family)
            await p.Apps.ProvisionAsync(Package, family, ct);
    }

    public override string Describe() => $"приложение {Package}";

    private static AppxPackageState Parse(string captured)
    {
        if (captured == Installed) return new(true, true, null);
        if (captured.StartsWith(ProvisionedPrefix, StringComparison.Ordinal))
            return new(true, true, captured[ProvisionedPrefix.Length..]);
        return JsonSerializer.Deserialize<AppxPackageState>(captured)
            ?? throw new InvalidDataException("Некорректный снимок пакета.");
    }
}
