using System.Text;
using Microsoft.Extensions.Logging;
using Corelight.Core.Catalog;
using Corelight.Core.SystemInfo;
using Corelight.Core.Tweaks;
using Corelight.Core.Platform;

namespace Corelight.App.Services;

/// <summary>
/// Единая точка применения и отката твиков из любого раздела: точка восстановления перед первым изменением
/// за сеанс, индикатор занятости, флаг перезагрузки и отчёт об ошибках.
/// </summary>
public sealed class ApplyCoordinator(
    TweakEngine engine,
    TweakCatalog catalog,
    RestorePointService restorePoints,
    DialogService dialogs,
    AppState state,
    ILogger<ApplyCoordinator> log)
{
    private bool _restorePointHandled;

    /// <returns>false, если пользователь отменил операцию или какой-то твик не применился.</returns>
    public async Task<bool> ApplyAsync(IReadOnlyList<TweakDefinition> tweaks, bool showSummary)
    {
        if (tweaks.Count == 0)
            return true;
        if (!await EnsureRestorePointAsync())
            return false;
        return await RunAsync(tweaks, apply: true, showSummary);
    }

    public Task<bool> RevertAsync(IReadOnlyList<TweakDefinition> tweaks, bool showSummary) =>
        tweaks.Count == 0 ? Task.FromResult(true) : RunAsync(tweaks, apply: false, showSummary);

    private async Task<bool> RunAsync(IReadOnlyList<TweakDefinition> tweaks, bool apply, bool showSummary)
    {
        var failures = new List<string>();
        var warnings = new List<string>();
        var succeeded = 0;
        var pending = 0;
        var notStarted = 0;

        using (state.BeginBusy(""))
        {
            for (var i = 0; i < tweaks.Count; i++)
            {
                var tweak = tweaks[i];
                var verb = apply ? "Применение" : "Откат";
                if (tweak.Category == TweakCategory.Apps)
                    verb = apply ? "Удаление" : "Возврат приложения";
                var waitHint = apply && tweak.Category == TweakCategory.Apps ? "\nWindows: до 3 минут на пакет" : "";
                state.BusyText = (tweaks.Count == 1 ? $"{verb}: {tweak.Title}…" : $"{verb} ({i + 1} из {tweaks.Count}): {tweak.Title}…") + waitHint;

                try
                {
                    var confirmed = true;
                    if (apply)
                    {
                        if (engine.GetApplyConflictReason(tweak, catalog.Tweaks) is { } reason)
                            throw new TweakException(reason);
                        var result = await Task.Run(() => engine.ApplyAsync(tweak));
                        warnings.AddRange(result.Warnings.Select(w => $"{tweak.Title}: {w}"));
                        confirmed = result.Confirmed;
                        if (result.RequiresReboot) state.RebootRequired = true;
                    }
                    else
                    {
                        await Task.Run(() => engine.RevertAsync(tweak));
                    }
                    if (confirmed) succeeded++;
                    else pending++;
                    if (tweak.RequiresReboot)
                        state.RebootRequired = true;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Твик {Tweak} не {Action}", tweak.Id, apply ? "применён" : "откачен");
                    failures.Add(ex is TweakException ? ex.Message : $"{tweak.Title}: {ex.Message}");
                    if (apply && ex is TweakException { InnerException: AppxOperationTimeoutException })
                    {
                        notStarted = tweaks.Count - i - 1;
                        break;
                    }
                }
            }
            state.MarkSystemChanged();
        }

        if (failures.Count > 0 || pending > 0 || warnings.Count > 0 || showSummary)
            await ShowSummaryAsync(apply, succeeded, pending, notStarted, failures, warnings);
        return failures.Count == 0 && pending == 0;
    }

    private async Task<bool> EnsureRestorePointAsync()
    {
        if (_restorePointHandled)
            return true;

        RestorePointResult result;
        string? restoreError = null;
        using (state.BeginBusy("Создание точки восстановления Windows… Это может занять до минуты."))
            result = await restorePoints.CreateAsync("Corelight: перед оптимизацией");

        if (result is RestorePointResult.ProtectionDisabled or RestorePointResult.NotCreated)
        {
            var choice = await dialogs.ShowAsync(
                result == RestorePointResult.ProtectionDisabled ? "Защита системы выключена" : "Создание точки не подтверждено",
                "Включите защиту системного диска, чтобы повторить создание точки. Для точек потребуется место на диске.\n\n" +
                "Настройки сохраняются в истории; возврат приложений может требовать интернета и ручной установки.",
                "Включить и создать", "Продолжить без точки");
            if (choice == DialogChoice.Cancel)
                return false;

            if (choice == DialogChoice.Primary)
            {
                using (state.BeginBusy("Включение защиты системы и создание точки восстановления…"))
                {
                    try
                    {
                        await restorePoints.EnableProtectionAsync();
                        result = await restorePoints.CreateAsync("Corelight: перед оптимизацией");
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Не удалось включить защиту системы");
                        result = RestorePointResult.Failed;
                        restoreError = ex.Message;
                    }
                }
            }
            else
            {
                _restorePointHandled = true;
                return true;
            }
        }

        if (result != RestorePointResult.Created)
        {
            var proceed = await dialogs.ConfirmAsync(
                "Точка восстановления не создана",
                "Причина: " + (restoreError ?? restorePoints.LastError) +
                "\n\nНастройки сохранятся в истории. Приложения могут потребовать интернета и ручного возврата. Продолжить?",
                "Продолжить");
            if (!proceed)
                return false;
        }

        _restorePointHandled = true;
        return true;
    }

    private Task ShowSummaryAsync(bool apply, int succeeded, int pending, int notStarted, List<string> failures, List<string> warnings)
    {
        var text = new StringBuilder();
        text.AppendLine(apply ? $"Применено изменений: {succeeded}." : $"Отменено изменений: {succeeded}.");
        if (pending > 0) text.AppendLine($"Результат пока не подтверждён: {pending}.");
        if (notStarted > 0) text.AppendLine($"Остальные изменения не запускались: {notStarted}. Проверьте состояние приложения в истории перед повторной попыткой.");
        if (failures.Count > 0)
        {
            text.AppendLine().AppendLine("Не удалось:");
            foreach (var f in failures)
                text.AppendLine("• " + f);
        }
        if (warnings.Count > 0)
        {
            text.AppendLine().AppendLine("Замечания:");
            foreach (var w in warnings.Take(5))
                text.AppendLine("• " + w);
            if (warnings.Count > 5)
                text.AppendLine($"…и ещё {warnings.Count - 5}. Подробности в журнале.");
        }
        if (state.RebootRequired)
            text.AppendLine().Append("Часть изменений вступит в силу после перезагрузки.");

        var title = failures.Count > 0 ? "Готово, но с ошибками" : pending > 0 || warnings.Count > 0 ? "Готово с замечаниями" : "Готово";
        return dialogs.ShowInfoAsync(title, text.ToString().TrimEnd());
    }
}
