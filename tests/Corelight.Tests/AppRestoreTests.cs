using System.Collections.Concurrent;
using System.ComponentModel;
using Microsoft.Extensions.Logging.Abstractions;
using Corelight.Core.Actions;
using Corelight.Core.Backup;
using Corelight.Core.Platform;
using Corelight.Core.Tweaks;

namespace Corelight.Tests;

public class AppRestoreTests
{
    private const string WinGet = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_fixture\winget.exe";

    [Theory]
    [InlineData(unchecked((int)0x8A150014), "Приложение не найдено")]
    [InlineData(unchecked((int)0x8A150010), "нет подходящего установщика")]
    public async Task Store_lookup_failures_offer_manual_recovery(int code, string explanation)
    {
        var apps = new WindowsAppxControl(new FailedInstaller(code), NullLogger<WindowsAppxControl>.Instance) { FindWinGet = () => WinGet };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => apps.InstallFromStoreAsync("test-id"));

        Assert.Contains(explanation, exception.Message);
        Assert.Contains("Открыть в Store", exception.Message);
        Assert.Contains("откат", exception.Message);
    }

    [Theory]
    [InlineData(0, StoreRestoreAvailability.Found)]
    [InlineData(unchecked((int)0x8A150014), StoreRestoreAvailability.NotFound)]
    [InlineData(unchecked((int)0x8A150010), StoreRestoreAvailability.NoApplicableInstaller)]
    [InlineData(unchecked((int)0x8A150046), StoreRestoreAvailability.AgreementsRequired)]
    [InlineData(unchecked((int)0x8A150045), StoreRestoreAvailability.Unverified)]
    public async Task Store_check_uses_metadata_and_exit_codes_without_installing(int code, StoreRestoreAvailability expected)
    {
        var runner = new StoreRunner(() => new ProcessResult(code, "", ""));
        var apps = new WindowsAppxControl(runner, NullLogger<WindowsAppxControl>.Instance) { FindWinGet = () => WinGet };

        Assert.Equal(expected, await apps.CheckStoreRestoreAsync("test-id", CancellationToken.None));
        Assert.Equal(WinGet, runner.FileName);
        Assert.Equal(["show", "--id", "test-id", "--source", "msstore", "--exact", "--disable-interactivity"], runner.Arguments);
    }

    [Theory]
    [InlineData(false, StoreRestoreAvailability.WinGetUnavailable)]
    [InlineData(true, StoreRestoreAvailability.TimedOut)]
    public async Task Store_check_reports_missing_winget_or_timeout(bool timeout, StoreRestoreAvailability expected)
    {
        Exception failure = timeout ? new TimeoutException() : new Win32Exception(2);
        var runner = new StoreRunner(() => throw failure);
        var apps = new WindowsAppxControl(runner, NullLogger<WindowsAppxControl>.Instance) { FindWinGet = () => WinGet };

        Assert.Equal(expected, await apps.CheckStoreRestoreAsync("test-id", CancellationToken.None));
    }

    [Fact]
    public async Task Store_check_does_not_fall_back_to_path_when_app_installer_is_missing()
    {
        var runner = new StoreRunner(() => new ProcessResult(0, "", ""));
        var apps = new WindowsAppxControl(runner, NullLogger<WindowsAppxControl>.Instance) { FindWinGet = () => null };

        Assert.Equal(StoreRestoreAvailability.WinGetUnavailable, await apps.CheckStoreRestoreAsync("test-id", CancellationToken.None));
        Assert.Null(runner.FileName);
    }

    [Fact]
    public async Task Pre_removal_check_skips_absent_packages_and_warns_for_each_affected_choice()
    {
        var sys = new TestSystem();
        var queries = new ConcurrentBag<string>();
        sys.Apps.OnRestoreCheck = (id, _) =>
        {
            queries.Add(id);
            return Task.FromResult(id == "missing" ? StoreRestoreAvailability.NotFound : StoreRestoreAvailability.Found);
        };
        sys.Apps.Installed.UnionWith(["First", "Second", "Manual", "Provisioned"]);
        sys.Apps.Provisioned["Provisioned"] = "Provisioned_family";
        var first = TestSystem.Tweak(new AppxPackageAction { Package = "First", StoreId = "missing" },
            new AppxPackageAction { Package = "AlsoAbsent", Optional = true });
        var second = TestSystem.Tweak(new AppxPackageAction { Package = "Second", StoreId = "missing" });
        var manual = TestSystem.Tweak(new AppxPackageAction { Package = "Manual" });
        var found = TestSystem.Tweak(new AppxPackageAction { Package = "Provisioned", StoreId = "found" });
        var absent = TestSystem.Tweak(new AppxPackageAction { Package = "Absent", StoreId = "unused" });
        var setting = TestSystem.Tweak(new ServiceStartupAction { Service = "Example" });

        var issues = await sys.CreateEngine().CheckAppRestoresAsync([first, second, manual, found, absent, setting]);

        Assert.Equal(["found", "missing"], queries.Order());
        Assert.Collection(issues,
            issue => { Assert.Same(first, issue.Tweak); Assert.Equal("WinGet не нашёл приложение в Microsoft Store.", issue.Reason); },
            issue => { Assert.Same(second, issue.Tweak); Assert.Equal("WinGet не нашёл приложение в Microsoft Store.", issue.Reason); },
            issue => { Assert.Same(manual, issue.Tweak); Assert.Equal("Автоматического возврата нет.", issue.Reason); });
        Assert.Equal(["First", "Manual", "Provisioned", "Second"], sys.Apps.Installed.Order());
        Assert.Empty(sys.Apps.StoreInstalls);
        Assert.Empty(sys.Backups.GetAll());
    }

    [Fact]
    public async Task Pre_removal_check_honors_cancellation_during_the_catalog_lookup()
    {
        var sys = new TestSystem();
        using var cancel = new CancellationTokenSource();
        sys.Apps.Installed.Add("First");
        sys.Apps.OnRestoreCheck = (_, ct) =>
        {
            cancel.Cancel();
            return Task.FromCanceled<StoreRestoreAvailability>(ct);
        };
        var tweak = TestSystem.Tweak(new AppxPackageAction { Package = "First", StoreId = "first" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sys.CreateEngine().CheckAppRestoresAsync([tweak], cancel.Token));

        Assert.Contains("First", sys.Apps.Installed);
        Assert.Empty(sys.Backups.GetAll());
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
    public async Task Manual_confirmation_ignores_a_package_that_was_not_removed()
    {
        var sys = new TestSystem();
        var tweak = TestSystem.Tweak(new AppxPackageAction { Package = "Current.Teams" }, new AppxPackageAction { Package = "Legacy.Teams" });
        sys.Apps.Installed.Add("Current.Teams");
        var engine = sys.CreateEngine();
        await engine.ApplyAsync(tweak);
        sys.Apps.Installed.UnionWith(["Current.Teams", "Legacy.Teams"]);

        Assert.True(await engine.TryConfirmManualAppRestoreAsync(tweak));
        Assert.Null(sys.Backups.Get(tweak.Id));
        Assert.Contains("Legacy.Teams", sys.Apps.Installed);
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
        var engine = new TweakEngine(platform, backups, NullLogger<TweakEngine>.Instance) { ReadBootSession = () => TestSystem.TestBoot };
        var tweak = TestSystem.Tweak(
            new AppxPackageAction { Package = "First.Teams" },
            new AppxPackageAction { Package = "Second.Teams", Optional = optional });

        var error = await Assert.ThrowsAsync<TweakException>(() => engine.ApplyAsync(tweak));

        Assert.IsType<AppxOperationTimeoutException>(error.InnerException);
        Assert.NotNull(backups.Get(tweak.Id));
        Assert.Null(backups.Get(tweak.Id)!.Actions[0].AppxStartedInBoot);
        Assert.Equal(TestSystem.TestBoot, backups.Get(tweak.Id)!.Actions[1].AppxStartedInBoot);
        Assert.False(apps.IsInstalled("First.Teams"));
        Assert.True(apps.IsInstalled("Second.Teams"));
        Assert.Equal(2, apps.RemovalAttempts);
    }

    [Fact]
    public async Task Restarting_corelight_during_removal_preserves_history_until_windows_restarts()
    {
        var folder = Directory.CreateTempSubdirectory("Corelight.Appx.Tests-");
        var path = Path.Combine(folder.FullName, "backup.json");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sys = new TestSystem();
        const string package = "Test.App";
        sys.Apps.Installed.Add(package);
        sys.Apps.BeforeRemove = () => { started.SetResult(); return finish.Task; };
        var tweak = TestSystem.Tweak(new AppxPackageAction { Package = package, StoreId = "test-id" });
        var applying = sys.CreateEngine(new JsonBackupStore(path)).ApplyAsync(tweak);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var disk = new JsonBackupStore(path);
            var reopened = sys.CreateEngine(disk);
            Assert.Equal(TestSystem.TestBoot, disk.Get(tweak.Id)!.Actions[0].AppxStartedInBoot);
            Assert.Equal(TweakState.Unknown, await reopened.DetectAsync(tweak));
            Assert.Contains("Перезагрузите Windows", (await Assert.ThrowsAsync<TweakException>(
                () => reopened.TryConfirmManualAppRestoreAsync(tweak))).Message);
            await Assert.ThrowsAsync<TweakException>(() => reopened.RevertAsync(tweak));
            await Assert.ThrowsAsync<TweakException>(() => reopened.ApplyAsync(tweak));
            Assert.NotNull(new JsonBackupStore(path).Get(tweak.Id));

            finish.SetException(new AppxOperationTimeoutException(package, new TimeoutException()));
            await Assert.ThrowsAsync<TweakException>(() => applying);
            sys.Apps.Installed.Remove(package); // Windows completed the removal after the wait ended.
            await Assert.ThrowsAsync<TweakException>(() => reopened.RevertAsync(tweak));
            Assert.Empty(sys.Apps.StoreInstalls);

            sys.Apps.StorePackages["test-id"] = package;
            var afterReboot = sys.CreateEngine(new JsonBackupStore(path), TestSystem.TestBoot.AddHours(1));
            await afterReboot.RevertAsync(tweak);
            Assert.Contains(package, sys.Apps.Installed);
            Assert.Null(new JsonBackupStore(path).Get(tweak.Id));
        }
        finally
        {
            finish.TrySetCanceled();
            try { await applying; } catch (TweakException) { }
            File.Delete(path);
            folder.Delete();
        }
    }

    [Fact]
    public async Task Appx_timeout_during_rollback_requires_a_new_windows_boot_before_retrying()
    {
        const string package = "Test.App";
        var sys = new TestSystem();
        sys.Apps.Installed.Add(package);
        sys.Apps.BeforeInstall = () => throw new TimeoutException("Store still busy");
        var tweak = TestSystem.Tweak(new AppxPackageAction { Package = package, StoreId = "test-id" });
        var engine = sys.CreateEngine();
        await engine.ApplyAsync(tweak);

        await Assert.ThrowsAsync<TweakException>(() => engine.RevertAsync(tweak));
        Assert.Equal(TestSystem.TestBoot, sys.Backups.Get(tweak.Id)!.Actions[0].AppxStartedInBoot);
        sys.Apps.Installed.Add(package); // Registry changed, but the Store operation might still be running.
        await Assert.ThrowsAsync<TweakException>(() => sys.CreateEngine().TryConfirmManualAppRestoreAsync(tweak));

        Assert.True(await sys.CreateEngine(boot: TestSystem.TestBoot.AddHours(1)).TryConfirmManualAppRestoreAsync(tweak));
        Assert.Null(sys.Backups.Get(tweak.Id));
    }

    [Fact]
    public async Task Old_pending_appx_backup_requires_one_verified_windows_restart()
    {
        var sys = new TestSystem();
        sys.Apps.Installed.Add("Test.App");
        var tweak = TestSystem.Tweak(new AppxPackageAction { Package = "Test.App" });
        sys.Backups.Save(new BackupRecord
        {
            TweakId = tweak.Id,
            Title = tweak.Title,
            AppliedAt = TestSystem.TestBoot,
            Actions = [new(true, "installed", ActionBackupPhase.Pending)]
        });

        await Assert.ThrowsAsync<TweakException>(() => sys.CreateEngine().TryConfirmManualAppRestoreAsync(tweak));
        Assert.Equal(TestSystem.TestBoot, sys.Backups.Get(tweak.Id)!.Actions[0].AppxStartedInBoot);
        Assert.True(await sys.CreateEngine(boot: TestSystem.TestBoot.AddHours(1)).TryConfirmManualAppRestoreAsync(tweak));
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
    public async Task Incomplete_inventory_cannot_confirm_an_absent_package()
    {
        var sys = new TestSystem();
        var tweak = TestSystem.Tweak(new AppxPackageAction { Package = "MSTeams" });
        sys.Apps.Installed.Add("MSTeams");
        sys.Apps.InventoryUnavailableAfterRemoval = true;
        var engine = sys.CreateEngine();

        Assert.Equal(ApplyOutcome.Unconfirmed, (await engine.ApplyAsync(tweak)).Outcome);
        Assert.Equal(TweakState.Unknown, await engine.DetectAsync(tweak));
        Assert.False(await engine.TryConfirmManualAppRestoreAsync(tweak));
        Assert.NotNull(sys.Backups.Get(tweak.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_requires_registration_for_the_current_user_even_when_present_elsewhere(bool provisioned)
    {
        const string package = "Test.Package";
        var sys = new TestSystem();
        var tweak = TestSystem.Tweak(new AppxPackageAction { Package = package, StoreId = "test-id" });
        sys.Apps.Installed.Add(package);
        if (provisioned) sys.Apps.Provisioned[package] = "Test.Package_family";
        sys.Apps.StorePackages["test-id"] = package;
        var engine = sys.CreateEngine();

        await engine.ApplyAsync(tweak);
        sys.Apps.OtherUsers.Add(package);
        if (provisioned) sys.Apps.Provisioned[package] = "Test.Package_family";
        await engine.RevertAsync(tweak);

        Assert.Contains(package, sys.Apps.Installed);
        Assert.Equal(["test-id"], sys.Apps.StoreInstalls);
        Assert.Null(sys.Backups.Get(tweak.Id));
    }

    [Theory]
    [InlineData("Microsoft.Copilot", "Microsoft.Copilot_8wekyb3d8bbwe", true)]
    [InlineData("Microsoft.Copilot", "Microsoft.Copilot_other", false)]
    [InlineData("Clipchamp.Clipchamp", "Clipchamp.Clipchamp_yxz26nhyzhsrt", true)]
    public void Target_package_family_includes_a_known_publisher(string package, string family, bool expected)
        => Assert.Equal(expected, WindowsAppxControl.MatchesFamily(package, family));

    [Fact]
    public async Task Cancellation_while_windows_is_busy_requests_cancel_and_preserves_uncertain_outcome()
    {
        using var cancel = new CancellationTokenSource();
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = false;
        var waiting = WindowsAppxControl.WaitForPendingDeploymentAsync(pending.Task, () => requested = true, "MSTeams",
            "удаляет", TimeSpan.FromMinutes(1), cancel.Token, NullLogger.Instance);
        cancel.Cancel();

        await Assert.ThrowsAsync<AppxOperationTimeoutException>(() => waiting);
        Assert.True(requested);
        Assert.False(pending.Task.IsCompleted);
        pending.SetResult(0);
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

    private sealed class StoreRunner(Func<ProcessResult> result) : IProcessRunner
    {
        public string? FileName { get; private set; }
        public IReadOnlyList<string>? Arguments { get; private set; }

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default)
        {
            FileName = fileName;
            Arguments = arguments;
            return Task.FromResult(result());
        }
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
        public Task<StoreRestoreAvailability> CheckStoreRestoreAsync(string storeId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ExternallyInstalledApp : IAppxControl
    {
        private bool _cachedInstalled;
        public bool InstallerCalled { get; private set; }
        public void Refresh() => _cachedInstalled = true;
        public bool IsInstalled(string packageName) => _cachedInstalled;
        public Task RemoveAsync(string packageName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StoreRestoreAvailability> CheckStoreRestoreAsync(string storeId, CancellationToken ct) => throw new NotSupportedException();
        public Task InstallFromStoreAsync(string storeId, CancellationToken ct = default)
        {
            InstallerCalled = true;
            throw new InvalidOperationException("The installer should not run after manual restoration.");
        }
    }
}
