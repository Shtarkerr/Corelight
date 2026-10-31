using System.Text;
using Microsoft.Extensions.Logging;
using Corelight.Core.Catalog;
using Corelight.Core.SystemInfo;
using Corelight.Core.Tweaks;

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

        using (state.BeginBusy(""))
        {
            for (var i = 0; i < tweaks.Count; i++)
            {
                var tweak = tweaks[i];
                var verb = apply ? "Применение" : "Откат";
                if (tweak.Category == TweakCategory.Apps)
                    verb = apply ? "Удаление" : "Установка из Microsoft Store";
                state.BusyText = tweaks.Count == 1 ? $"{verb}: {tweak.Title}…" : $"{verb} ({i + 1} из {tweaks.Count}): {tweak.Title}…";

                try
                {
                    if (apply)
                    {
                        // Например, «Высокая производительность» перед «Максимальной»: сначала вернуть исходную схему.
                        await Task.Run(() => engine.RevertConflictsAsync(tweak, catalog.Tweaks));
                        var result = await Task.Run(() => engine.ApplyAsync(tweak));
                        warnings.AddRange(result.Warnings.Select(w => $"{tweak.Title}: {w}"));
                    }
                    else
                    {
                        await Task.Run(() => engine.RevertAsync(tweak));
                    }
                    succeeded++;
                    if (tweak.RequiresReboot)
                        state.RebootRequired = true;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Твик {Tweak} не {Action}", tweak.Id, apply ? "применён" : "откачен");
                    failures.Add(ex is TweakException ? ex.Message : $"{tweak.Title}: {ex.Message}");
                }
            }
            state.MarkSystemChanged();
        }

        if (failures.Count > 0 || showSummary)
            await ShowSummaryAsync(apply, succeeded, failures, warnings);
        return failures.Count == 0;
    }

    private async Task<bool> EnsureRestorePointAsync()
    {
        if (_restorePointHandled)
            return true;

        RestorePointResult result;
        using (state.BeginBusy("Создание точки восстановления Windows… Это может занять до минуты."))
            result = await restorePoints.CreateAsync("Corelight: перед оптимизацией");

        if (result == RestorePointResult.ProtectionDisabled)
        {
            var choice = await dialogs.ShowAsync(
                "Защита системы выключена",
                "Чтобы создать точку восстановления, нужно включить защиту системы для системного диска. " +
                "Windows выделит под точки восстановления небольшую часть диска.\n\n" +
                "Даже без точки восстановления каждое изменение можно будет откатить в разделе «История и откат».",
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
                "Windows не смогла создать точку восстановления. Каждое изменение всё равно можно будет откатить " +
                "в разделе «История и откат». Продолжить?",
                "Продолжить");
            if (!proceed)
                return false;
        }

        _restorePointHandled = true;
        return true;
    }

    private Task ShowSummaryAsync(bool apply, int succeeded, List<string> failures, List<string> warnings)
    {
        var text = new StringBuilder();
        text.AppendLine(apply ? $"Применено изменений: {succeeded}." : $"Откачено изменений: {succeeded}.");
        if (failures.Count > 0)
        {
            text.AppendLine().AppendLine("Не удалось:");
            foreach (var f in failures)
                text.AppendLine("• " + f);
        }
        if (warnings.Count > 0)
        {
            text.AppendLine().AppendLine("Часть второстепенных изменений Windows не разрешила (это не критично):");
            foreach (var w in warnings.Take(5))
                text.AppendLine("• " + w);
            if (warnings.Count > 5)
                text.AppendLine($"…и ещё {warnings.Count - 5}. Подробности в журнале.");
        }
        if (state.RebootRequired)
            text.AppendLine().Append("Часть изменений вступит в силу после перезагрузки.");

        var title = failures.Count == 0 ? "Готово" : "Готово, но с ошибками";
        return dialogs.ShowInfoAsync(title, text.ToString().TrimEnd());
    }
}
