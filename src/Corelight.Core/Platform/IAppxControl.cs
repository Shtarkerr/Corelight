using Microsoft.Extensions.Logging;
using Windows.Management.Deployment;

namespace Corelight.Core.Platform;

public interface IAppxControl
{
    /// <summary>Обновляет сведения о пакетах после установки вне Corelight.</summary>
    void Refresh();

    /// <summary>Установлен ли пакет у любого пользователя или подготовлен (provisioned) для новых пользователей.</summary>
    bool IsInstalled(string packageName);

    /// <summary>Удаляет пакет у всех пользователей и снимает его подготовку, чтобы он не вернулся у новых учётных записей.</summary>
    Task RemoveAsync(string packageName, CancellationToken ct = default);

    /// <summary>Устанавливает приложение заново через winget из Microsoft Store.</summary>
    Task InstallFromStoreAsync(string storeId, CancellationToken ct = default);

    string? GetProvisionedFamily(string packageName) => null;

    Task ProvisionAsync(string packageName, string familyName, CancellationToken ct = default) =>
        throw new NotSupportedException("Восстановление предустановки пакета недоступно.");
}

public sealed class AppxOperationTimeoutException(string packageName, Exception inner, string operation = "удаляет")
    : TimeoutException($"Windows слишком долго {operation} пакет {packageName}. Запрошена отмена; проверьте состояние приложения в разделе «История и откат» перед повторной попыткой.", inner);

public sealed class WindowsAppxControl(IProcessRunner runner, ILogger<WindowsAppxControl> log) : IAppxControl
{
    private static readonly TimeSpan DeploymentTimeout = TimeSpan.FromMinutes(3);
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

    public string? GetProvisionedFamily(string packageName) => _pm.FindProvisionedPackages()
        .Where(p => string.Equals(p.Id.Name, packageName, StringComparison.OrdinalIgnoreCase))
        .Select(p => p.Id.FamilyName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .SingleOrDefault();

    public async Task ProvisionAsync(string packageName, string familyName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.Equals(GetProvisionedFamily(packageName), familyName, StringComparison.OrdinalIgnoreCase)) return;
        if (!_pm.FindPackages().Any(p => string.Equals(p.Id.Name, packageName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Id.FamilyName, familyName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Исходное семейство пакета не найдено после установки. Предустановка не восстановлена.");

        try
        {
            log.LogInformation("Возврат предустановки пакета {Family}", familyName);
            await WaitForDeploymentAsync(_pm.ProvisionPackageForAllUsersAsync(familyName), familyName, ct, "восстанавливает");
        }
        finally
        {
            Invalidate();
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
                await WaitForDeploymentAsync(_pm.RemovePackageAsync(fullName, RemovalOptions.RemoveForAllUsers), fullName, ct);
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
                await WaitForDeploymentAsync(_pm.DeprovisionPackageForAllUsersAsync(family), family, ct);
            }
        }
        finally
        {
            Invalidate();
        }
    }

    private async Task WaitForDeploymentAsync(
        Windows.Foundation.IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> operation,
        string packageName, CancellationToken ct, string verb = "удаляет")
    {
        // WinRT may keep working after the wait ends. The engine must retain its backup on timeout.
        static async Task<DeploymentResult> AwaitOperationAsync(Windows.Foundation.IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> pending)
            => await pending;

        try
        {
            var result = await AwaitOperationAsync(operation).WaitAsync(DeploymentTimeout, ct);
            if (result.ExtendedErrorCode is { HResult: < 0 } error)
                throw new InvalidOperationException($"Операция AppX для пакета {packageName} завершилась ошибкой: {result.ErrorText}", error);
        }
        catch (TimeoutException ex)
        {
            try { operation.Cancel(); }
            catch (Exception cancelEx) { log.LogWarning(cancelEx, "Не удалось отменить операцию AppX для {Package}", packageName); }
            log.LogError(ex, "Windows {Verb} пакет {Package} дольше {Timeout}", verb, packageName, DeploymentTimeout);
            throw new AppxOperationTimeoutException(packageName, ex, verb);
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
        try
        {
            foreach (var p in _pm.FindPackages())
                names.Add(p.Id.Name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            log.LogWarning(ex, "Не удалось получить пакеты всех пользователей; проверяем текущего пользователя");
            foreach (var p in _pm.FindPackagesForUser(string.Empty))
                names.Add(p.Id.Name);
        }
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
