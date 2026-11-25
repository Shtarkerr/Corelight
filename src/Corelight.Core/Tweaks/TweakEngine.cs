using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Corelight.Core.Actions;
using Corelight.Core.Backup;
using Corelight.Core.Platform;
using Corelight.Core.SystemInfo;

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
public enum ApplyOutcome { Applied, AlreadyApplied, NotApplicable, Partial, Unconfirmed }
public sealed record TweakResult(IReadOnlyList<string> Warnings, ApplyOutcome Outcome, bool RequiresReboot = false)
{
    public bool Confirmed => Outcome is ApplyOutcome.Applied or ApplyOutcome.AlreadyApplied;
}
public sealed record TweakDetection(TweakState State, string? Error = null);
public sealed record AppRestoreIssue(TweakDefinition Tweak, string Reason);

public sealed class TweakEngine(PlatformServices platform, IBackupStore backups, ILogger<TweakEngine> log)
{
    private const string AppxRestartRequired = "Предыдущая операция с приложением могла не завершиться. Перезагрузите Windows и повторите проверку или откат. Запись сохранена в истории.";
    private readonly ConcurrentDictionary<string, string> _blockedChanges = new();
    internal Func<DateTimeOffset> ReadBootSession { get; init; } = WindowsBootSession.Read;

    public void RefreshExternalState()
    {
        platform.Apps.Refresh();
        platform.Components?.Refresh();
    }
    public IBackupStore Backups => backups;

    /// <summary>Проверяет доступность возврата приложений перед удалением.</summary>
    public async Task<IReadOnlyList<AppRestoreIssue>> CheckAppRestoresAsync(
        IReadOnlyList<TweakDefinition> tweaks, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var candidates = tweaks.Select(tweak => (Tweak: tweak, Packages: tweak.Actions.OfType<AppxPackageAction>().ToArray(),
                Desktop: tweak.Actions.Any(action => action is CopilotDesktopAction)))
            .Where(candidate => candidate.Packages.Length > 0 || candidate.Desktop).ToArray();
        if (candidates.Length == 0) return [];

        Dictionary<string, AppxPackageState> inventory;
        try
        {
            platform.Apps.Refresh();
            inventory = candidates.SelectMany(candidate => candidate.Packages)
                .Select(package => package.Package).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(package => package, platform.Apps.GetPackageState, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Не удалось проверить установленные пакеты перед удалением");
            return candidates.Select(candidate => new AppRestoreIssue(candidate.Tweak, "Не удалось проверить установленные пакеты.")).ToArray();
        }

        var storeIds = candidates.SelectMany(candidate => candidate.Packages)
            .Where(package => inventory[package.Package].CurrentUser).Select(package => package.StoreId).OfType<string>()
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
        foreach (var (tweak, packages, desktop) in candidates)
        {
            var reasons = packages.Where(package => inventory[package.Package].Present)
                .Select(package => !inventory[package.Package].CurrentUser
                    ? "Приложение недоступно для удаления в текущем профиле."
                    : package.StoreId is { } id
                    ? DescribeRestoreAvailability(results.GetValueOrDefault(id, StoreRestoreAvailability.TimedOut))
                    : tweak.RestoreHelpUrl is null ? "Автоматического возврата нет." : "Возврат только вручную.")
                .OfType<string>().ToList();
            if (desktop)
            {
                try
                {
                    if (platform.CopilotDesktop?.IsInstalled() == true)
                        reasons.Add("Возврат только вручную.");
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Не удалось проверить настольный Copilot перед удалением");
                    reasons.Add("Не удалось проверить настольный Copilot.");
                }
            }
            if (reasons.Count > 0)
                issues.Add(new(tweak, string.Join(" ", reasons.Distinct())));
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

        try
        {
            if (backups.Get(tweak.Id) is { } record && record.Actions.Count == tweak.Actions.Count &&
                UnsettledAppxActions(tweak, record).Any(i => IsInCurrentBoot(record.Actions[i])))
                return new(TweakState.Unknown, AppxRestartRequired);
        }
        catch (TweakException ex)
        {
            return new(TweakState.Unknown, ex.Message);
        }

        var states = new List<ActionState>();
        foreach (var action in tweak.Actions)
        {
            try { states.Add(await action.DetectAsync(platform, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Не удалось определить состояние {Action}", action.Describe());
                return new(TweakState.Unknown, ex.Message);
            }
        }

        return new(Summarize(tweak, states));
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

        var original = backups.Get(tweak.Id);
        if (original is not null)
        {
            EnsureCompatibleBackup(tweak, original);
            RequireSettledAppx(tweak, original);
        }

        var initial = new ActionState[tweak.Actions.Count];
        for (var i = 0; i < initial.Length; i++)
            initial[i] = await tweak.Actions[i].DetectAsync(platform, ct);

        var initialState = Summarize(tweak, initial);
        if (initialState is TweakState.Applied or TweakState.NotApplicable)
            return new TweakResult([], initialState == TweakState.Applied ? ApplyOutcome.AlreadyApplied : ApplyOutcome.NotApplicable);

        var record = original ?? new BackupRecord
        {
            TweakId = tweak.Id,
            Title = tweak.Title,
            AppliedAt = DateTimeOffset.Now,
            Actions = initial.Select(_ => Untouched).ToArray(),
            ActionTargets = Targets(tweak),
        };
        var attempted = new List<(int Index, string? Before)>();
        var warnings = new List<string>();
        var applied = 0;
        var incomplete = 0;
        var servicePending = false;
        try
        {
            for (var i = 0; i < initial.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (initial[i] != ActionState.NotApplied) continue;

                var action = tweak.Actions[i];
                var current = record.Actions[i];
                var before = await action.CaptureAsync(platform, ct);
                if (await action.DetectAsync(platform, ct) != ActionState.NotApplied)
                    continue;

                var snapshot = current.NeedsRestore ? current : new ActionBackup(true, before, ActionBackupPhase.Pending);
                async Task ChangeAsync()
                {
                    attempted.Add((i, before));
                    if (action is PowerPlanAction plan)
                        await plan.ApplyAsync(platform, fingerprint => record = SaveAction(record, i,
                            record.Actions[i] with { PowerCopyFingerprint = fingerprint }), ct);
                    else
                        await action.ApplyAsync(platform, ct);
                }

                try
                {
                    if (action is AppxPackageAction)
                        await RunAppxMutationAsync(ChangeAsync, boot => record = SaveAction(record, i, WithAppxBoot(snapshot, boot)));
                    else
                    {
                        if (!current.NeedsRestore)
                            record = SaveAction(record, i, snapshot);
                        await ChangeAsync();
                    }
                }
                catch (Exception ex) when (action.Optional && ex is not OperationCanceledException &&
                    record.Actions[i].AppxStartedInBoot is null)
                {
                    log.LogWarning(ex, "Необязательное действие {Action} твика {Tweak} не выполнено", action.Describe(), tweak.Id);
                    warnings.Add($"{action.Describe()}: {ex.Message}");
                    incomplete++;
                    if (!current.NeedsRestore && await MatchesBackupAsync(action, before, ct))
                        record = SaveAction(record, i, Untouched);
                    continue;
                }

                if (await ConfirmAppliedAsync(action, ct))
                {
                    applied++;
                    if (record.Actions[i].Phase == ActionBackupPhase.Pending)
                        record = SaveAction(record, i, record.Actions[i] with { Phase = ActionBackupPhase.Applied, AppxOperationReturned = false });
                }
                else
                {
                    incomplete++;
                    warnings.Add($"Результат для «{action.Describe()}» пока не подтверждён. Бэкап сохранён.");
                    servicePending |= action is ServiceStartupAction { LastStopPending: true };
                }
            }

            if (attempted.Count > 0 && !record.Actions.Any(action => action.NeedsRestore))
                backups.Remove(tweak.Id);
        }
        catch (AppxOperationTimeoutException ex)
        {
            log.LogError(ex, "Операция AppX для {Tweak} не завершилась; бэкап сохранён", tweak.Id);
            throw new TweakException($"Не удалось применить «{tweak.Title}»: {ex.Message} {AppxRestartRequired}", ex);
        }
        catch (Exception ex)
        {
            if (ex is RegistryWriteDeniedException)
                _blockedChanges.TryAdd(tweak.Id, ex.Message);

            if (attempted.Any(item => record.Actions[item.Index].AppxStartedInBoot is not null))
            {
                log.LogError(ex, "Исход операции AppX для {Tweak} не подтверждён; бэкап сохранён", tweak.Id);
                throw new TweakException(AppxRestartRequired, ex);
            }

            log.LogError(ex, "Ошибка применения твика {Tweak}, откатываем выполненные действия", tweak.Id);
            var rollbackErrors = new List<string>();
            for (var j = attempted.Count - 1; j >= 0; j--)
            {
                var (i, before) = attempted[j];
                try
                {
                    var action = tweak.Actions[i];
                    await RestoreActionAsync(action, before, CancellationToken.None,
                        boot => record = SaveAction(record, i, WithAppxBoot(record.Actions[i], boot)));
                    var previous = original?.Actions[i] ?? Untouched;
                    if (tweak.Actions[i] is PowerPlanAction plan &&
                        record.Actions[i].PowerCopyFingerprint != previous.PowerCopyFingerprint)
                        await plan.RemoveUnchangedCopyAsync(platform, record.Actions[i].PowerCopyFingerprint, CancellationToken.None);
                    if (record.Actions[i] != previous)
                        record = SaveAction(record, i, previous);
                }
                catch (Exception rollbackEx)
                {
                    log.LogError(rollbackEx, "Не удалось откатить {Action}", tweak.Actions[i].Describe());
                    rollbackErrors.Add($"{tweak.Actions[i].Describe()}: {rollbackEx.Message}");
                }
            }
            if (original is null && attempted.Count > 0 && !record.Actions.Any(action => action.NeedsRestore))
                backups.Remove(tweak.Id);

            if (ex is OperationCanceledException)
                throw;
            var rollbackMessage = rollbackErrors.Count == 0 ? "" :
                $" Не удалось полностью вернуть исходное состояние; бэкап сохранён. Повторите откат в разделе «История и откат». {string.Join(" ", rollbackErrors)}";
            throw new TweakException($"Не удалось применить «{tweak.Title}»: {ex.Message}{rollbackMessage}", ex);
        }

        if (servicePending)
            warnings.Add("Одна из служб не остановилась сразу. Для завершения отключения перезагрузите Windows.");
        if (incomplete > 0)
            return new TweakResult(warnings, applied > 0 ? ApplyOutcome.Partial : ApplyOutcome.Unconfirmed, servicePending);
        if (applied > 0)
            return new TweakResult(warnings, ApplyOutcome.Applied);

        var detection = await DetectDetailedAsync(tweak, ct);
        var outcome = detection.State switch
        {
            TweakState.Applied => ApplyOutcome.AlreadyApplied,
            TweakState.NotApplicable => ApplyOutcome.NotApplicable,
            _ => ApplyOutcome.Unconfirmed,
        };
        return new TweakResult(warnings, outcome);
    }

    private static readonly ActionBackup Untouched = new(false, null, ActionBackupPhase.Untouched);

    private static IEnumerable<int> UnsettledAppxActions(TweakDefinition tweak, BackupRecord record) =>
        Enumerable.Range(0, tweak.Actions.Count).Where(i => tweak.Actions[i] is AppxPackageAction &&
            (record.Actions[i].AppxStartedInBoot is not null ||
             record.Actions[i].Phase == ActionBackupPhase.Pending && !record.Actions[i].AppxOperationReturned));

    private bool IsInCurrentBoot(ActionBackup action) =>
        action.AppxStartedInBoot is not { } started || GetBootSession() <= started;

    private void RequireSettledAppx(TweakDefinition tweak, BackupRecord record)
    {
        var pending = UnsettledAppxActions(tweak, record).Where(i => IsInCurrentBoot(record.Actions[i])).ToArray();
        if (pending.Length == 0) return;

        // Старые записи Pending не хранили время загрузки. Отсчёт начинается при первой попытке восстановления.
        if (pending.Any(i => record.Actions[i].AppxStartedInBoot is null))
        {
            var entries = record.Actions.ToArray();
            var boot = GetBootSession();
            foreach (var i in pending)
                if (entries[i].AppxStartedInBoot is null)
                    entries[i] = WithAppxBoot(entries[i], boot);
            backups.Save(record with { Actions = entries });
        }
        throw new TweakException(AppxRestartRequired);
    }

    private DateTimeOffset GetBootSession()
    {
        try { return ReadBootSession(); }
        catch (Exception ex)
        {
            throw new TweakException("Не удалось проверить время последней загрузки Windows. Изменение приложения остановлено.", ex);
        }
    }

    private static ActionBackup WithAppxBoot(ActionBackup snapshot, DateTimeOffset? boot) =>
        snapshot with
        {
            AppxStartedInBoot = boot,
            // Отличаем завершённое ожидание от старых записей Pending без отметки загрузки.
            AppxOperationReturned = boot is null && snapshot.Phase == ActionBackupPhase.Pending,
        };

    private async Task RunAppxMutationAsync(Func<Task> mutate, Action<DateTimeOffset?> saveBoot)
    {
        saveBoot(GetBootSession());
        try { await mutate(); }
        catch (Exception ex) when (ex is not (TimeoutException or OperationCanceledException))
        {
            saveBoot(null);
            throw;
        }
        saveBoot(null);
    }

    private static TweakState Summarize(TweakDefinition tweak, IEnumerable<ActionState> states)
    {
        var relevant = states.Where(state => state != ActionState.NotApplicable).ToList();
        if (relevant.Count == 0) return TweakState.NotApplicable;
        var effective = tweak.AppliedWhenAnyActionMatches
            ? relevant.Any(state => state == ActionState.Applied)
            : relevant.All(state => state == ActionState.Applied);
        return effective ? TweakState.Applied : TweakState.NotApplied;
    }

    private BackupRecord SaveAction(BackupRecord record, int index, ActionBackup action)
    {
        var entries = record.Actions.ToArray();
        entries[index] = action;
        var updated = record with { Actions = entries };
        backups.Save(updated);
        return updated;
    }

    private async Task RestoreActionAsync(TweakAction action, string? state, CancellationToken ct, Action<DateTimeOffset?> saveAppxBoot)
    {
        if (await MatchesBackupAsync(action, state, ct)) return;

        if (action is AppxPackageAction)
            await RunAppxMutationAsync(() => action.RevertAsync(platform, state, ct), saveAppxBoot);
        else
            await action.RevertAsync(platform, state, ct);
        if (!await MatchesBackupAsync(action, state, ct))
            throw new InvalidOperationException($"Возврат {action.Describe()} пока не подтверждён. Исходный бэкап сохранён.");
    }

    private async Task<bool> ConfirmAppliedAsync(TweakAction action, CancellationToken ct)
    {
        var detected = await SafeDetectAsync(action, ct);
        if (action is AppxPackageAction)
        {
            // Windows can finish removing the package in the background after the deployment returns.
            for (var attempt = 0; attempt < 4 && detected != ActionState.Applied; attempt++)
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
        RequireSettledAppx(tweak, record);
        RefreshExternalState();
        log.LogInformation("Откат твика {Tweak}", tweak.Id);
        var errors = new List<string>();

        for (var i = tweak.Actions.Count - 1; i >= 0; i--)
        {
            if (!record.Actions[i].NeedsRestore) continue;
            var action = tweak.Actions[i];
            try
            {
                ct.ThrowIfCancellationRequested();
                await RestoreActionAsync(action, record.Actions[i].State, ct,
                    boot => record = SaveAction(record, i, WithAppxBoot(record.Actions[i], boot)));
                if (action is PowerPlanAction plan)
                    await plan.RemoveUnchangedCopyAsync(platform, record.Actions[i].PowerCopyFingerprint, ct);
                record = SaveAction(record, i, Untouched);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Не удалось откатить {Action} твика {Tweak}", action.Describe(), tweak.Id);
                if (record.Actions[i].AppxStartedInBoot is not null)
                    throw new TweakException(AppxRestartRequired, ex);
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
        RequireSettledAppx(tweak, record);

        platform.Apps.Refresh();
        for (var i = 0; i < tweak.Actions.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!record.Actions[i].NeedsRestore) continue;
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
