using Corelight.Core.Platform;
using Corelight.Core.Tweaks;

namespace Corelight.Core.Actions;

/// <summary>Отключает задачу планировщика и восстанавливает сохранённое состояние.</summary>
public sealed class ScheduledTaskAction : TweakAction
{
    /// <summary>Полный путь задачи, например "\Microsoft\Windows\Feedback\Siuf\DmClient".</summary>
    public required string Path { get; init; }

    public override Task<ActionState> DetectAsync(PlatformServices p, CancellationToken ct)
    {
        var enabled = p.Tasks.IsEnabled(Path);
        var state = enabled is null ? ActionState.NotApplicable
            : enabled.Value ? ActionState.NotApplied
            : ActionState.Applied;
        return Task.FromResult(state);
    }

    public override Task<string?> CaptureAsync(PlatformServices p, CancellationToken ct) =>
        Task.FromResult(p.Tasks.IsEnabled(Path)?.ToString());

    public override Task ApplyAsync(PlatformServices p, CancellationToken ct)
    {
        p.Tasks.SetEnabled(Path, false);
        return Task.CompletedTask;
    }

    public override Task RevertAsync(PlatformServices p, string? captured, CancellationToken ct)
    {
        if (captured is not null && p.Tasks.IsEnabled(Path) is not null)
            p.Tasks.SetEnabled(Path, bool.Parse(captured));
        return Task.CompletedTask;
    }

    public override string Describe() => $"задача {Path}";
}
