using Microsoft.Extensions.Logging;
using Corelight.Core.Backup;
using Corelight.Core.Platform;

namespace Corelight.Core.Tweaks;

public enum TweakState
{
    /// <summary>Ничего из того, что меняет твик, в системе нет.</summary>
    NotApplicable,
    NotApplied,
    Applied,
}

public sealed class TweakException(string message, Exception? inner = null) : Exception(message, inner);

/// <param name="Warnings">Ошибки необязательных действий, которые не помешали применить твик.</param>
public sealed record TweakResult(IReadOnlyList<string> Warnings);

public sealed class TweakEngine(PlatformServices platform, IBackupStore backups, ILogger<TweakEngine> log)
{
    public IBackupStore Backups => backups;

    public async Task<TweakState> DetectAsync(TweakDefinition tweak, CancellationToken ct = default)
    {
        var states = new List<(TweakAction Action, ActionState State)>();
        foreach (var action in tweak.Actions)
            states.Add((action, await SafeDetectAsync(action, ct)));

        var relevant = states.Where(s => s.State != ActionState.NotApplicable).ToList();
        if (relevant.Count == 0)
            return TweakState.NotApplicable;

        // Состояние определяют обязательные действия.
        var required = relevant.Where(s => !s.Action.Optional).ToList();
        if (required.Count > 0)
            return required.All(s => s.State == ActionState.Applied) ? TweakState.Applied : TweakState.NotApplied;

        // Твик только из необязательных действий (набор задач планировщика) применён, если применено всё.
        // Исключение — мы его уже применяли, и часть задач Windows отключить не дала: повторять бессмысленно.
        if (relevant.All(s => s.State == ActionState.Applied))
            return TweakState.Applied;
        return backups.Get(tweak.Id) is not null && relevant.Any(s => s.State == ActionState.Applied)
            ? TweakState.Applied
            : TweakState.NotApplied;
    }

    /// <summary>
    /// Откатывает уже применённые твики той же группы взаимоисключающих (например, разные схемы питания).
    /// Без этого бэкап нового твика запомнил бы состояние, созданное соседним твиком, а не исходное.
    /// </summary>
    public async Task RevertConflictsAsync(TweakDefinition tweak, IEnumerable<TweakDefinition> candidates, CancellationToken ct = default)
    {
        if (tweak.ExclusiveGroup is null)
            return;

        foreach (var other in candidates)
        {
            if (other.Id != tweak.Id && other.ExclusiveGroup == tweak.ExclusiveGroup && backups.Get(other.Id) is not null)
                await RevertAsync(other, ct);
        }
    }

    public async Task<TweakResult> ApplyAsync(TweakDefinition tweak, CancellationToken ct = default)
    {
        log.LogInformation("Применение твика {Tweak}", tweak.Id);

        // Если твик уже применялся, сохраняем самый первый бэкап: в нём настоящее исходное состояние.
        var record = backups.Get(tweak.Id);
        var createdRecord = false;
        if (record is null || record.Actions.Count != tweak.Actions.Count)
        {
            record = await CaptureAsync(tweak, ct);
            backups.Save(record);
            createdRecord = true;
        }

        var applied = new List<int>();
        var warnings = new List<string>();
        try
        {
            for (var i = 0; i < tweak.Actions.Count; i++)
            {
                var action = tweak.Actions[i];
                if (await SafeDetectAsync(action, ct) != ActionState.NotApplied)
                    continue;

                try
                {
                    await action.ApplyAsync(platform, ct);
                    applied.Add(i);
                }
                catch (Exception ex) when (action.Optional && ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Необязательное действие {Action} твика {Tweak} не выполнено", action.Describe(), tweak.Id);
                    warnings.Add($"{action.Describe()}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Ошибка применения твика {Tweak}, откатываем выполненные действия", tweak.Id);
            for (var j = applied.Count - 1; j >= 0; j--)
            {
                var i = applied[j];
                try
                {
                    await tweak.Actions[i].RevertAsync(platform, record.Actions[i].State, CancellationToken.None);
                }
                catch (Exception rollbackEx)
                {
                    log.LogError(rollbackEx, "Не удалось откатить {Action}", tweak.Actions[i].Describe());
                }
            }
            if (createdRecord)
                backups.Remove(tweak.Id);

            if (ex is OperationCanceledException)
                throw;
            throw new TweakException($"Не удалось применить «{tweak.Title}»: {ex.Message}", ex);
        }

        return new TweakResult(warnings);
    }

    public async Task RevertAsync(TweakDefinition tweak, CancellationToken ct = default)
    {
        log.LogInformation("Откат твика {Tweak}", tweak.Id);

        var record = backups.Get(tweak.Id);
        var usable = record is not null && record.Actions.Count == tweak.Actions.Count;
        var errors = new List<string>();

        for (var i = tweak.Actions.Count - 1; i >= 0; i--)
        {
            var action = tweak.Actions[i];
            try
            {
                if (usable && record!.Actions[i].Applicable)
                    await action.RevertAsync(platform, record.Actions[i].State, ct);
                else if (await SafeDetectAsync(action, ct) == ActionState.Applied)
                    await action.RevertToDefaultAsync(platform, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Не удалось откатить {Action} твика {Tweak}", action.Describe(), tweak.Id);
                if (!action.Optional)
                    errors.Add(ex.Message);
            }
        }

        if (errors.Count > 0)
            throw new TweakException($"Не удалось полностью откатить «{tweak.Title}»: {string.Join(" ", errors.Distinct())}");

        if (record is not null)
            backups.Remove(tweak.Id);
    }

    private async Task<BackupRecord> CaptureAsync(TweakDefinition tweak, CancellationToken ct)
    {
        var actions = new List<ActionBackup>();
        foreach (var action in tweak.Actions)
        {
            actions.Add(await SafeDetectAsync(action, ct) == ActionState.NotApplicable
                ? new ActionBackup(false, null)
                : new ActionBackup(true, await action.CaptureAsync(platform, ct)));
        }
        return new BackupRecord { TweakId = tweak.Id, Title = tweak.Title, AppliedAt = DateTimeOffset.Now, Actions = actions };
    }

    private async Task<ActionState> SafeDetectAsync(TweakAction action, CancellationToken ct)
    {
        try
        {
            return await action.DetectAsync(platform, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Не удалось определить состояние {Action}", action.Describe());
            return ActionState.NotApplicable;
        }
    }
}
