using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Corelight.Core.Platform;

[Flags]
public enum OneDriveScope { None = 0, CurrentUser = 1, AllUsers = 2 }

public interface IOneDriveControl
{
    OneDriveScope GetInstalledScope();
    Task UninstallAsync(CancellationToken ct = default);
    Task RestoreAsync(OneDriveScope scope, CancellationToken ct = default);
}

internal sealed record OneDriveLocations(string UserRoot, string MachineRoot, string MachineX86Root,
    string WindowsRoot, string DataRoot, string ProfileRoot)
{
    public static OneDriveLocations Current => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "OneDrive"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft OneDrive"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft OneDrive"),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), AppPaths.DataDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}

/// <summary>Только штатный клиент синхронизации OneDrive. Пользовательские файлы не удаляются.</summary>
public sealed class WindowsOneDriveControl : IOneDriveControl
{
    private readonly IRegistryAccess _registry;
    private readonly IProcessRunner _runner;
    private readonly ILogger<WindowsOneDriveControl> _log;
    private readonly OneDriveLocations _locations;

    public WindowsOneDriveControl(IRegistryAccess registry, IProcessRunner runner, ILogger<WindowsOneDriveControl> log)
        : this(registry, runner, log, OneDriveLocations.Current) { }

    internal WindowsOneDriveControl(IRegistryAccess registry, IProcessRunner runner,
        ILogger<WindowsOneDriveControl> log, OneDriveLocations locations)
        => (_registry, _runner, _log, _locations) = (registry, runner, log, locations);

    public OneDriveScope GetInstalledScope()
    {
        var scope = OneDriveScope.None;
        if (File.Exists(Path.Combine(_locations.UserRoot, "OneDrive.exe"))) scope |= OneDriveScope.CurrentUser;
        if (MachineRoots().Any(root => File.Exists(Path.Combine(root, "OneDrive.exe")))) scope |= OneDriveScope.AllUsers;
        return scope;
    }

    public async Task UninstallAsync(CancellationToken ct = default)
    {
        var scope = GetInstalledScope();
        if (scope == OneDriveScope.None) return;
        RequireSupportedScope(scope);
        var redirected = RedirectedFolders();
        if (redirected.Count > 0)
            throw new InvalidOperationException($"OneDrive используется для папок: {string.Join(", ", redirected)}. Сначала отключите их резервное копирование в настройках OneDrive и убедитесь, что нужные файлы сохранены на компьютере.");

        var installer = FindInstaller(scope) ?? throw new InvalidOperationException(
            "Штатный установщик OneDrive не найден. Удаление отменено, чтобы сохранить возможность возврата клиента.");
        await VerifyMicrosoftSignatureAsync(installer, ct);
        var cached = CachedInstaller(scope);
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        File.Copy(installer, cached, overwrite: true);
        await VerifyMicrosoftSignatureAsync(cached, ct);
        _log.LogInformation("Установщик OneDrive сохранён для возврата: {Path}, область {Scope}", cached, scope);
        await RunInstallerAsync(cached, scope == OneDriveScope.AllUsers ? ["/uninstall", "/allusers"] : ["/uninstall"], ct);
        // Сохраняем запись отката и при отложенном завершении удаления после перезагрузки.
        if (GetInstalledScope() != OneDriveScope.None)
            _log.LogWarning("После запуска удаления OneDrive ещё определяется в системе; может потребоваться перезагрузка");
    }

    public async Task RestoreAsync(OneDriveScope scope, CancellationToken ct = default)
    {
        RequireSupportedScope(scope);
        var current = GetInstalledScope();
        if (current == scope) return;
        if (current != OneDriveScope.None)
            throw new InvalidOperationException("OneDrive уже установлен другим способом. Проверьте установку вручную; запись отката сохранена.");
        var cached = CachedInstaller(scope);
        if (!File.Exists(cached))
            throw new InvalidOperationException("Сохранённый установщик OneDrive не найден. Используйте «Как вернуть» и официальный установщик Microsoft, затем повторите откат.");
        await VerifyMicrosoftSignatureAsync(cached, ct);
        await RunInstallerAsync(cached, scope == OneDriveScope.AllUsers ? ["/silent", "/allusers"] : ["/silent"], ct);
        for (var i = 0; i < 20 && GetInstalledScope() != scope; i++)
            await Task.Delay(500, ct);
        if (GetInstalledScope() != scope)
            throw new InvalidOperationException("Установка запущена, но возврат OneDrive ещё не подтверждён. Перезагрузите Windows и повторите откат. Запись сохранена.");
    }

    private static void RequireSupportedScope(OneDriveScope scope)
    {
        if (scope is not (OneDriveScope.CurrentUser or OneDriveScope.AllUsers))
            throw new InvalidOperationException("Обнаружены несколько вариантов установки OneDrive. Используйте штатные «Установленные приложения» Windows для ручной проверки.");
    }

    internal string CachedInstaller(OneDriveScope scope)
    {
        var owner = scope == OneDriveScope.CurrentUser
            ? "CurrentUser-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_locations.UserRoot.ToUpperInvariant())))[..16]
            : "AllUsers";
        return Path.Combine(_locations.DataRoot, "installers", "OneDrive", owner, "OneDriveSetup.exe");
    }

    private IEnumerable<string> MachineRoots() => new[] { _locations.MachineRoot, _locations.MachineX86Root }.Distinct(StringComparer.OrdinalIgnoreCase);

    private string? FindInstaller(OneDriveScope scope)
    {
        var roots = scope == OneDriveScope.CurrentUser ? new[] { _locations.UserRoot } : MachineRoots();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.GetDirectories(root)
                .Where(path => Version.TryParse(Path.GetFileName(path), out _))
                .OrderByDescending(path => Version.Parse(Path.GetFileName(path))))
            {
                var candidate = Path.Combine(directory, "OneDriveSetup.exe");
                if (File.Exists(candidate)) return candidate;
            }
            var direct = Path.Combine(root, "OneDriveSetup.exe");
            if (File.Exists(direct)) return direct;
        }
        return new[] { "System32", "SysWOW64" }.Select(folder => Path.Combine(_locations.WindowsRoot, folder, "OneDriveSetup.exe"))
            .FirstOrDefault(File.Exists);
    }

    private async Task VerifyMicrosoftSignatureAsync(string path, CancellationToken ct)
    {
        if (!await MicrosoftInstallerSignature.IsValidAsync(_runner, path, ct))
            throw new InvalidOperationException("Проверка подписи Microsoft у установщика OneDrive не пройдена. Установка или удаление не выполнялись.");
    }

    private async Task RunInstallerAsync(string path, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var result = await _runner.RunAsync(path, arguments, TimeSpan.FromMinutes(15), ct);
        if (result.ExitCode is not (0 or 3010))
        {
            _log.LogWarning("Установщик OneDrive вернул {Code}. Вывод: {Output}; {Error}", result.ExitCode, result.Output, result.Error);
            throw new InvalidOperationException($"Установщик OneDrive завершился с кодом {result.ExitCode}. Закройте OneDrive через значок в области уведомлений и повторите действие. Подробности в журнале.");
        }
    }

    private List<string> RedirectedFolders()
    {
        var roots = new List<string> { Path.Combine(_locations.ProfileRoot, "OneDrive") };
        const string accounts = @"HKCU\Software\Microsoft\OneDrive\Accounts";
        foreach (var account in _registry.GetSubKeyNames(accounts))
            if (_registry.GetValue(accounts + "\\" + account, "UserFolder")?.Data is { Length: > 0 } root) roots.Add(root);
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } root) roots.Add(root);

        var normalizedRoots = roots.Select(NormalizePath).ToArray();
        const string folders = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
        var redirected = new List<string>();
        foreach (var (name, title) in new[] { ("Desktop", "Рабочий стол"), ("Personal", "Документы"), ("My Pictures", "Изображения") })
        {
            var value = _registry.GetValue(folders, name)?.Data;
            if (string.IsNullOrWhiteSpace(value)) continue;
            var path = NormalizePath(value);
            if (normalizedRoots.Any(root => path.Equals(root, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) redirected.Add(title);
        }
        return redirected;
    }

    private static string NormalizePath(string path) => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)).TrimEnd('\\');
}
