using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Corelight.Core.Actions;
using Corelight.Core.Backup;
using Corelight.Core.Platform;
using Corelight.Core.SystemInfo;
using Corelight.Core.Tweaks;

namespace Corelight.Tests;

public sealed class ReviewRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Corelight-review-" + Guid.NewGuid().ToString("N"));
    private const string Key = @"HKCU\Software\CorelightReview";

    [Fact]
    public void Failed_save_does_not_create_an_in_memory_backup()
    {
        var path = Path.Combine(_root, "backup.json");
        var store = new JsonBackupStore(path);
        Directory.CreateDirectory(path);
        var error = Xunit.Record.Exception(() => store.Save(Record()));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Failed_remove_keeps_backup_in_memory_and_on_disk()
    {
        var path = Path.Combine(_root, "backup.json");
        var store = new JsonBackupStore(path);
        store.Save(Record());
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Xunit.Record.Exception(() => store.Remove("test"));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.NotNull(store.Get("test"));
            Assert.NotNull(new JsonBackupStore(path).Get("test"));
        }
        store.Remove("test");
        Assert.Empty(new JsonBackupStore(path).GetAll());
    }

    [Fact]
    public void Invalid_backup_shapes_and_duplicate_ids_are_preserved_as_corrupt_copies()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "backup.json");
        foreach (var json in new[] { "null", "[null]", JsonSerializer.Serialize(new[] { Record(), Record() }) })
        {
            File.WriteAllText(path, json);
            var store = new JsonBackupStore(path);
            Assert.NotNull(store.CorruptCopyPath);
            Assert.Equal(json, File.ReadAllText(store.CorruptCopyPath));
            Assert.Empty(store.GetAll());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_tweak_targets_cannot_overwrite_or_reinterpret_old_backup(bool changeCount)
    {
        var sys = new TestSystem();
        var engine = sys.CreateEngine();
        var original = TestSystem.Tweak(new RegistryValueAction { Path = Key, Name = "Old", Value = "1" });
        await engine.ApplyAsync(original);
        var backup = sys.Backups.Get(original.Id);
        var changed = TestSystem.Tweak(changeCount
            ? [new RegistryValueAction { Path = Key, Name = "Old", Value = "1" }, new RegistryValueAction { Path = Key, Name = "New", Value = "1" }]
            : [new RegistryValueAction { Path = Key, Name = "New", Value = "1" }]);

        await Assert.ThrowsAsync<TweakException>(() => engine.ApplyAsync(changed));
        await Assert.ThrowsAsync<TweakException>(() => engine.RevertAsync(changed));
        Assert.Same(backup, sys.Backups.Get(original.Id));
        Assert.Equal("1", sys.Registry.GetValue(Key, "Old")!.Data);
        Assert.Null(sys.Registry.GetValue(Key, "New"));
    }

    [Fact]
    public async Task Target_absent_from_original_snapshot_is_not_changed_on_revert()
    {
        var sys = new TestSystem();
        var engine = sys.CreateEngine();
        var tweak = TestSystem.Tweak(new ServiceStartupAction { Service = "InstalledLater" });
        Assert.Equal(ApplyOutcome.NotApplicable, (await engine.ApplyAsync(tweak)).Outcome);
        Assert.Null(sys.Backups.Get(tweak.Id));
        sys.Services.StartTypes["InstalledLater"] = ServiceStartType.Disabled;
        Assert.Equal(ServiceStartType.Disabled, sys.Services.StartTypes["InstalledLater"]);
    }

    [Fact]
    public async Task Revert_preserves_a_skipped_value_modified_later()
    {
        var sys = new TestSystem();
        var tweak = TestSystem.Tweak(
            new RegistryValueAction { Path = Key, Name = "Skipped", Value = "1" },
            new RegistryValueAction { Path = Key, Name = "Changed", Value = "1" });
        sys.Registry.SetValue(Key, "Skipped", RegValue.DWord(1));
        await sys.CreateEngine().ApplyAsync(tweak);
        sys.Registry.SetValue(Key, "Skipped", RegValue.DWord(7));

        await sys.CreateEngine().RevertAsync(tweak);

        Assert.Equal(RegValue.DWord(7), sys.Registry.GetValue(Key, "Skipped"));
        Assert.Null(sys.Registry.GetValue(Key, "Changed"));
    }

    [Fact]
    public async Task Reapplying_an_effective_policy_does_not_add_a_second_policy_value()
    {
        var sys = new TestSystem();
        var tweak = new TweakDefinition
        {
            Id = "test.alternatives",
            Title = "Test",
            Description = "Test",
            AppliedWhenAnyActionMatches = true,
            Actions =
            [
                new RegistryValueAction { Path = Key, Name = "First", Value = "1" },
                new RegistryValueAction { Path = Key, Name = "Second", Value = "1" }
            ],
        };
        sys.Registry.SetValue(Key, "First", RegValue.DWord(1));

        var result = await sys.CreateEngine().ApplyAsync(tweak);

        Assert.Equal(ApplyOutcome.AlreadyApplied, result.Outcome);
        Assert.Null(sys.Registry.GetValue(Key, "Second"));
        Assert.Empty(sys.Backups.GetAll());
    }

    [Fact]
    public async Task Retried_rollback_of_old_history_preserves_user_edits_after_partial_restore()
    {
        var sys = new TestSystem();
        sys.Tasks.Tasks["\\Blocked"] = false;
        sys.Tasks.Tasks["\\Restored"] = false;
        sys.Tasks.Protected.Add("\\Blocked");
        var tweak = TestSystem.Tweak(new ScheduledTaskAction { Path = "\\Blocked" }, new ScheduledTaskAction { Path = "\\Restored" });
        sys.Backups.Save(new BackupRecord
        {
            TweakId = tweak.Id,
            Title = tweak.Title,
            AppliedAt = DateTimeOffset.Now,
            Actions = [new(true, "True"), new(true, "True")],
        });

        await Assert.ThrowsAsync<TweakException>(() => sys.CreateEngine().RevertAsync(tweak));
        Assert.True(sys.Tasks.Tasks["\\Restored"]);
        sys.Tasks.Tasks["\\Restored"] = false;
        sys.Tasks.Protected.Clear();
        await sys.CreateEngine().RevertAsync(tweak);

        Assert.True(sys.Tasks.Tasks["\\Blocked"]);
        Assert.False(sys.Tasks.Tasks["\\Restored"]);
        Assert.Empty(sys.Backups.GetAll());
    }

    [Fact]
    public async Task Reapplying_captures_a_service_that_became_available()
    {
        var sys = new TestSystem();
        var tweak = TestSystem.Tweak(new RegistryValueAction { Path = Key, Name = "Changed", Value = "1" },
            new ServiceStartupAction { Service = "InstalledLater" });
        await sys.CreateEngine().ApplyAsync(tweak);
        sys.Services.StartTypes["InstalledLater"] = ServiceStartType.Manual;
        await sys.CreateEngine().ApplyAsync(tweak);
        Assert.Equal(ServiceStartType.Disabled, sys.Services.StartTypes["InstalledLater"]);

        await sys.CreateEngine().RevertAsync(tweak);

        Assert.Equal(ServiceStartType.Manual, sys.Services.StartTypes["InstalledLater"]);
        Assert.Null(sys.Registry.GetValue(Key, "Changed"));
    }

    [Fact]
    public async Task Denied_optional_actions_do_not_report_success_or_leave_history()
    {
        var sys = new TestSystem();
        const string taskPath = @"\Test";
        sys.Tasks.Tasks[taskPath] = true;
        sys.Tasks.Protected.Add(taskPath);
        var tweak = TestSystem.Tweak(new ScheduledTaskAction { Path = taskPath, Optional = true });

        Assert.Equal(ApplyOutcome.Unconfirmed, (await sys.CreateEngine().ApplyAsync(tweak)).Outcome);
        Assert.Null(sys.Backups.Get(tweak.Id));
        Assert.True(sys.Tasks.Tasks[taskPath]);
    }

    [Fact]
    public async Task Initial_detection_failure_prevents_unbacked_mutation()
    {
        var sys = new TestSystem();
        var action = new StateAction { FailFirstDetection = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => sys.CreateEngine().ApplyAsync(TestSystem.Tweak(action)));
        Assert.Equal(5, action.Value);
        Assert.Empty(sys.Backups.GetAll());
    }

    [Fact]
    public async Task Revert_that_does_not_restore_state_keeps_the_backup()
    {
        var sys = new TestSystem();
        var action = new StateAction { IgnoreRevert = true };
        var tweak = TestSystem.Tweak(action);
        var engine = sys.CreateEngine();
        await engine.ApplyAsync(tweak);
        await Assert.ThrowsAsync<TweakException>(() => engine.RevertAsync(tweak));
        Assert.NotNull(sys.Backups.Get(tweak.Id));
    }

    [Fact]
    public async Task Unconfirmed_automatic_rollback_keeps_original_state_for_retry()
    {
        var sys = new TestSystem();
        var action = new StateAction { ThrowAfterApply = true, IgnoreRevert = true };
        var tweak = TestSystem.Tweak(action);

        await Assert.ThrowsAsync<TweakException>(() => sys.CreateEngine().ApplyAsync(tweak));

        Assert.Equal(1, action.Value);
        Assert.Equal("5", sys.Backups.Get(tweak.Id)!.Actions[0].State);
    }

    [Fact]
    public async Task Detection_failure_after_capture_is_reported_instead_of_success()
    {
        var sys = new TestSystem();
        var action = new StateAction { FailSecondDetection = true };

        await Assert.ThrowsAsync<TweakException>(() => sys.CreateEngine().ApplyAsync(TestSystem.Tweak(action)));

        Assert.Equal(5, action.Value);
        Assert.Empty(sys.Backups.GetAll());
    }

    [Fact]
    public async Task Unconfirmed_apply_is_reported_without_dropping_its_backup()
    {
        var sys = new TestSystem();
        var tweak = TestSystem.Tweak(new StateAction { IgnoreApply = true });
        var result = await sys.CreateEngine().ApplyAsync(tweak);
        Assert.False(result.Confirmed);
        Assert.NotEmpty(result.Warnings);
        Assert.NotNull(sys.Backups.Get(tweak.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task List_entry_revert_preserves_later_changes_to_other_graphics_settings(bool legacyBackup)
    {
        var sys = new TestSystem();
        var action = new RegistryListEntryAction { Path = Key, Name = "Graphics", Key = "Target", Value = "1" };
        var tweak = TestSystem.Tweak(action);
        var original = RegValue.String("Target=0;Other=1;");
        sys.Registry.SetValue(Key, "Graphics", original);
        var engine = sys.CreateEngine();
        if (legacyBackup)
            sys.Backups.Save(new BackupRecord { TweakId = tweak.Id, Title = tweak.Title, AppliedAt = DateTimeOffset.Now, Actions = [new(true, JsonSerializer.Serialize(original))] });
        else await engine.ApplyAsync(tweak);
        sys.Registry.SetValue(Key, "Graphics", RegValue.String("Target=1;Other=2;New=3;"));
        await engine.RevertAsync(tweak);
        Assert.Equal("Target=0;Other=2;New=3;", sys.Registry.GetValue(Key, "Graphics")!.Data);
        Assert.Null(sys.Backups.Get(tweak.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("opaque;Other=1;Other=2")]
    [InlineData("opaque;Target=8; Target=0 ;Other=1;")]
    public async Task Registry_list_roundtrips_empty_duplicates_and_opaque_segments(string value)
    {
        var sys = new TestSystem();
        var tweak = TestSystem.Tweak(new RegistryListEntryAction { Path = Key, Name = "Graphics", Key = "Target", Value = "1" });
        var before = RegValue.String(value);
        sys.Registry.SetValue(Key, "Graphics", before);
        var engine = sys.CreateEngine();

        await engine.ApplyAsync(tweak);
        await engine.RevertAsync(tweak);

        Assert.Equal(before, sys.Registry.GetValue(Key, "Graphics"));
    }

    [Fact]
    public async Task Registry_list_rollback_accepts_independently_added_separators()
    {
        var sys = new TestSystem();
        var tweak = TestSystem.Tweak(new RegistryListEntryAction { Path = Key, Name = "Graphics", Key = "Target", Value = "1" });
        var engine = sys.CreateEngine();

        await engine.ApplyAsync(tweak);
        sys.Registry.SetValue(Key, "Graphics", RegValue.String("Target=1;;"));
        await engine.RevertAsync(tweak);

        Assert.Equal(RegValue.String(";"), sys.Registry.GetValue(Key, "Graphics"));
        Assert.Empty(sys.Backups.GetAll());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_an_unmodified_scheme_created_by_corelight_is_deleted_on_revert(bool modify)
    {
        var sys = new TestSystem();
        var plan = new PowerPlanAction { Scheme = Guid.NewGuid(), CopyGuid = Guid.NewGuid() };
        var tweak = TestSystem.Tweak(plan);
        var engine = sys.CreateEngine();

        await engine.ApplyAsync(tweak);
        if (modify) sys.Power.CopyFingerprint = "modified by the user";
        await engine.RevertAsync(tweak);

        Assert.Equal(PowerPlanAction.Balanced, sys.Power.Active);
        Assert.Equal(modify, sys.Power.Schemes.Contains(plan.CopyGuid));
    }

    [Fact]
    public void Unknown_hardware_does_not_recommend_disabling_peripherals_or_desktop_power_settings()
    {
        var unknown = new HardwareProfile();
        Assert.All(new[] { "desktop", "laptop", "noPrinter", "noBluetooth" }, condition => Assert.False(unknown.Satisfies(condition)));
        var known = new HardwareProfile { HasBattery = false, HasPrinter = false, HasBluetooth = false };
        Assert.All(new[] { "desktop", "noPrinter", "noBluetooth" }, condition => Assert.True(known.Satisfies(condition)));
    }

    private static BackupRecord Record() => new() { TweakId = "test", Title = "Test", AppliedAt = DateTimeOffset.Now, Actions = [] };

    public void Dispose()
    {
        var path = Path.GetFullPath(_root);
        if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith("Corelight-review-"))
            throw new InvalidOperationException("Invalid test directory");
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed class StateAction : TweakAction
    {
        public int Value { get; private set; } = 5;
        public bool FailFirstDetection { get; init; }
        public bool FailSecondDetection { get; init; }
        public bool IgnoreApply { get; init; }
        public bool IgnoreRevert { get; init; }
        public bool ThrowAfterApply { get; init; }
        private int _reads;
        public override Task<ActionState> DetectAsync(PlatformServices p, CancellationToken ct)
        {
            if (++_reads == 1 && FailFirstDetection) throw new InvalidOperationException("Initial read failed");
            if (_reads == 2 && FailSecondDetection) throw new InvalidOperationException("Second read failed");
            return Task.FromResult(Value == 1 ? ActionState.Applied : ActionState.NotApplied);
        }
        public override Task<string?> CaptureAsync(PlatformServices p, CancellationToken ct) => Task.FromResult<string?>(Value.ToString());
        public override Task ApplyAsync(PlatformServices p, CancellationToken ct)
        {
            if (!IgnoreApply) Value = 1;
            if (ThrowAfterApply) throw new InvalidOperationException("Apply failed after mutation");
            return Task.CompletedTask;
        }
        public override Task RevertAsync(PlatformServices p, string? captured, CancellationToken ct) { if (!IgnoreRevert) Value = int.Parse(captured!); return Task.CompletedTask; }
        public override string Describe() => "review state";
    }
}
