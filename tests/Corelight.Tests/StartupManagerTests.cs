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

        var items = new StartupManager(registry).GetItems().Where(i => i.Location.StartsWith("Реестр")).ToDictionary(i => i.Name);

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
        var manager = new StartupManager(registry);
        var item = manager.GetItems().Single(i => i.Name == "App");

        manager.SetEnabled(item, false);
        Assert.False(manager.GetItems().Single(i => i.Name == "App").IsEnabled);

        manager.SetEnabled(item, true);
        Assert.True(manager.GetItems().Single(i => i.Name == "App").IsEnabled);
    }

    private static RegValue Binary(string hex) => new(RegistryValueKind.Binary, hex);
}
