using Corelight.App.ViewModels;
using Corelight.Core.Catalog;
using Corelight.Core.Platform;
using Corelight.Core.Scanning;
using Corelight.Core.Tweaks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Corelight.Tests;

public class CopilotDesktopTests
{
    private const string RegistryKey = @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Copilot";
    private const string AppRoot = @"C:\Program Files (x86)\Microsoft\Copilot\Application";
    private const string Client = AppRoot + @"\copilotapp.exe";
    private const string Installer = AppRoot + @"\157.0.4341.60\Installer\copilot_setup.exe";

    [Fact]
    public async Task Desktop_copilot_is_detected_separately_and_manual_restore_requires_the_same_installation_type()
    {
        var system = new TestSystem();
        var registry = system.Registry;
        RegisterCopilot(registry, Installer);
        var runner = new InstallerRunner { OnUninstall = () => registry.DeleteValue(RegistryKey, "InstallLocation") };
        var desktop = CreateControl(registry, runner);
        var engine = new TweakEngine(system.Platform with { CopilotDesktop = desktop }, system.Backups, NullLogger<TweakEngine>.Instance);
        var catalog = TweakCatalog.LoadEmbedded();
        var tweak = catalog.Find("apps.copilot-desktop")!;
        Assert.False(new TweakStatus(tweak, TweakState.NotApplied, null, tweak.Recommended).IsPreselected);
        Assert.Equal(TweakState.Applied, await engine.DetectAsync(catalog.Find("apps.copilot")!));
        Assert.Equal(TweakState.NotApplied, await engine.DetectAsync(tweak));

        Assert.True((await engine.ApplyAsync(tweak)).Confirmed);
        Assert.Equal(TweakState.Applied, await engine.DetectAsync(tweak));
        var record = Assert.Single(system.Backups.GetAll());
        var item = new HistoryItemViewModel(record, tweak);
        Assert.True(item.NeedsManualRestore);
        Assert.True(item.HasRestoreHelp);

        system.Apps.Installed.Add("Microsoft.Copilot");
        Assert.False(await engine.TryConfirmManualAppRestoreAsync(tweak));
        Assert.NotNull(system.Backups.Get(tweak.Id));

        RegisterCopilot(registry, Installer);
        Assert.True(await engine.TryConfirmManualAppRestoreAsync(tweak));
        Assert.Empty(system.Backups.GetAll());
    }

    [Theory]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 1)]
    public async Task Rejects_other_installers_and_invalid_signatures(bool matchingPath, bool validSignature, int signatureChecks)
    {
        var registry = new FakeRegistry();
        RegisterCopilot(registry, matchingPath ? Installer : @"C:\Elsewhere\copilot_setup.exe");
        var runner = new InstallerRunner { ValidSignature = validSignature };
        var control = CreateControl(registry, runner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => control.UninstallAsync());

        Assert.Equal(signatureChecks, runner.SignatureChecks);
        Assert.Equal(0, runner.UninstallCalls);
        Assert.True(control.IsInstalled());
    }

    [Fact]
    public async Task Stale_uninstall_registration_without_the_client_does_not_count_as_installed()
    {
        var registry = new FakeRegistry();
        RegisterCopilot(registry, Installer);
        var runner = new InstallerRunner();
        var control = CreateControl(registry, runner, clientExists: false);

        Assert.False(control.IsInstalled());
        await control.UninstallAsync();
        Assert.Equal(0, runner.SignatureChecks);
        Assert.Equal(0, runner.UninstallCalls);
    }

    private static WindowsCopilotDesktopControl CreateControl(FakeRegistry registry, InstallerRunner runner, bool clientExists = true) =>
        new(registry, runner, path => path == Installer || (clientExists && path == Client),
            @"C:\Program Files", @"C:\Program Files (x86)");

    private static void RegisterCopilot(FakeRegistry registry, string uninstaller)
    {
        registry.SetValue(RegistryKey, "InstallLocation", RegValue.String(AppRoot));
        registry.SetValue(RegistryKey, "DisplayVersion", RegValue.String("157.0.4341.60"));
        registry.SetValue(RegistryKey, "UninstallString", RegValue.String($"\"{uninstaller}\" --untrusted-arg"));
    }

    private sealed class InstallerRunner : IProcessRunner
    {
        public bool ValidSignature { get; init; } = true;
        public Action? OnUninstall { get; init; }
        public int SignatureChecks { get; private set; }
        public int UninstallCalls { get; private set; }

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default)
        {
            if (Path.GetFileName(fileName).Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
            {
                SignatureChecks++;
                Assert.Contains(Installer, arguments[^1]);
                return Task.FromResult(new ProcessResult(ValidSignature ? 0 : 1, "", ""));
            }

            UninstallCalls++;
            Assert.Equal(1, SignatureChecks);
            Assert.Equal(Installer, fileName);
            Assert.Equal(new[] { "--uninstall", "--mscopilot", "--channel=stable", "--system-level", "--verbose-logging" }, arguments);
            OnUninstall?.Invoke();
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }
}
