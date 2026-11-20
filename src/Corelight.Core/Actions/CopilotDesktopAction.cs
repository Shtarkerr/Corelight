using Corelight.Core.Platform;
using Corelight.Core.Tweaks;

namespace Corelight.Core.Actions;

public sealed class CopilotDesktopAction : TweakAction
{
    public override Task<ActionState> DetectAsync(PlatformServices p, CancellationToken ct) =>
        Task.FromResult(p.CopilotDesktop?.IsInstalled() == true ? ActionState.NotApplied : ActionState.Applied);

    public override Task<string?> CaptureAsync(PlatformServices p, CancellationToken ct) =>
        Task.FromResult(p.CopilotDesktop?.IsInstalled() == true ? "installed" : null);

    public override Task ApplyAsync(PlatformServices p, CancellationToken ct) =>
        p.CopilotDesktop?.UninstallAsync(ct) ?? throw new InvalidOperationException("Управление настольным Copilot недоступно.");

    public override Task RevertAsync(PlatformServices p, string? captured, CancellationToken ct) =>
        captured is null ? Task.CompletedTask
            : throw new InvalidOperationException("Установите настольный Copilot вручную и проверьте возврат в истории Corelight.");

    public override string Describe() => "настольный Microsoft Copilot";
}
