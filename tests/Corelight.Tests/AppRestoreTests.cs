using Microsoft.Extensions.Logging.Abstractions;
using Corelight.Core.Actions;
using Corelight.Core.Platform;

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

    private sealed class FailedInstaller(int code) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult(new ProcessResult(code, "No package found matching input criteria.", ""));
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
