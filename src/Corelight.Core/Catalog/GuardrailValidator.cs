using Corelight.Core.Actions;
using Corelight.Core.Platform;
using Corelight.Core.SystemInfo;
using Corelight.Core.Tweaks;

namespace Corelight.Core.Catalog;

/// <summary>
/// Жёсткие ограничения проекта. Твик, нарушающий любое из них, не загружается.
/// 1. Никакого разгона: частоты, напряжения, лимиты мощности и драйверы не трогаем.
/// 2. Безопасно для античитов: Secure Boot, VBS/HVCI, подпись драйверов, Defender и сервисы античитов не трогаем.
/// 3. Визуальную часть не урезаем.
/// 4. Windows Update не отключаем.
/// </summary>
public static class GuardrailValidator
{
    private static readonly string[] ForbiddenRegistryFragments =
    [
        // Безопасность и античиты: VBS/HVCI, Secure Boot, подпись кода, Defender, защита от уязвимостей CPU.
        @"\Control\DeviceGuard",
        "HypervisorEnforcedCodeIntegrity",
        @"\Control\SecureBoot",
        @"\Control\CI",
        @"\Control\CodeIntegrity",
        "Windows Defender",
        "Microsoft Antimalware",
        "FeatureSettingsOverride",
        // Обновления Windows.
        @"\Policies\Microsoft\Windows\WindowsUpdate",
        // Разгон и драйверы: параметры питания процессора, классы устройств видеоадаптеров и процессоров.
        @"\Control\Power\PowerSettings",
        @"\Control\Processor",
        "{4d36e968-e325-11ce-bfc1-08002be10318}",
        "{50127dc3-0f36-415e-a6cc-4cb3be910b65}",
        // Визуальная часть системы.
        @"\Control Panel\Desktop",
        @"\Explorer\VisualEffects",
        @"\Themes\Personalize",
        @"\Microsoft\Windows\DWM",
    ];

    /// <summary>Службы и драйверы, которые нельзя трогать ни действием service, ни через их ключ в реестре.</summary>
    private static readonly HashSet<string> ForbiddenServices = new(StringComparer.OrdinalIgnoreCase)
    {
        // Безопасность.
        "WinDefend", "WdNisSvc", "Sense", "SecurityHealthService", "wscsvc", "mpssvc", "BFE", "CryptSvc", "TrustedInstaller",
        "WdFilter", "WdBoot", "WdNisDrv",
        // Видеодрайверы.
        "nvlddmkm", "amdkmdag", "igfx", "igfxn",
        // Обновления.
        "wuauserv", "UsoSvc", "WaaSMedicSvc", "BITS", "DoSvc",
        // Магазин, лицензии и игровая инфраструктура, без которой не запускаются игры.
        "AppXSvc", "ClipSVC", "LicenseManager", "InstallService", "GamingServices", "GamingServicesNet", "XboxGipSvc",
        // Античиты и игровые клиенты.
        "vgc", "vgk", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "FACEIT", "faceit", "Steam Client Service",
        "EAAntiCheatService", "PnkBstrA", "PnkBstrB",
        // Ядро системы.
        "RpcSs", "DcomLaunch", "RpcEptMapper", "LSM", "Winmgmt", "EventLog", "Schedule", "Power", "PlugPlay",
        "ProfSvc", "Audiosrv", "AudioEndpointBuilder", "Dhcp", "Dnscache", "nsi", "netprofm", "NlaSvc",
        "Themes", "UxSms", "TabletInputService", "TextInputManagementService",
    };

    private static readonly string[] ForbiddenPackagePrefixes =
    [
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller",
        "Microsoft.VCLibs", "Microsoft.UI.Xaml", "Microsoft.NET.Native", "Microsoft.WindowsAppRuntime", "Microsoft.Services.Store",
        "Microsoft.MicrosoftEdge", "Microsoft.Edge", "Microsoft.Win32WebViewHost", "Microsoft.WebView2",
        "Microsoft.XboxIdentityProvider", "Microsoft.Xbox.TCUI", "Microsoft.GamingServices",
        "Microsoft.SecHealthUI", "Microsoft.Windows.SecHealthUI",
        "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost", "MicrosoftWindows.Client.CBS",
        "MicrosoftWindows.Client.Core", "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl", "windows.immersivecontrolpanel",
        "Microsoft.Windows.CloudExperienceHost", "Microsoft.LockApp", "Microsoft.Windows.ContentDeliveryManager",
    ];

    private static readonly string[] ForbiddenTaskPrefixes =
    [
        @"\Microsoft\Windows\WindowsUpdate",
        @"\Microsoft\Windows\UpdateOrchestrator",
        @"\Microsoft\Windows\WaaSMedic",
        @"\Microsoft\Windows\Windows Defender",
        @"\Microsoft\Windows\ExploitGuard",
        @"\Microsoft\Windows\TPM",
        @"\Microsoft\Windows\BitLocker",
    ];

    /// <summary>Только штатные схемы питания Windows и их копии.</summary>
    private static readonly HashSet<Guid> AllowedPowerSchemes =
    [
        PowerPlanAction.Balanced,
        new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), // Высокая производительность
        new("e9a42b02-d5df-448d-aa00-03f14749eb61"), // Максимальная производительность
    ];

    public static IReadOnlyList<string> Validate(TweakDefinition tweak)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(tweak.Id))
            errors.Add("пустой id");
        if (string.IsNullOrWhiteSpace(tweak.Title) || string.IsNullOrWhiteSpace(tweak.Description))
            errors.Add("нет названия или описания");
        if (tweak.Actions.Count == 0)
            errors.Add("нет действий");

        if (tweak.RestoreHelpUrl is { } url && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            errors.Add("страница ручного возврата должна использовать HTTPS");

        foreach (var condition in tweak.RecommendWhen.Concat(tweak.Requires))
        {
            if (!HardwareProfile.KnownConditions.Contains(condition))
                errors.Add($"неизвестное условие «{condition}»");
        }

        foreach (var action in tweak.Actions)
            errors.AddRange(ValidateAction(action));

        return errors;
    }

    private static IEnumerable<string> ValidateAction(TweakAction action)
    {
        switch (action)
        {
            case RegistryValueAction r:
                return ValidateRegistry(r.Path, () => new RegValue(r.Kind, r.Value).Normalize(),
                    r.EffectiveWhenMissing is null ? null : () => new RegValue(r.Kind, r.EffectiveWhenMissing).Normalize());
            case RegistryListEntryAction l:
                return ValidateRegistry(l.Path);
            case ServiceStartupAction s when ForbiddenServices.Contains(s.Service):
                return [$"служба {s.Service} под защитой"];
            case AppxPackageAction a when ForbiddenPackagePrefixes.Any(p => a.Package.StartsWith(p, StringComparison.OrdinalIgnoreCase)):
                return [$"пакет {a.Package} нельзя удалять"];
            case ScheduledTaskAction t when !t.Path.StartsWith('\\'):
                return [$"путь задачи {t.Path} должен начинаться с \\"];
            case ScheduledTaskAction t when ForbiddenTaskPrefixes.Any(p => NormalizePath(t.Path).StartsWith(p, StringComparison.OrdinalIgnoreCase)):
                return [$"задача {t.Path} под защитой"];
            case PowerPlanAction p when !AllowedPowerSchemes.Contains(p.Scheme):
                return [$"схема питания {p.Scheme} не является штатной"];
            case PowerPlanAction p when AllowedPowerSchemes.Contains(p.CopyGuid):
                return ["GUID копии схемы питания совпадает со штатным"];
            default:
                return [];
        }
    }

    private static IEnumerable<string> ValidateRegistry(string path, Func<RegValue>? value = null,
        Func<RegValue>? effectiveWhenMissing = null)
    {
        path = NormalizePath(path);
        if (!path.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase) && !path.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase))
            yield return $"путь {path} должен начинаться с HKLM\\ или HKCU\\";

        foreach (var fragment in ForbiddenRegistryFragments)
        {
            if (path.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                yield return $"ключ {path} под защитой ({fragment})";
        }

        // Иначе запрет на службы можно было бы обойти, записав значение Start прямо в реестр.
        var service = ServiceNameFromPath(path);
        if (service is not null && ForbiddenServices.Contains(service))
            yield return $"ключ службы {service} под защитой";

        string? formatError = null;
        try
        {
            value?.Invoke();
            effectiveWhenMissing?.Invoke();
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or NotSupportedException)
        {
            formatError = $"некорректное значение для {path}: {ex.Message}";
        }
        if (formatError is not null)
            yield return formatError;
    }

    private static string NormalizePath(string path)
    {
        var normalized = string.Join('\\', path.Split('\\', StringSplitOptions.RemoveEmptyEntries));
        return path.StartsWith('\\') ? @"\" + normalized : normalized;
    }

    /// <summary>Имя службы из пути вида HKLM\SYSTEM\&lt;ControlSet&gt;\Services\&lt;имя&gt;\...</summary>
    private static string? ServiceNameFromPath(string path)
    {
        const string marker = @"\Services\";
        if (!path.StartsWith(@"HKLM\SYSTEM\", StringComparison.OrdinalIgnoreCase))
            return null;

        var start = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        var rest = path[(start + marker.Length)..];
        var end = rest.IndexOf('\\');
        return end < 0 ? rest : rest[..end];
    }
}
