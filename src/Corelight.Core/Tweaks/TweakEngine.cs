using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Corelight.Core.Actions;
using Corelight.Core.Backup;
using Corelight.Core.Platform;

namespace Corelight.Core.Tweaks;

public enum TweakState
{
    /// <summary>Ничего из того, что меняет твик, в системе нет.</summary>
    NotApplicable,
    NotApplied,
    Applied,
    Unknown,
}

public sealed class TweakException(string message, Exception? inner = null) : Exception(message, inner);

/// <param name="Warnings">Ошибки необязательных действий, которые не помешали применить твик.</param>
public sealed record TweakResult(IReadOnlyList<string> Warnings, bool Confirmed = true, bool RequiresReboot = false);
public sealed record TweakDetection(TweakState State, string? Error = null);
public sealed record AppRestoreIssue(TweakDefinition Tweak, string Reason);

public sealed class TweakEngine(PlatformServices platform, IBackupStore backups, ILogger<TweakEngine> log)
{
    private readonly ConcurrentDictionary<string, string> _blockedChanges = new();

    public void RefreshExternalState()
    {
        platform.Apps.Refresh();
        platform.Components?.Refresh();
    }
    public IBackupStore Backups => backups;

    /// <summary>Проверяет доступность возврата установленных AppX до применения выбранных изменений.</summary>
    public async Task<IReadOnlyList<AppRestoreIssue>> CheckAppRestoresAsync(
        IReadOnlyList<TweakDefinition> tweaks, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var candidates = tweaks.Select(tweak => (Tweak: tweak, Packages: tweak.Actions.OfType<AppxPackageAction>().ToArray()))
            .Where(candidate => candidate.Packages.Length > 0).ToArray();
        if (candidates.Length == 0) return [];

        Dictionary<string, bool> installed;
        try
        {
            platform.Apps.Refresh();
            installed = candidates.SelectMany(candidate => candidate.Packages)
                .Select(package => package.Package).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(package => package, platform.Apps.IsInstalled, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Не удалось проверить установленные пакеты перед удалением");
            return candidates.Select(candidate => new AppRestoreIssue(candidate.Tweak, "Не удалось проверить установленные пакеты.")).ToArray();
        }

        var storeIds = candidates.SelectMany(candidate => candidate.Packages)
            .Where(package => installed[package.Package]).Select(package => package.StoreId).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var results = new ConcurrentDictionary<string, StoreRestoreAvailability>(StringComparer.OrdinalIgnoreCase);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await Parallel.ForEachAsync(storeIds, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = budget.Token },
                async (id, token) =>
                {
                    try { results[id] = await platform.Apps.CheckStoreRestoreAsync(id, token); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogWarning(ex, "Ошибка проверки возврата приложения Store {StoreId}", id);
                        results[id] = StoreRestoreAvailability.Unverified;
                    }
                });
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            log.LogInformation("Достигнут общий предел времени проверки возврата AppX");
        }
        ct.ThrowIfCancellationRequested();

        var issues = new List<AppRestoreIssue>();
        foreach (var (tweak, packages) in candidates)
        {
            var reasons = packages.Where(package => installed[package.Package])
                .Select(package => package.StoreId is { } id
                    ? DescribeRestoreAvailability(results.GetValueOrDefault(id, StoreRestoreAvailability.TimedOut))
                    : tweak.RestoreHelpUrl is null ? "Автоматического возврата нет." : "Возврат только вручную.")
                .OfType<string>().Distinct().ToArray();
            if (reasons.Length > 0)
                issues.Add(new(tweak, string.Join(" ", reasons)));
        }
        return issues;
    }

    private static string? DescribeRestoreAvailability(StoreRestoreAvailability availability) => availability switch
    {
        StoreRestoreAvailability.Found => null,
        StoreRestoreAvailability.NotFound => "WinGet не нашёл приложение в Microsoft Store.",
        StoreRestoreAvailability.NoApplicableInstaller => "WinGet не нашёл подходящий установщик.",
        StoreRestoreAvailability.AgreementsRequired => "WinGet требует принять условия источника Microsoft Store.",
        StoreRestoreAvailability.WinGetUnavailable => "WinGet недоступен.",
        StoreRestoreAvailability.TimedOut => "Проверка не завершилась вовремя.",
        _ => "Доступность через Microsoft Store не подтверждена.",
    };

    public async Task<TweakState> DetectAsync(TweakDefinition tweak, CancellationToken ct = default) =>
        (await DetectDetailedAsync(tweak, ct)).State;

    public async Task<TweakDetection> DetectDetailedAsync(TweakDefinition tweak, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_blockedChanges.TryGetValue(tweak.Id, out var reason))
            return new(TweakState.Unknown, reason);

        var states = new List<(TweakAction Action, ActionState State)>();
        foreach (var action in tweak.Actions)
        {
            try { states.Add((action, await action.DetectAsync(platform, ct))); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Не удалось определить состояние {Action}", action.Describe());
                return new(TweakState.Unknown, ex.Message);
            }
        }

        var relevant = states.Where(s => s.State != ActionState.NotApplicable).ToList();
        if (relevant.Count == 0)
            return new(TweakState.NotApplicable);

        if (tweak.AppliedWhenAnyActionMatches)
            return new(relevant.Any(s => s.State == ActionState.Applied) ? TweakState.Applied : TweakState.NotApplied);

        // Состояние определяют обязательные действия.
        // Варианты одного приложения (например, старый/новый Teams) считаются установленными,
        // пока присутствует хотя бы один пакет; optional влияет на обработку ошибок, а не на наличие.
        var required = relevant.Where(s => !s.Action.Optional || s.Action is AppxPackageAction).ToList();
        if (required.Count > 0)
            return new(required.All(s => s.State == ActionState.Applied) ? TweakState.Applied : TweakState.NotApplied);

        // Твик только из необязательных действий (набор задач планировщика) применён, если применено всё.
        // Исключение — мы его уже применяли, и часть задач Windows отключить не дала: повторять бессмысленно.
        if (relevant.All(s => s.State == ActionState.Applied))
            return new(TweakState.Applied);
        return new(backups.Get(tweak.Id) is not null && relevant.Any(s => s.State == ActionState.Applied)
            ? TweakState.Applied
            : TweakState.NotApplied);
    }

    /// <summary>Объясняет, какое изменение той же группы нужно откатить перед применением.</summary>
    public string? GetApplyConflictReason(TweakDefinition tweak, IEnumerable<TweakDefinition> candidates)
    {
        if (tweak.ExclusiveGroup is null)
            return null;

        var conflict = candidates.FirstOrDefault(other =>
            other.Id != tweak.Id && other.ExclusiveGroup == tweak.ExclusiveGroup && backups.Get(other.Id) is not null);
        return conflict is null ? null : $"Сначала откатите «{conflict.Title}» в разделе «История и откат».";
    }

    public async Task<TweakResult> ApplyAsync(TweakDefinition tweak, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_blockedChanges.TryGetValue(tweak.Id, out var reason))
            throw new TweakException($"Не удалось применить «{tweak.Title}»: {reason}");
        RefreshExternalState();
        log.LogInformation("Применение твика {Tweak}", tweak.Id);

        // Если твик уже применялся, сохраняем самый первый бэкап: в нём настоящее исходное состояние.
        var record = backups.Get(tweak.Id);
        var createdRecord = false;
        if (record is not null) EnsureCompatibleBackup(tweak, record);
        if (record is null)
        {
            record = await CaptureAsync(tweak, ct);
            backups.Save(record);
            createdRecord = true;
        }

        var attempted = new List<(int Index, string? Before)>();
        var optionalFailures = new HashSet<int>();
        var warnings = new List<string>();
        try
        {
            for (var i = 0; i < tweak.Actions.Count; i++)
            {
                var action = tweak.Actions[i];
                if (!record.Actions[i].Applicable) continue;
                if (await action.DetectAsync(platform, ct) != ActionState.NotApplied)
                    continue;

                try
                {
                    var before = createdRecord ? record.Actions[i].State : await action.CaptureAsync(platform, ct);
                    // Действие может успеть изменить систему перед исключением.
                    attempted.Add((i, before));
                    await action.ApplyAsync(platform, ct);
                }
                catch (Exception ex) when (action.Optional && ex is not (OperationCanceledException or AppxOperationTimeoutException))
                {
                    optionalFailures.Add(i);
                    log.LogWarning(ex, "Необязательное действие {Action} твика {Tweak} не выполнено", action.Describe(), tweak.Id);
                    warnings.Add($"{action.Describe()}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            if (ex is RegistryWriteDeniedException)
                _blockedChanges.TryAdd(tweak.Id, ex.Message);

            if (ex is AppxOperationTimeoutException)
            {
                // Windows may complete a deployment even after Cancel. Rolling it back now could race
                // with that deployment; keep the original state until the user can inspect it.
                log.LogError(ex, "Удаление приложения {Tweak} превысило время ожидания; бэкап сохранён", tweak.Id);
                throw new TweakException($"Не удалось применить «{tweak.Title}»: {ex.Message} Исходное состояние сохранено в истории.", ex);
            }
            log.LogError(ex, "Ошибка применения твика {Tweak}, откатываем выполненные действия", tweak.Id);
            var rollbackErrors = new List<string>();
            for (var j = attempted.Count - 1; j >= 0; j--)
            {
                var (i, before) = attempted[j];
                try
                {
                    // Например, отказ в доступе мог произойти до изменения цели.
                    if (await MatchesBackupAsync(tweak.Actions[i], before, CancellationToken.None))
                        continue;
                    await tweak.Actions[i].RevertAsync(platform, before, CancellationToken.None);
                    if (!await MatchesBackupAsync(tweak.Actions[i], before, CancellationToken.None))
                        throw new InvalidOperationException($"Возврат {tweak.Actions[i].Describe()} пока не подтверждён.");
                }
                catch (Exception rollbackEx)
                {
                    log.LogError(rollbackEx, "Не удалось откатить {Action}", tweak.Actions[i].Describe());
                    rollbackErrors.Add($"{tweak.Actions[i].Describe()}: {rollbackEx.Message}");
                }
            }
            if (createdRecord && rollbackErrors.Count == 0)
                backups.Remove(tweak.Id);

            if (ex is OperationCanceledException)
                throw;
            var rollbackMessage = rollbackErrors.Count == 0 ? "" :
                $" Не удалось полностью вернуть исходное состояние; бэкап сохранён. Повторите откат в разделе «История и откат». {string.Join(" ", rollbackErrors)}";
            throw new TweakException($"Не удалось применить «{tweak.Title}»: {ex.Message}{rollbackMessage}", ex);
        }

        var confirmed = true;
        var unconfirmed = new HashSet<int>();
        foreach (var (i, _) in attempted)
        {
            if (optionalFailures.Contains(i)) continue;
            if (await ConfirmAppliedAsync(tweak.Actions[i], ct)) continue;
            unconfirmed.Add(i);
            warnings.Add($"Результат для «{tweak.Actions[i].Describe()}» пока не подтверждён. Бэкап сохранён; проверьте состояние после перезагрузки.");
            if (!tweak.Actions[i].Optional) confirmed = false;
        }
        var servicePending = unconfirmed.Any(i => tweak.Actions[i] is ServiceStartupAction { LastStopPending: true });
        if (servicePending)
            warnings.Add("Одна из служб не остановилась сразу; для завершения отключения перезагрузите Windows.");
        return new TweakResult(warnings, confirmed, servicePending);
    }

    private async Task<bool> ConfirmAppliedAsync(TweakAction action, CancellationToken ct)
    {
        var detected = await SafeDetectAsync(action, ct);
        if (action is AppxPackageAction)
        {
            // Windows can finish removing the package in the background after the deployment returns.
            for (var attempt = 0; attempt < 4 && detected == ActionState.NotApplied; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                platform.Apps.Refresh();
                detected = await SafeDetectAsync(action, ct);
            }
        }
        return detected == ActionState.Applied;
    }

    public async Task RevertAsync(TweakDefinition tweak, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var record = backups.Get(tweak.Id)
            ?? throw new TweakException($"Не удалось откатить «{tweak.Title}»: исходное состояние отсутствует в истории.");
        EnsureCompatibleBackup(tweak, record);
        RefreshExternalState();
        log.LogInformation("Откат твика {Tweak}", tweak.Id);
        var errors = new List<string>();

        for (var i = tweak.Actions.Count - 1; i >= 0; i--)
        {
            if (!record.Actions[i].Applicable) continue;
            var action = tweak.Actions[i];
            try
            {
                ct.ThrowIfCancellationRequested();
                if (await MatchesBackupAsync(action, record.Actions[i].State, ct))
                    continue;
                await action.RevertAsync(platform, record.Actions[i].State, ct);
                if (!await MatchesBackupAsync(action, record.Actions[i].State, ct))
                    throw new InvalidOperationException($"Возврат {action.Describe()} пока не подтверждён. Исходный бэкап сохранён.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Не удалось откатить {Action} твика {Tweak}", action.Describe(), tweak.Id);
                // Необязательность допускает пропуск применения, но не потерю данных для отката.
                errors.Add(ex.Message);
            }
        }

        if (errors.Count > 0)
            throw new TweakException($"Не удалось полностью откатить «{tweak.Title}»: {string.Join(" ", errors.Distinct())}");

        backups.Remove(tweak.Id);
    }

    /// <summary>Подтверждает ручной возврат приложения по бэкапу. Ничего не устанавливает и не удаляет.</summary>
    public async Task<bool> TryConfirmManualAppRestoreAsync(TweakDefinition tweak, CancellationToken ct = default)
    {
        var record = backups.Get(tweak.Id);
        if (record is null || record.Actions.Count != tweak.Actions.Count || tweak.Actions.Count == 0
            || tweak.Actions.Any(a => a is not (AppxPackageAction or CopilotDesktopAction)))
            return false;

        EnsureCompatibleBackup(tweak, record);

        platform.Apps.Refresh();
        for (var i = 0; i < tweak.Actions.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!record.Actions[i].Applicable) continue;
            if (!await MatchesBackupAsync(tweak.Actions[i], record.Actions[i].State, ct))
                return false;
        }

        backups.Remove(tweak.Id);
        log.LogInformation("Ручной возврат приложения {Tweak} подтверждён", tweak.Id);
        return true;
    }

    private async Task<bool> MatchesBackupAsync(TweakAction action, string? captured, CancellationToken ct)
    {
        try
        {
            return action.CapturedStateMatches(await action.CaptureAsync(platform, ct), captured);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ошибка чтения не должна мешать попытке восстановить сохранённое состояние.
            log.LogWarning(ex, "Не удалось сравнить состояние {Action} с бэкапом, выполняем откат", action.Describe());
            return false;
        }
    }

    private async Task<BackupRecord> CaptureAsync(TweakDefinition tweak, CancellationToken ct)
    {
        var actions = new List<ActionBackup>();
        foreach (var action in tweak.Actions)
        {
            ct.ThrowIfCancellationRequested();
            actions.Add(await action.DetectAsync(platform, ct) == ActionState.NotApplicable
                ? new ActionBackup(false, null)
                : new ActionBackup(true, await action.CaptureAsync(platform, ct)));
        }
        return new BackupRecord { TweakId = tweak.Id, Title = tweak.Title, AppliedAt = DateTimeOffset.Now, Actions = actions, ActionTargets = Targets(tweak) };
    }

    private static string[] Targets(TweakDefinition tweak) => tweak.Actions.Select(a => $"{a.GetType().Name}:{a.Describe()}").ToArray();

    private static void EnsureCompatibleBackup(TweakDefinition tweak, BackupRecord record)
    {
        if (record.Actions.Count != tweak.Actions.Count || (record.ActionTargets is not null && !record.ActionTargets.SequenceEqual(Targets(tweak))))
            throw new TweakException($"Состав настройки «{tweak.Title}» изменился с момента сохранения бэкапа. Автоматическое изменение остановлено, исходный бэкап сохранён. Используйте прежнюю версию программы для отката.");
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
