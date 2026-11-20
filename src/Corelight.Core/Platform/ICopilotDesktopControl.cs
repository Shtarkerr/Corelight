namespace Corelight.Core.Platform;

public interface ICopilotDesktopControl
{
    bool IsInstalled();
    Task UninstallAsync(CancellationToken ct = default);
}

/// <summary>Настольный Copilot с записью удаления Win32 и штатным деинсталлятором Microsoft.</summary>
public sealed class WindowsCopilotDesktopControl : ICopilotDesktopControl
{
    private const string UninstallKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Copilot";
    private const string UninstallKeyX86 = @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Copilot";

    private readonly IRegistryAccess _registry;
    private readonly IProcessRunner _runner;
    private readonly Func<string, bool> _fileExists;
    private readonly (string Key, string Root)[] _locations;

    public WindowsCopilotDesktopControl(IRegistryAccess registry, IProcessRunner runner)
        : this(registry, runner, File.Exists,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)) { }

    internal WindowsCopilotDesktopControl(IRegistryAccess registry, IProcessRunner runner,
        Func<string, bool> fileExists, string programFiles, string programFilesX86)
    {
        (_registry, _runner, _fileExists) = (registry, runner, fileExists);
        _locations =
        [
            (UninstallKey, Path.Combine(programFiles, "Microsoft", "Copilot", "Application")),
            (UninstallKeyX86, Path.Combine(programFilesX86, "Microsoft", "Copilot", "Application")),
        ];
    }

    public bool IsInstalled() => InstalledLocations().Any();

    public async Task UninstallAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var installations = InstalledLocations().ToArray();
        if (installations.Length == 0) return;
        if (installations.Length > 1)
            throw new InvalidOperationException("Найдено несколько установок Copilot. Проверьте их в разделе «Установленные приложения» Windows.");

        var (key, root) = installations[0];
        if (!Version.TryParse(_registry.GetValue(key, "DisplayVersion")?.Data, out var version))
            throw new InvalidOperationException("Windows не указала версию установленного Copilot.");

        var installer = Path.Combine(root, version.ToString(), "Installer", "copilot_setup.exe");
        var registeredCommand = _registry.GetValue(key, "UninstallString")?.Data;
        if (!_fileExists(installer) || registeredCommand?.StartsWith($"\"{installer}\" ", StringComparison.OrdinalIgnoreCase) != true)
            throw new InvalidOperationException("Штатный деинсталлятор Copilot не найден. Проверьте приложение в разделе «Установленные приложения» Windows.");
        if (!await MicrosoftInstallerSignature.IsValidAsync(_runner, installer, ct))
            throw new InvalidOperationException("Подпись Microsoft у деинсталлятора Copilot не подтверждена.");

        // Путь проверяется по штатной регистрации, аргументы из реестра не исполняются.
        var result = await _runner.RunAsync(installer,
            ["--uninstall", "--mscopilot", "--channel=stable", "--system-level", "--verbose-logging"],
            TimeSpan.FromMinutes(5), ct);
        if (result.ExitCode is not (0 or 3010) && IsInstalled())
            throw new InvalidOperationException($"Деинсталлятор Copilot завершился с кодом {result.ExitCode}.");
    }

    private IEnumerable<(string Key, string Root)> InstalledLocations() =>
        _locations.Where(location =>
            string.Equals(_registry.GetValue(location.Key, "InstallLocation")?.Data.TrimEnd('\\'),
                location.Root, StringComparison.OrdinalIgnoreCase) &&
            _fileExists(Path.Combine(location.Root, "copilotapp.exe")));
}
