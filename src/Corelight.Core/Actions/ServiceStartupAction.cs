using System.Text.Json;
using Corelight.Core.Platform;
using Corelight.Core.Tweaks;

namespace Corelight.Core.Actions;

/// <summary>Меняет тип запуска службы и при отключении сразу её останавливает.</summary>
public sealed class ServiceStartupAction : TweakAction
{
    private sealed record ServiceSnapshot(ServiceStartType StartType, bool WasRunning);
    public bool LastStopPending { get; private set; }
    public required string Service { get; init; }

    public ServiceStartType StartType { get; init; } = ServiceStartType.Disabled;

    public override Task<ActionState> DetectAsync(PlatformServices p, CancellationToken ct)
    {
        var current = p.Services.GetStartType(Service);
        var state = current is null ? ActionState.NotApplicable
            : current != StartType ? ActionState.NotApplied
            : StartType == ServiceStartType.Disabled && p.Services.IsRunning(Service) ? ActionState.NotApplied
            : ActionState.Applied;
        return Task.FromResult(state);
    }

    public override Task<string?> CaptureAsync(PlatformServices p, CancellationToken ct)
    {
        var start = p.Services.GetStartType(Service);
        return Task.FromResult(start is null ? null : JsonSerializer.Serialize(new ServiceSnapshot(start.Value, p.Services.IsRunning(Service))));
    }

    public override bool CapturedStateMatches(string? current, string? original)
    {
        if (original is null) return current is null;
        if (current is null) return false;
        var old = Parse(original);
        var now = Parse(current);
        return now.StartType == old.StartType && (old.WasRunning is null || now.WasRunning == old.WasRunning);
    }

    public override async Task ApplyAsync(PlatformServices p, CancellationToken ct)
    {
        LastStopPending = false;
        await p.Services.SetStartTypeAsync(Service, StartType, ct);
        if (StartType == ServiceStartType.Disabled)
            LastStopPending = !p.Services.TryStop(Service);
    }

    public override Task RevertAsync(PlatformServices p, string? captured, CancellationToken ct) =>
        captured is null ? Task.CompletedTask : RestoreAsync(p, Parse(captured), ct);

    public override string Describe() => $"служба {Service}";

    private static (ServiceStartType StartType, bool? WasRunning) Parse(string captured)
    {
        if (captured.StartsWith('{'))
        {
            var snapshot = JsonSerializer.Deserialize<ServiceSnapshot>(captured) ?? throw new InvalidDataException("Снимок службы пуст.");
            return (snapshot.StartType, snapshot.WasRunning);
        }
        return (Enum.Parse<ServiceStartType>(captured), null); // Бэкап предыдущих версий.
    }

    private async Task RestoreAsync(PlatformServices p, (ServiceStartType StartType, bool? WasRunning) snapshot, CancellationToken ct)
    {
        await p.Services.SetStartTypeAsync(Service, snapshot.StartType, ct);
        var shouldStart = snapshot.WasRunning ?? (snapshot.StartType is ServiceStartType.Automatic or ServiceStartType.AutomaticDelayed);
        if (shouldStart && !p.Services.TryStart(Service))
            throw new InvalidOperationException($"Не удалось запустить службу {Service}; исходный снимок сохранён.");
        if (snapshot.WasRunning == false && p.Services.IsRunning(Service) && !p.Services.TryStop(Service))
            throw new InvalidOperationException($"Не удалось остановить службу {Service}; исходный снимок сохранён.");
    }
}
