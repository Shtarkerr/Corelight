using Microsoft.Win32;
using Corelight.Core.Actions;
using Corelight.Core.Platform;
using Corelight.Core.Tweaks;

namespace Corelight.Tests;

public class TweakEngineTests
{
    private const string Key = @"HKLM\SOFTWARE\Policies\Test";

    [Fact]
    public async Task Registry_value_that_did_not_exist_is_deleted_on_revert()
    {
        var sys = new TestSystem();
        var engine = sys.CreateEngine();
        var tweak = TestSystem.Tweak(new RegistryValueAction { Path = Key, Name = "Flag", Value = "0" });

        Assert.Equal(TweakState.NotApplied, await engine.DetectAsync(tweak));
        await engine.ApplyAsync(tweak);
        Assert.Equal(TweakState.Applied, await engine.DetectAsync(tweak));
        Assert.Equal("0", sys.Registry.GetValue(Key, "Flag")!.Data);

        await engine.RevertAsync(tweak);
        Assert.Null(sys.Registry.GetValue(Key, "Flag"));
        Assert.Null(sys.Backups.Get(tweak.Id));
    }

    [Fact]
    public async Task Registry_value_is_restored_exactly_including_kind()
    {
        var sys = new TestSystem();
        sys.Registry.SetValue(Key, "Mode", new RegValue(RegistryValueKind.String, "Medium"));
        var engine = sys.CreateEngine();
        var tweak = TestSystem.Tweak(new RegistryValueAction { Path = Key, Name = "Mode", Kind = RegistryValueKind.String, Value = "High" });

        await engine.ApplyAsync(tweak);
        await engine.RevertAsync(tweak);

        Assert.Equal(new RegValue(RegistryValueKind.String, "Medium"), sys.Registry.GetValue(Key, "Mode"));
    }

    [Fact]
    public async Task Hex_dword_is_detected_as_same_value()
    {
        var sys = new TestSystem();
        sys.Registry.SetValue(Key, "Index", RegValue.DWord(uint.MaxValue));
        var tweak = TestSystem.Tweak(new RegistryValueAction { Path = Key, Name = "Index", Value = "0xFFFFFFFF" });

        Assert.Equal(TweakState.Applied, await sys.CreateEngine().DetectAsync(tweak));
    }

    [Fact]
    public async Task Reapplying_keeps_the_original_backup()
    {
        var sys = new TestSystem();
        sys.Registry.SetValue(Key, "Flag", RegValue.DWord(5));
        var engine = sys.CreateEngine();
        var tweak = TestSystem.Tweak(new RegistryValueAction { Path = Key, Name = "Flag", Value = "0" });

        await engine.ApplyAsync(tweak);
        sys.Registry.SetValue(Key, "Flag", RegValue.DWord(7)); // например, значение вернуло обновление Windows
        await engine.ApplyAsync(tweak);
        await engine.RevertAsync(tweak);

        Assert.Equal("5", sys.Registry.GetValue(Key, "Flag")!.Data);
    }

    [Fact]
    public async Task Failure_rolls_back_already_applied_actions()
    {
        var sys = new TestSystem();
        sys.Services.StartTypes["Good"] = ServiceStartType.Automatic;
        sys.Services.StartTypes["Locked"] = ServiceStartType.Manual;
        sys.Services.Failing.Add("Locked");
        var engine = sys.CreateEngine();
        var tweak = TestSystem.Tweak(
            new RegistryValueAction { Path = Key, Name = "Flag", Value = "1" },
            new ServiceStartupAction { Service = "Good" },
            new ServiceStartupAction { Service = "Locked" });

        await Assert.ThrowsAsync<TweakException>(() => engine.ApplyAsync(tweak));

        Assert.Null(sys.Registry.GetValue(Key, "Flag"));
        Assert.Equal(ServiceStartType.Automatic, sys.Services.StartTypes["Good"]);
        Assert.Null(sys.Backups.Get(tweak.Id));
    }

    [Fact]
    public async Task Optional_failure_does_not_abort_tweak()
    {
        var sys = new TestSystem();
        sys.Tasks.Tasks[@"\A"] = true;
        sys.Tasks.Tasks[@"\B"] = true;
        sys.Tasks.Protected.Add(@"\B");
        var engine = sys.CreateEngine();
        var tweak = TestSystem.Tweak(
            new ScheduledTaskAction { Path = @"\A", Optional = true },
            new ScheduledTaskAction { Path = @"\B", Optional = true });

        var result = await engine.ApplyAsync(tweak);

        Assert.Single(result.Warnings);
        Assert.False(sys.Tasks.Tasks[@"\A"]);
        Assert.Equal(TweakState.Applied, await engine.DetectAsync(tweak));
    }

    [Fact]
    public async Task Optional_only_tweak_is_not_applied_when_user_disabled_just_one_task()
    {
        var sys = new TestSystem();
        sys.Tasks.Tasks[@"\A"] = false; // пользователь отключил сам
        sys.Tasks.Tasks[@"\B"] = true;
        var tweak = TestSystem.Tweak(
            new ScheduledTaskAction { Path = @"\A", Optional = true },
            new ScheduledTaskAction { Path = @"\B", Optional = true });

        Assert.Equal(TweakState.NotApplied, await sys.CreateEngine().DetectAsync(tweak));
    }

    [Fact]
    public async Task Exclusive_group_reverts_neighbour_so_original_state_is_restored()
    {
        var sys = new TestSystem();
        var engine = sys.CreateEngine();
        var high = new TweakDefinition
        {
            Id = "power.high", Title = "High", Description = "d", ExclusiveGroup = "power",
            Actions = [new PowerPlanAction { Scheme = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), CopyGuid = Guid.NewGuid() }],
        };
        var ultimate = new TweakDefinition
        {
            Id = "power.ultimate", Title = "Ultimate", Description = "d", ExclusiveGroup = "power",
            Actions = [new PowerPlanAction { Scheme = new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61"), CopyGuid = Guid.NewGuid() }],
        };
        TweakDefinition[] all = [high, ultimate];

        await engine.RevertConflictsAsync(high, all);
        await engine.ApplyAsync(high);
        await engine.RevertConflictsAsync(ultimate, all);
        await engine.ApplyAsync(ultimate);

        Assert.Null(sys.Backups.Get(high.Id));
        Assert.Equal(TweakState.NotApplied, await engine.DetectAsync(high));

        await engine.RevertAsync(ultimate);
        Assert.Equal(PowerPlanAction.Balanced, sys.Power.Active);
    }

    [Fact]
    public async Task Missing_targets_make_tweak_not_applicable()
    {
        var sys = new TestSystem();
        var tweak = TestSystem.Tweak(
            new ServiceStartupAction { Service = "Absent" },
            new ScheduledTaskAction { Path = @"\Absent" });

        Assert.Equal(TweakState.NotApplicable, await sys.CreateEngine().DetectAsync(tweak));
    }

    [Fact]
    public async Task Service_start_type_roundtrip()
    {
        var sys = new TestSystem();
        sys.Services.StartTypes["MapsBroker"] = ServiceStartType.AutomaticDelayed;
        var engine = sys.CreateEngine();
        var tweak = TestSystem.Tweak(new ServiceStartupAction { Service = "MapsBroker" });

        await engine.ApplyAsync(tweak);
        Assert.Equal(ServiceStartType.Disabled, sys.Services.StartTypes["MapsBroker"]);
        await engine.RevertAsync(tweak);
        Assert.Equal(ServiceStartType.AutomaticDelayed, sys.Services.StartTypes["MapsBroker"]);
    }

    [Fact]
    public async Task Revert_without_backup_uses_windows_default()
    {
        var sys = new TestSystem();
        sys.Services.StartTypes["DiagTrack"] = ServiceStartType.Disabled; // отключено до нас
        sys.Registry.SetValue(Key, "Flag", RegValue.DWord(0));
        var engine = sys.CreateEngine();
        var tweak = TestSystem.Tweak(
            new ServiceStartupAction { Service = "DiagTrack", Default = ServiceStartType.Automatic },
            new RegistryValueAction { Path = Key, Name = "Flag", Value = "0", Default = "1" });

        await engine.RevertAsync(tweak);

        Assert.Equal(ServiceStartType.Automatic, sys.Services.StartTypes["DiagTrack"]);
        Assert.Equal("1", sys.Registry.GetValue(Key, "Flag")!.Data);
    }

    [Fact]
    public async Task Removed_app_is_reinstalled_from_store_on_revert()
    {
        var sys = new TestSystem();
        sys.Apps.Installed.Add("Clipchamp.Clipchamp");
        sys.Apps.StorePackages["9P1J8S7CCWWT"] = "Clipchamp.Clipchamp";
        var engine = sys.CreateEngine();
        var tweak = TestSystem.Tweak(new AppxPackageAction { Package = "Clipchamp.Clipchamp", StoreId = "9P1J8S7CCWWT" });

        await engine.ApplyAsync(tweak);
        Assert.False(sys.Apps.IsInstalled("Clipchamp.Clipchamp"));
        Assert.Equal(TweakState.Applied, await engine.DetectAsync(tweak));

        await engine.RevertAsync(tweak);
        Assert.Equal(["9P1J8S7CCWWT"], sys.Apps.StoreInstalls);
    }

    [Fact]
    public async Task Power_plan_creates_copy_when_scheme_is_hidden_and_restores_previous()
    {
        var sys = new TestSystem();
        var high = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        var copy = Guid.NewGuid();
        var engine = sys.CreateEngine();
        var tweak = TestSystem.Tweak(new PowerPlanAction { Scheme = high, CopyGuid = copy });

        await engine.ApplyAsync(tweak);
        Assert.Equal(copy, sys.Power.Active);
        Assert.Equal(TweakState.Applied, await engine.DetectAsync(tweak));

        await engine.RevertAsync(tweak);
        Assert.Equal(PowerPlanAction.Balanced, sys.Power.Active);
    }

    [Fact]
    public async Task List_entry_preserves_other_settings()
    {
        var sys = new TestSystem();
        const string path = @"HKCU\Software\Microsoft\DirectX\UserGpuPreferences";
        sys.Registry.SetValue(path, "DirectXUserGlobalSettings", RegValue.String("AutoHDREnable=1;VRROptimizeEnable=0;"));
        var engine = sys.CreateEngine();
        var tweak = TestSystem.Tweak(new RegistryListEntryAction { Path = path, Name = "DirectXUserGlobalSettings", Key = "SwapEffectUpgradeEnable", Value = "1" });

        await engine.ApplyAsync(tweak);
        Assert.Equal("AutoHDREnable=1;VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;", sys.Registry.GetValue(path, "DirectXUserGlobalSettings")!.Data);

        await engine.RevertAsync(tweak);
        Assert.Equal("AutoHDREnable=1;VRROptimizeEnable=0;", sys.Registry.GetValue(path, "DirectXUserGlobalSettings")!.Data);
    }

    [Fact]
    public async Task Tcp_no_delay_touches_only_interfaces_with_address()
    {
        var sys = new TestSystem();
        var ifaces = TcpNoDelayAction.InterfacesKey;
        sys.Registry.SetValue($@"{ifaces}\{{A}}", "DhcpIPAddress", RegValue.String("192.168.1.5"));
        sys.Registry.SetValue($@"{ifaces}\{{B}}", "DhcpIPAddress", RegValue.String("0.0.0.0"));
        sys.Registry.SetValue($@"{ifaces}\{{A}}", "TcpAckFrequency", RegValue.DWord(2));
        var engine = sys.CreateEngine();
        var tweak = TestSystem.Tweak(new TcpNoDelayAction());

        await engine.ApplyAsync(tweak);
        Assert.Equal("1", sys.Registry.GetValue($@"{ifaces}\{{A}}", "TCPNoDelay")!.Data);
        Assert.Null(sys.Registry.GetValue($@"{ifaces}\{{B}}", "TCPNoDelay"));

        await engine.RevertAsync(tweak);
        Assert.Equal("2", sys.Registry.GetValue($@"{ifaces}\{{A}}", "TcpAckFrequency")!.Data);
        Assert.Null(sys.Registry.GetValue($@"{ifaces}\{{A}}", "TCPNoDelay"));
    }
}
