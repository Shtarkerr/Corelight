using Corelight.Core.Platform;
using Corelight.Core.Tweaks;

namespace Corelight.Core.Actions;

/// <summary>Удаление штатного Win32-клиента OneDrive и возврат из сохранённого установщика.</summary>
public sealed class OneDriveAction : TweakAction
{
    public override Task<ActionState> DetectAsync(PlatformServices p, CancellationToken ct) =>
        Task.FromResult(p.OneDrive.GetInstalledScope() == OneDriveScope.None ? ActionState.Applied : ActionState.NotApplied);

    public override Task<string?> CaptureAsync(PlatformServices p, CancellationToken ct)
    {
        var scope = p.OneDrive.GetInstalledScope();
        return Task.FromResult(scope == OneDriveScope.None ? null : scope.ToString());
    }

    public override Task ApplyAsync(PlatformServices p, CancellationToken ct) => p.OneDrive.UninstallAsync(ct);

    public override Task RevertAsync(PlatformServices p, string? captured, CancellationToken ct) =>
        captured is null ? Task.CompletedTask : p.OneDrive.RestoreAsync(Enum.Parse<OneDriveScope>(captured), ct);

    public override string Describe() => "клиент синхронизации Microsoft OneDrive";
}
