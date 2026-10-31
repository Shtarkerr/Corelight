using Corelight.Core.Platform;
using Corelight.Core.Tweaks;

namespace Corelight.Core.Actions;

/// <summary>Меняет тип запуска службы и при отключении сразу её останавливает.</summary>
public sealed class ServiceStartupAction : TweakAction
{
    public required string Service { get; init; }

    public ServiceStartType StartType { get; init; } = ServiceStartType.Disabled;

    public ServiceStartType Default { get; init; } = ServiceStartType.Manual;

    public override Task<ActionState> DetectAsync(PlatformServices p, CancellationToken ct)
    {
        var current = p.Services.GetStartType(Service);
        var state = current is null ? ActionState.NotApplicable
            : current == StartType ? ActionState.Applied
            : ActionState.NotApplied;
        return Task.FromResult(state);
    }

    public override Task<string?> CaptureAsync(PlatformServices p, CancellationToken ct) =>
        Task.FromResult(p.Services.GetStartType(Service)?.ToString());

    public override async Task ApplyAsync(PlatformServices p, CancellationToken ct)
    {
        await p.Services.SetStartTypeAsync(Service, StartType, ct);
        if (StartType == ServiceStartType.Disabled)
            p.Services.TryStop(Service);
    }

    public override Task RevertAsync(PlatformServices p, string? captured, CancellationToken ct) =>
        captured is null ? Task.CompletedTask : RestoreAsync(p, Enum.Parse<ServiceStartType>(captured), ct);

    public override Task RevertToDefaultAsync(PlatformServices p, CancellationToken ct) => RestoreAsync(p, Default, ct);

    public override string Describe() => $"служба {Service}";

    private async Task RestoreAsync(PlatformServices p, ServiceStartType startType, CancellationToken ct)
    {
        await p.Services.SetStartTypeAsync(Service, startType, ct);
        if (startType is ServiceStartType.Automatic or ServiceStartType.AutomaticDelayed)
            p.Services.TryStart(Service);
    }
}
