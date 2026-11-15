using Corelight.Core.Actions;
using Corelight.Core.Catalog;
using Corelight.Core.Platform;
using Corelight.Core.Tweaks;

namespace Corelight.Tests;

public class ComponentPresenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Edge_requires_executable_not_just_registration_or_policy(bool executableExists)
    {
        var registry = new FakeRegistry();
        const string executable = @"C:\CustomEdge\msedge.exe";
        registry.SetValue(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe", "", RegValue.String(executable));
        registry.SetValue(@"HKLM\SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", RegValue.DWord(1));
        var probe = new WindowsComponentPresence(registry, new FeatureRunner())
        {
            FileExists = path => executableExists && path == executable,
        };
        Assert.Equal(executableExists, await probe.IsPresentAsync(WindowsComponent.Edge, default));
    }

    [Fact]
    public async Task Catalog_does_not_suggest_absent_apps_services_or_tasks()
    {
        var engine = new TestSystem().CreateEngine();
        foreach (var tweak in TweakCatalog.LoadEmbedded().Tweaks)
        {
            if (tweak.Actions.All(a => a is AppxPackageAction or OneDriveAction))
                Assert.Equal(TweakState.Applied, await engine.DetectAsync(tweak));
            if (tweak.Actions.All(a => a is ServiceStartupAction or ScheduledTaskAction))
                Assert.Equal(TweakState.NotApplicable, await engine.DetectAsync(tweak));
        }
    }

    [Theory]
    [InlineData("MSTeams")]
    [InlineData("MicrosoftTeams")]
    public async Task Either_teams_variant_is_detected_and_removed(string package)
    {
        var sys = new TestSystem();
        sys.Apps.Installed.Add(package);
        var tweak = TweakCatalog.LoadEmbedded().Find("apps.teams-personal")!;
        var engine = sys.CreateEngine();
        Assert.Equal(TweakState.NotApplied, await engine.DetectAsync(tweak));
        await engine.ApplyAsync(tweak);
        Assert.Empty(sys.Apps.Installed);
        Assert.Equal(TweakState.Applied, await engine.DetectAsync(tweak));
    }

    [Theory]
    [InlineData("interface.edge-background")]
    [InlineData("privacy.recall")]
    [InlineData("gaming.game-dvr")]
    [InlineData("privacy.error-reporting")]
    [InlineData("network.delivery-optimization")]
    [InlineData("network.throttling")]
    [InlineData("gaming.mmcss-games")]
    public async Task Component_settings_require_actual_component_and_recheck_before_apply(string id)
    {
        var sys = new TestSystem();
        var tweak = TweakCatalog.LoadEmbedded().Find(id)!;
        var engine = sys.CreateEngine();
        Assert.Equal(TweakState.NotApplicable, await engine.DetectAsync(tweak));
        sys.Registry.DenyWrites = true;
        await engine.ApplyAsync(tweak);
        Assert.Empty(sys.Registry.Values);
        await engine.RevertAsync(tweak);
        sys.Registry.DenyWrites = false;

        foreach (var action in tweak.Actions.OfType<RegistryValueAction>())
        {
            if (action.RequiredComponent is { } component) sys.Components.Present.Add(component);
            if (action.RequiredPackage is { } package) sys.Apps.Installed.Add(package);
            if (action.RequiredService is { } service) sys.Services.StartTypes[service] = ServiceStartType.Manual;
        }
        Assert.Equal(TweakState.NotApplied, await engine.DetectAsync(tweak));
        await engine.ApplyAsync(tweak);
        Assert.Equal(TweakState.Applied, await engine.DetectAsync(tweak));
        sys.Components.Present.Clear();
        sys.Apps.Installed.Clear();
        sys.Services.StartTypes.Clear();
        await engine.RevertAsync(tweak);
        Assert.Empty(sys.Registry.Values);
        Assert.Empty(sys.Backups.GetAll());
        // Откат service-действия восстановил тип запуска в fake-адаптере.
        // Следующая проверка прямого применения моделирует отсутствие службы заново.
        sys.Services.StartTypes.Clear();
        sys.Registry.DenyWrites = true;
        foreach (var action in tweak.Actions.OfType<RegistryValueAction>()) await action.ApplyAsync(sys.Platform, default);
    }

    [Fact]
    public async Task Unknown_presence_is_not_a_recommendation_and_cannot_mutate_or_create_backup()
    {
        var sys = new TestSystem();
        sys.Components.Fail = true;
        var tweak = TweakCatalog.LoadEmbedded().Find("privacy.recall")!;
        var engine = sys.CreateEngine();
        Assert.Equal(TweakState.NotApplicable, await engine.DetectAsync(tweak));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ApplyAsync(tweak));
        Assert.Empty(sys.Registry.Values);
        Assert.Empty(sys.Backups.GetAll());
    }

    [Theory]
    [InlineData("Enabled", true)]
    [InlineData("Disabled", false)]
    [InlineData("DisabledWithPayloadRemoved", false)]
    [InlineData("NotPresent", false)]
    public void Recall_checks_feature_state(string output, bool expected) =>
        Assert.Equal(expected, WindowsComponentPresence.ParseRecallState(output));

    [Theory]
    [InlineData("")]
    [InlineData("EnablePending")]
    [InlineData("DisablePending")]
    public void Uncertain_recall_state_is_not_treated_as_enabled(string output) =>
        Assert.Throws<InvalidOperationException>(() => WindowsComponentPresence.ParseRecallState(output));

    [Fact]
    public async Task Recall_inventory_is_refreshed_between_scans()
    {
        var runner = new FeatureRunner();
        var probe = new WindowsComponentPresence(new FakeRegistry(), runner);
        Assert.True(await probe.IsPresentAsync(WindowsComponent.Recall, default));
        runner.Output = "Disabled";
        Assert.True(await probe.IsPresentAsync(WindowsComponent.Recall, default));
        Assert.Equal(1, runner.Calls);
        probe.Refresh();
        Assert.False(await probe.IsPresentAsync(WindowsComponent.Recall, default));
        Assert.Equal(2, runner.Calls);
    }

    private sealed class FeatureRunner : IProcessRunner
    {
        public string Output { get; set; } = "Enabled";
        public int Calls { get; private set; }
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default)
        {
            Calls++;
            Assert.Equal("powershell.exe", fileName);
            Assert.Contains("Get-WindowsOptionalFeature", arguments.Last());
            return Task.FromResult(new ProcessResult(0, Output, ""));
        }
    }
}
