using Microsoft.Extensions.Logging;
using Windows.Management.Deployment;

namespace Corelight.Core.Platform;

public interface IAppxControl
{
    /// <summary>Обновляет сведения о пакетах после установки вне Corelight.</summary>
    void Refresh();

    /// <summary>Установлен ли пакет у текущего пользователя или подготовлен (provisioned) для новых пользователей.</summary>
    bool IsInstalled(string packageName);

    /// <summary>Удаляет пакет у всех пользователей и снимает его подготовку, чтобы он не вернулся у новых учётных записей.</summary>
    Task RemoveAsync(string packageName, CancellationToken ct = default);

    /// <summary>Устанавливает приложение заново через winget из Microsoft Store.</summary>
    Task InstallFromStoreAsync(string storeId, CancellationToken ct = default);
}

public sealed class WindowsAppxControl(IProcessRunner runner, ILogger<WindowsAppxControl> log) : IAppxControl
{
    private readonly PackageManager _pm = new();
    private readonly Lock _gate = new();
    private HashSet<string>? _installed;

    public void Refresh() => Invalidate();

    public bool IsInstalled(string packageName)
    {
        lock (_gate)
        {
            _installed ??= LoadInstalledNames();
            return _installed.Contains(packageName);
        }
    }

    public async Task RemoveAsync(string packageName, CancellationToken ct = default)
    {
        try
        {
            // Один и тот же пакет, установленный нескольким пользователям, может встретиться в списке несколько раз.
            var fullNames = _pm.FindPackages()
                .Where(p => string.Equals(p.Id.Name, packageName, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Id.FullName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var fullName in fullNames)
            {
                ct.ThrowIfCancellationRequested();
                log.LogInformation("Удаление пакета {Package}", fullName);
                await _pm.RemovePackageAsync(fullName, RemovalOptions.RemoveForAllUsers);
            }

            var provisioned = _pm.FindProvisionedPackages()
                .Where(p => string.Equals(p.Id.Name, packageName, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Id.FamilyName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var family in provisioned)
            {
                ct.ThrowIfCancellationRequested();
                log.LogInformation("Снятие подготовки пакета {Family}", family);
                await _pm.DeprovisionPackageForAllUsersAsync(family);
            }
        }
        finally
        {
            Invalidate();
        }
    }

    public async Task InstallFromStoreAsync(string storeId, CancellationToken ct = default)
    {
        try
        {
            var result = await runner.RunAsync("winget.exe",
                ["install", "--id", storeId, "--source", "msstore", "--exact", "--silent",
                 "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
                TimeSpan.FromMinutes(15), ct);
            if (!result.Succeeded)
            {
                log.LogWarning("Установка приложения Store {StoreId}: winget вернул {Code} (0x{HexCode}). Вывод: {Output}. Ошибки: {Error}",
                    storeId, result.ExitCode, unchecked((uint)result.ExitCode).ToString("X8"), result.Output, result.Error);
                var message = result.ExitCode switch
                {
                    unchecked((int)0x8A150014) => "Приложение не найдено для автоматической установки. Возможны ограничения региона Windows или Microsoft Store; одного VPN может быть недостаточно. Откройте страницу кнопкой «Открыть в Store», попробуйте ручную установку и повторите откат.",
                    unchecked((int)0x8A150010) => "В каталоге winget нет подходящего установщика. Попробуйте установку кнопкой «Открыть в Store», затем повторите откат.",
                    _ => $"Microsoft Store не смог установить приложение (код 0x{unchecked((uint)result.ExitCode):X8}). Подробности сохранены в журнале. Можно попробовать установку кнопкой «Открыть в Store»."
                };
                throw new InvalidOperationException(
                    message);
            }
        }
        finally
        {
            Invalidate();
        }
    }

    private void Invalidate()
    {
        lock (_gate)
            _installed = null;
    }

    private HashSet<string> LoadInstalledNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _pm.FindPackagesForUser(string.Empty))
            names.Add(p.Id.Name);
        try
        {
            foreach (var p in _pm.FindProvisionedPackages())
                names.Add(p.Id.Name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Без прав администратора список подготовленных пакетов недоступен — хватит пакетов пользователя.
            log.LogWarning(ex, "Не удалось получить список подготовленных пакетов");
        }
        return names;
    }
}
