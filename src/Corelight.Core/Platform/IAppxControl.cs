using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel;
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

    Task<StoreRestoreAvailability> CheckStoreRestoreAsync(string storeId, CancellationToken ct);

    string? GetProvisionedFamily(string packageName) => null;

    Task ProvisionAsync(string packageName, string familyName, CancellationToken ct = default) =>
        throw new NotSupportedException("Восстановление предустановки пакета недоступно.");
}

public enum StoreRestoreAvailability
{
    Found,
    NotFound,
    NoApplicableInstaller,
    AgreementsRequired,
    WinGetUnavailable,
    TimedOut,
    Unverified,
}

public sealed class AppxOperationTimeoutException(string packageName, Exception inner, string operation = "удаляет")
    : TimeoutException($"Windows слишком долго {operation} пакет {packageName}. Запрошена отмена; проверьте состояние приложения в разделе «История и откат» перед повторной попыткой.", inner);

public sealed class WindowsAppxControl(IProcessRunner runner, ILogger<WindowsAppxControl> log) : IAppxControl
{
    private static readonly TimeSpan DeploymentTimeout = TimeSpan.FromMinutes(3);
    private const int NoPackagesFound = unchecked((int)0x8A150014);
    private const int NoApplicableInstaller = unchecked((int)0x8A150010);
    private const int SourceAgreementsRequired = unchecked((int)0x8A150046);
    private readonly PackageManager _pm = new();
    private readonly Lock _gate = new();
    private HashSet<string>? _installed;
    internal Func<string?>? FindWinGet { get; init; }

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
            var result = await runner.RunAsync(GetWinGetPath(),
                ["install", "--id", storeId, "--source", "msstore", "--exact", "--silent",
                 "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
                TimeSpan.FromMinutes(15), ct);
            if (!result.Succeeded)
            {
                log.LogWarning("Установка приложения Store {StoreId}: winget вернул {Code} (0x{HexCode}). Вывод: {Output}. Ошибки: {Error}",
                    storeId, result.ExitCode, unchecked((uint)result.ExitCode).ToString("X8"), result.Output, result.Error);
                var message = result.ExitCode switch
                {
                    NoPackagesFound => "Приложение не найдено для автоматической установки. Возможны ограничения региона Windows или Microsoft Store; одного VPN может быть недостаточно. Откройте страницу кнопкой «Открыть в Store», попробуйте ручную установку и повторите откат.",
                    NoApplicableInstaller => "В каталоге winget нет подходящего установщика. Попробуйте установку кнопкой «Открыть в Store», затем повторите откат.",
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

    public async Task<StoreRestoreAvailability> CheckStoreRestoreAsync(string storeId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var result = await runner.RunAsync(GetWinGetPath(),
                ["show", "--id", storeId, "--source", "msstore", "--exact", "--disable-interactivity"],
                TimeSpan.FromSeconds(8), ct);
            if (result.Succeeded) return StoreRestoreAvailability.Found;

            log.LogInformation("Проверка возврата Store {StoreId}: WinGet вернул 0x{HexCode}",
                storeId, unchecked((uint)result.ExitCode).ToString("X8"));
            return result.ExitCode switch
            {
                NoPackagesFound => StoreRestoreAvailability.NotFound,
                NoApplicableInstaller => StoreRestoreAvailability.NoApplicableInstaller,
                SourceAgreementsRequired => StoreRestoreAvailability.AgreementsRequired,
                _ => StoreRestoreAvailability.Unverified,
            };
        }
        catch (TimeoutException ex)
        {
            log.LogInformation(ex, "Проверка возврата Store {StoreId} превысила время ожидания", storeId);
            return StoreRestoreAvailability.TimedOut;
        }
        catch (Win32Exception ex)
        {
            ct.ThrowIfCancellationRequested();
            log.LogWarning(ex, "Не удалось запустить WinGet для проверки возврата {StoreId}", storeId);
            return StoreRestoreAvailability.WinGetUnavailable;
        }
        catch (FileNotFoundException ex)
        {
            log.LogWarning(ex, "Не найден WinGet для проверки возврата {StoreId}", storeId);
            return StoreRestoreAvailability.WinGetUnavailable;
        }
    }

    private string GetWinGetPath()
    {
        // Используем установленный App Installer текущей учётной записи, без поиска EXE в PATH.
        var path = FindWinGet is not null ? FindWinGet() :
            _pm.FindPackagesForUser("", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe")
                .Where(package => !package.IsDevelopmentMode && package.SignatureKind is PackageSignatureKind.Store or PackageSignatureKind.System)
                .OrderByDescending(package => (package.Id.Version.Major, package.Id.Version.Minor, package.Id.Version.Build, package.Id.Version.Revision))
                .Select(package => Path.Combine(package.InstalledLocation.Path, "winget.exe"))
                .FirstOrDefault(File.Exists);
        return path ?? throw new FileNotFoundException("WinGet не найден в установленном «Установщике приложений» Windows.");
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
