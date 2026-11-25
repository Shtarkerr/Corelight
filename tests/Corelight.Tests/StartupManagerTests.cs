using Microsoft.Win32;
using Corelight.Core.Platform;
using Corelight.Core.Startup;

namespace Corelight.Tests;

public class StartupManagerTests
{
    private const string RunKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    [Fact]
    public void Reads_state_the_same_way_as_task_manager()
    {
        var registry = new FakeRegistry();
        registry.SetValue(RunKey, "NoApproval", RegValue.String("a.exe"));
        registry.SetValue(RunKey, "Enabled", RegValue.String("b.exe"));
        registry.SetValue(RunKey, "Disabled", RegValue.String("c.exe"));
        registry.SetValue(RunKey, "Garbage", RegValue.String("d.exe"));
        registry.SetValue(RunKey, "SecurityHealth", RegValue.String("e.exe"));
        registry.SetValue(ApprovedKey, "Enabled", Binary("020000000000000000000000"));
        registry.SetValue(ApprovedKey, "Disabled", Binary("030000001122334455667788"));
        registry.SetValue(ApprovedKey, "Garbage", RegValue.DWord(3)); // не тот тип — Windows его игнорирует

        var items = new StartupManager(registry, _ => "").GetItems().Items.ToDictionary(i => i.Name);

        Assert.True(items["NoApproval"].IsEnabled);
        Assert.True(items["Enabled"].IsEnabled);
        Assert.False(items["Disabled"].IsEnabled);
        Assert.True(items["Garbage"].IsEnabled);
        Assert.DoesNotContain("SecurityHealth", items.Keys);
    }

    [Fact]
    public void Toggle_roundtrip()
    {
        var registry = new FakeRegistry();
        registry.SetValue(RunKey, "App", RegValue.String("app.exe"));
        var manager = new StartupManager(registry, _ => "");
        var item = Assert.Single(manager.GetItems().Items);

        Assert.False(manager.SetEnabled(item, false));
        Assert.False(Assert.Single(manager.GetItems().Items).IsEnabled);

        Assert.True(manager.SetEnabled(item, true));
        Assert.True(Assert.Single(manager.GetItems().Items).IsEnabled);
    }

    [Fact]
    public void Unavailable_folder_does_not_hide_registry_sources()
    {
        var registry = new FakeRegistry();
        registry.SetValue(RunKey, "App", RegValue.String("app.exe"));
        var manager = new StartupManager(registry, _ => throw new UnauthorizedAccessException());

        var scan = manager.GetItems();

        Assert.Equal("App", Assert.Single(scan.Items).Name);
        Assert.Equal(2, scan.Problems.Count);
    }

    [Fact]
    public void Changed_command_is_not_disabled_from_a_stale_row()
    {
        var registry = new FakeRegistry();
        registry.SetValue(RunKey, "App", RegValue.String("app.exe"));
        var manager = new StartupManager(registry, _ => "");
        var item = Assert.Single(manager.GetItems().Items);
        registry.SetValue(RunKey, "App", RegValue.String("other.exe"));

        Assert.Throws<InvalidOperationException>(() => manager.SetEnabled(item, false));
        Assert.Null(registry.GetValue(ApprovedKey, "App"));
    }

    [Theory]
    [InlineData("Teams", true)]
    [InlineData("Teams Backup", false)]
    [InlineData("OneDrive", true)]
    [InlineData("MyOneDriveStartup", false)]
    [InlineData("MicrosoftEdgeAutoLaunch_ABC", true)]
    [InlineData("MicrosoftEdgeAutoLauncher", false)]
    public void Autostart_hint_uses_known_exact_names_and_edge_prefix(string name, bool expected)
    {
        var registry = new FakeRegistry();
        registry.SetValue(RunKey, name, RegValue.String("test.exe"));

        var item = Assert.Single(new StartupManager(registry, _ => "").GetItems().Items);

        Assert.Equal(expected, item.IsKnownUpdater);
    }

    private static RegValue Binary(string hex) => new(RegistryValueKind.Binary, hex);
}
