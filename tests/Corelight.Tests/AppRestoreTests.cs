using Microsoft.Extensions.Logging.Abstractions;
using Corelight.Core.Actions;
using Corelight.Core.Platform;
using Corelight.Core.Tweaks;

namespace Corelight.Tests;

public class AppRestoreTests
{
    [Theory]
    [InlineData(unchecked((int)0x8A150014), "Приложение не найдено")]
    [InlineData(unchecked((int)0x8A150010), "нет подходящего установщика")]
    public async Task Store_lookup_failures_offer_manual_recovery(int code, string explanation)
    {
        var apps = new WindowsAppxControl(new FailedInstaller(code), NullLogger<WindowsAppxControl>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => apps.InstallFromStoreAsync("test-id"));

        Assert.Contains(explanation, exception.Message);
        Assert.Contains("Открыть в Store", exception.Message);
        Assert.Contains("откат", exception.Message);
    }

    [Fact]
    public async Task Manually_restored_app_is_recognized_without_running_the_installer_again()
    {
        var apps = new ExternallyInstalledApp();
        var platform = new PlatformServices(new FakeRegistry(), new FakeServices(), new FakeTasks(), apps, new FakePower(), new FakeOneDrive());
        var action = new AppxPackageAction { Package = "Test.Package", StoreId = "test-id" };

        await action.RevertAsync(platform, "installed", CancellationToken.None);

        Assert.True(apps.IsInstalled("Test.Package"));
        Assert.False(apps.InstallerCalled);
    }

    [Fact]
    public async Task Manual_app_without_store_id_can_be_confirmed_from_its_original_state()
    {
        var sys = new TestSystem();
        var tweak = TestSystem.Tweak(new AppxPackageAction { Package = "Current.Teams" }, new AppxPackageAction { Package = "Legacy.Teams" });
        sys.Apps.Installed.Add("Current.Teams");
        var engine = sys.CreateEngine();
        await engine.ApplyAsync(tweak);
        sys.Apps.Installed.Add("Current.Teams");

        Assert.True(await engine.TryConfirmManualAppRestoreAsync(tweak));
        Assert.Null(sys.Backups.Get(tweak.Id));
        Assert.Empty(sys.Apps.StoreInstalls);
    }

    [Fact]
    public async Task Missing_manual_app_keeps_its_history_record()
    {
        var sys = new TestSystem();
        var tweak = TestSystem.Tweak(new AppxPackageAction { Package = "Test.Package" });
        sys.Apps.Installed.Add("Test.Package");
        var engine = sys.CreateEngine();
        await engine.ApplyAsync(tweak);

        Assert.False(await engine.TryConfirmManualAppRestoreAsync(tweak));
        Assert.NotNull(sys.Backups.Get(tweak.Id));
        Assert.Empty(sys.Apps.StoreInstalls);
    }

    [Fact]
    public async Task Manual_confirmation_requires_all_original_package_states_to_match()
    {
        var sys = new TestSystem();
        var tweak = TestSystem.Tweak(new AppxPackageAction { Package = "Current.Teams" }, new AppxPackageAction { Package = "Legacy.Teams" });
        sys.Apps.Installed.Add("Current.Teams");
        var engine = sys.CreateEngine();
        await engine.ApplyAsync(tweak);
        sys.Apps.Installed.UnionWith(["Current.Teams", "Legacy.Teams"]);

        Assert.False(await engine.TryConfirmManualAppRestoreAsync(tweak));
        Assert.NotNull(sys.Backups.Get(tweak.Id));
        Assert.Empty(sys.Apps.StoreInstalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timed_out_package_removal_keeps_backup_and_does_not_race_with_rollback(bool optional)
    {
        var apps = new TimedOutApp();
        var backups = new MemoryBackupStore();
        var platform = new PlatformServices(new FakeRegistry(), new FakeServices(), new FakeTasks(), apps, new FakePower(), new FakeOneDrive());
        var engine = new TweakEngine(platform, backups, NullLogger<TweakEngine>.Instance);
        var tweak = TestSystem.Tweak(
            new AppxPackageAction { Package = "First.Teams" },
            new AppxPackageAction { Package = "Second.Teams", Optional = optional });

        var error = await Assert.ThrowsAsync<TweakException>(() => engine.ApplyAsync(tweak));

        Assert.IsType<AppxOperationTimeoutException>(error.InnerException);
        Assert.NotNull(backups.Get(tweak.Id));
        Assert.False(apps.IsInstalled("First.Teams"));
        Assert.True(apps.IsInstalled("Second.Teams"));
        Assert.Equal(2, apps.RemovalAttempts);
    }

    [Fact]
    public async Task Package_removed_after_the_first_check_is_confirmed()
    {
        var sys = new TestSystem();
        var tweak = TestSystem.Tweak(new AppxPackageAction { Package = "MSTeams" });
        sys.Apps.Installed.Add("MSTeams");
        sys.Apps.DelayRemovalUntilRefresh = true;

        var result = await sys.CreateEngine().ApplyAsync(tweak);

        Assert.True(result.Confirmed);
        Assert.Empty(result.Warnings);
        Assert.False(sys.Apps.IsInstalled("MSTeams"));
        Assert.NotNull(sys.Backups.Get(tweak.Id));
    }

    [Fact]
    public async Task Rollback_keeps_history_until_the_original_provisioning_is_restored()
    {
        const string package = "Test.Package";
        const string family = "Test.Package_original";
        var sys = new TestSystem();
        sys.Apps.Installed.Add(package);
        sys.Apps.Provisioned[package] = family;
        sys.Apps.StorePackages["test-id"] = package;
        var tweak = TestSystem.Tweak(new AppxPackageAction { Package = package, StoreId = "test-id" });
        var engine = sys.CreateEngine();

        await engine.ApplyAsync(tweak);
        sys.Apps.DenyProvisioning = true;
        await Assert.ThrowsAsync<TweakException>(() => engine.RevertAsync(tweak));
        Assert.NotNull(sys.Backups.Get(tweak.Id));
        Assert.Contains(package, sys.Apps.Installed);
        Assert.Null(sys.Apps.GetProvisionedFamily(package));

        sys.Apps.DenyProvisioning = false;
        await engine.RevertAsync(tweak);
        Assert.Equal(family, sys.Apps.GetProvisionedFamily(package));
        Assert.Equal(["test-id"], sys.Apps.StoreInstalls);
        Assert.Null(sys.Backups.Get(tweak.Id));
    }

    private sealed class FailedInstaller(int code) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult(new ProcessResult(code, "No package found matching input criteria.", ""));
    }

    private sealed class TimedOutApp : IAppxControl
    {
        private readonly HashSet<string> _installed = ["First.Teams", "Second.Teams"];
        public int RemovalAttempts { get; private set; }
        public void Refresh() { }
        public bool IsInstalled(string packageName) => _installed.Contains(packageName);
        public Task RemoveAsync(string packageName, CancellationToken ct = default)
        {
            RemovalAttempts++;
            if (packageName == "Second.Teams")
                throw new AppxOperationTimeoutException(packageName, new TimeoutException());
            _installed.Remove(packageName);
            return Task.CompletedTask;
        }
        public Task InstallFromStoreAsync(string storeId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ExternallyInstalledApp : IAppxControl
    {
        private bool _cachedInstalled;
        public bool InstallerCalled { get; private set; }
        public void Refresh() => _cachedInstalled = true;
        public bool IsInstalled(string packageName) => _cachedInstalled;
        public Task RemoveAsync(string packageName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task InstallFromStoreAsync(string storeId, CancellationToken ct = default)
        {
            InstallerCalled = true;
            throw new InvalidOperationException("The installer should not run after manual restoration.");
        }
    }
}
