using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Corelight.Core;
using Corelight.Core.Actions;
using Corelight.Core.Platform;
using Corelight.Core.SystemInfo;
using Corelight.Core.Tweaks;

namespace Corelight.Tests;

/// <summary>
/// Проверка настоящих адаптеров Windows. Тесты только читают систему, а запись идёт
/// во временный ключ HKCU\Software\Corelight.Tests\&lt;guid&gt;, который удаляется после теста.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WindowsIntegrationTests : IDisposable
{
    private readonly string _subKey = $@"Software\Corelight.Tests\{Guid.NewGuid():N}";
    private readonly WindowsRegistryAccess _registry = new();
    private readonly ProcessRunner _runner = new();

    private string Key => @"HKCU\" + _subKey;

    [Theory]
    [InlineData(RegistryValueKind.DWord, "4294967295")]
    [InlineData(RegistryValueKind.QWord, "18446744073709551615")]
    [InlineData(RegistryValueKind.String, "Привет")]
    [InlineData(RegistryValueKind.ExpandString, "%SystemRoot%\\x")]
    [InlineData(RegistryValueKind.MultiString, "a\0b")]
    [InlineData(RegistryValueKind.Binary, "0300000011223344")]
    public void Registry_values_roundtrip_exactly(RegistryValueKind kind, string data)
    {
        var value = new RegValue(kind, data);

        _registry.SetValue(Key, "V", value);

        Assert.Equal(value, _registry.GetValue(Key, "V"));
        _registry.DeleteValue(Key, "V");
        Assert.Null(_registry.GetValue(Key, "V"));
    }

    [Fact]
    public void Unsupported_existing_registry_value_does_not_look_absent()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_subKey);
        key.SetValue("Opaque", new byte[] { 1, 2, 3 }, RegistryValueKind.None);

        Assert.Throws<NotSupportedException>(() => _registry.GetValue(Key, "Opaque"));
    }

    [Fact]
    public async Task Engine_applies_and_reverts_on_real_registry()
    {
        _registry.SetValue(Key, "Existing", RegValue.String("Medium"));
        var platform = new PlatformServices(_registry, new FakeServices(), new FakeTasks(), new FakeApps(), new FakePower(), new FakeOneDrive());
        var engine = new TweakEngine(platform, new MemoryBackupStore(), NullLogger<TweakEngine>.Instance);
        var tweak = TestSystem.Tweak(
            new RegistryValueAction { Path = Key, Name = "New", Value = "0" },
            new RegistryValueAction { Path = Key, Name = "Existing", Kind = RegistryValueKind.String, Value = "High" },
            new RegistryListEntryAction { Path = Key, Name = "List", Key = "Swap", Value = "1" });

        await engine.ApplyAsync(tweak);
        Assert.Equal(TweakState.Applied, await engine.DetectAsync(tweak));

        await engine.RevertAsync(tweak);
        Assert.Null(_registry.GetValue(Key, "New"));
        Assert.Null(_registry.GetValue(Key, "List"));
        Assert.Equal(RegValue.String("Medium"), _registry.GetValue(Key, "Existing"));
    }

    [Fact]
    public void Service_start_type_is_read()
    {
        var services = new WindowsServiceControl(_registry, _runner, NullLogger<WindowsServiceControl>.Instance);

        Assert.Equal(ServiceStartType.Automatic, services.GetStartType("Winmgmt"));
        Assert.Null(services.GetStartType("Corelight_NoSuchService"));
        Assert.True(services.IsRunning("RpcSs"));
    }

    [Fact]
    public void Scheduled_task_state_is_read()
    {
        var tasks = new WindowsScheduledTaskControl();

        Assert.NotNull(tasks.IsEnabled(@"\Microsoft\Windows\Defrag\ScheduledDefrag"));
        Assert.Null(tasks.IsEnabled(@"\Microsoft\Windows\Corelight\NoSuchTask"));
        Assert.Null(tasks.IsEnabled(@"\NoSuchFolder\NoSuchTask"));
    }

    [Fact]
    public async Task Active_power_scheme_is_in_scheme_list()
    {
        var power = new WindowsPowerControl(_runner);

        var active = await power.GetActiveSchemeAsync();

        Assert.Contains(active, await power.ListSchemesAsync());
        Assert.Matches("^[0-9A-F]{64}$", await power.GetSchemeFingerprintAsync(active));
    }

    [Fact]
    public void Appx_inventory_agrees_with_the_current_users_package_registration()
    {
        const string package = "Microsoft.DesktopAppInstaller";
        var native = new Windows.Management.Deployment.PackageManager();
        var registered = native.FindPackagesForUser(string.Empty, WindowsAppxControl.ExpectedFamily(package)).Any();
        var apps = new WindowsAppxControl(_runner, NullLogger<WindowsAppxControl>.Instance);

        Assert.Equal(registered, apps.GetPackageState(package).CurrentUser);
    }

    [Fact]
    public void Protected_data_directory_rejects_insecure_permissions_and_redirection()
    {
        var root = Path.Combine(Path.GetTempPath(), "Corelight.Tests.Acl-" + Guid.NewGuid().ToString("N"));
        var safe = Path.Combine(root, "safe");
        var redirected = Path.Combine(root, "redirected");
        Directory.CreateDirectory(root);
        try
        {
            ProtectedDataDirectory.EnsureDirectory(safe);
            Directory.CreateSymbolicLink(redirected, safe);
            Assert.Throws<IOException>(() => ProtectedDataDirectory.EnsureDirectory(redirected, migrateLegacyPermissions: true));

            var info = new DirectoryInfo(safe);
            var acl = info.GetAccessControl();
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Write, AccessControlType.Allow));
            info.SetAccessControl(acl);

            Assert.Throws<UnauthorizedAccessException>(() => ProtectedDataDirectory.EnsureDirectory(safe, migrateLegacyPermissions: true));
        }
        finally
        {
            if (Directory.Exists(redirected)) Directory.Delete(redirected);
            if (Directory.Exists(safe)) Directory.Delete(safe);
            Directory.Delete(root);
        }
    }

    [Fact]
    public void Protected_data_directory_migrates_inherited_programdata_permissions_and_preserves_history()
    {
        var root = Path.Combine(Path.GetTempPath(), "Corelight.Tests.LegacyAcl-" + Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(root, "Corelight");
        var logs = Path.Combine(legacy, "logs");
        var history = Path.Combine(legacy, "backup.json");
        var inherited = new DirectorySecurity();
        inherited.SetSecurityDescriptorSddlForm("O:BAD:PAI(A;OICIIO;GA;;;CO)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;CI;DCLCRPCR;;;BU)");
        new DirectoryInfo(root).Create(inherited);
        try
        {
            Directory.CreateDirectory(legacy);
            Directory.CreateDirectory(logs);
            File.WriteAllText(history, "[{\"test\":true}]");
            Assert.Throws<UnauthorizedAccessException>(() => ProtectedDataDirectory.EnsureDirectory(legacy));

            var before = new DirectoryInfo(legacy).GetAccessControl();
            try { ProtectedDataDirectory.EnsureDirectory(legacy, migrateLegacyPermissions: true); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(before.GetSecurityDescriptorSddlForm(AccessControlSections.All), ex);
            }
            ProtectedDataDirectory.EnsureDirectory(logs, migrateLegacyPermissions: true);
            ProtectedDataDirectory.ValidateFileIfPresent(history);

            Assert.True(new DirectoryInfo(legacy).GetAccessControl().AreAccessRulesProtected);
            Assert.Equal("[{\"test\":true}]", File.ReadAllText(history));
            ProtectedDataDirectory.EnsureDirectory(legacy, migrateLegacyPermissions: true);
        }
        finally
        {
            if (File.Exists(history)) File.Delete(history);
            if (Directory.Exists(logs)) Directory.Delete(logs);
            if (Directory.Exists(legacy)) Directory.Delete(legacy);
            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task Console_output_is_decoded_with_oem_code_page()
    {
        // Кириллица в OEM-кодировке есть только в русской локали (866).
        if (CultureInfo.CurrentCulture.TextInfo.OEMCodePage != 866)
            return;

        var result = await _runner.RunAsync(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["/c", "echo Привет"], TimeSpan.FromSeconds(10));

        Assert.Contains("Привет", result.Output);
    }

    [Fact]
    public void Hardware_probe_returns_basic_info()
    {
        var profile = new HardwareProbe(_registry, NullLogger<HardwareProbe>.Instance).Probe();

        Assert.True(profile.OsBuild >= 22000);
        Assert.False(string.IsNullOrWhiteSpace(profile.CpuName));
        Assert.True(profile.TotalMemoryBytes > 0);
    }

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_subKey, throwOnMissingSubKey: false);
}
