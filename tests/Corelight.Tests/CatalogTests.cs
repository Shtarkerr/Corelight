using Corelight.Core.Catalog;
using Corelight.Core.Tweaks;

namespace Corelight.Tests;

public class CatalogTests
{
    [Fact]
    public void Embedded_catalog_loads_without_rejections()
    {
        var catalog = TweakCatalog.LoadEmbedded();

        Assert.Empty(catalog.Rejected);
        Assert.True(catalog.Tweaks.Count > 50);
        foreach (var category in Enum.GetValues<TweakCategory>())
            Assert.NotEmpty(catalog.InCategory(category));
    }

    [Fact]
    public void Advanced_and_caution_tweaks_explain_consequences()
    {
        var catalog = TweakCatalog.LoadEmbedded();
        var withoutWarning = catalog.Tweaks
            .Where(t => t.Risk == TweakRisk.Advanced && string.IsNullOrWhiteSpace(t.Warning))
            .Select(t => t.Id);

        Assert.Empty(withoutWarning);
    }

    [Theory]
    [InlineData("""{ "type": "registry", "path": "HKLM\\SYSTEM\\CurrentControlSet\\Control\\DeviceGuard\\Scenarios\\HypervisorEnforcedCodeIntegrity", "name": "Enabled", "value": "0" }""")]
    [InlineData("""{ "type": "registry", "path": "HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows Defender", "name": "DisableAntiSpyware", "value": "1" }""")]
    [InlineData("""{ "type": "registry", "path": "HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU", "name": "NoAutoUpdate", "value": "1" }""")]
    [InlineData("""{ "type": "registry", "path": "HKLM\\SYSTEM\\CurrentControlSet\\Control\\Power\\PowerSettings\\54533251-82be-4824-96c1-47b60b740d00", "name": "Attributes", "value": "2" }""")]
    [InlineData("""{ "type": "registry", "path": "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", "name": "EnableTransparency", "value": "0" }""")]
    [InlineData("""{ "type": "registry", "path": "HKCU\\Control Panel\\Desktop", "name": "MenuShowDelay", "kind": "String", "value": "0" }""")]
    [InlineData("""{ "type": "registry", "path": "HKLM\\SYSTEM\\CurrentControlSet\\Services\\vgk", "name": "Start", "value": "4" }""")]
    [InlineData("""{ "type": "registry", "path": "HKLM\\SYSTEM\\ControlSet001\\Services\\WinDefend\\Parameters", "name": "X", "value": "1" }""")]
    [InlineData("""{ "type": "service", "service": "WinDefend" }""")]
    [InlineData("""{ "type": "service", "service": "vgc" }""")]
    [InlineData("""{ "type": "service", "service": "wuauserv" }""")]
    [InlineData("""{ "type": "appx", "package": "Microsoft.WindowsStore" }""")]
    [InlineData("""{ "type": "appx", "package": "Microsoft.MicrosoftEdge.Stable" }""")]
    [InlineData("""{ "type": "task", "path": "\\Microsoft\\Windows\\WindowsUpdate\\Scheduled Start" }""")]
    [InlineData("""{ "type": "powerPlan", "scheme": "11111111-2222-3333-4444-555555555555", "copyGuid": "6f1c2a4e-3b7d-4e0a-9c51-0d2b8f7a1e09" }""")]
    public void Guardrails_reject_forbidden_targets(string actionJson)
    {
        var json = $$"""
            { "category": "Privacy", "tweaks": [ {
                "id": "bad", "title": "Плохой твик", "description": "Описание",
                "actions": [ {{actionJson}} ]
            } ] }
            """;

        var catalog = TweakCatalog.Load([("bad.json", json)]);

        Assert.Empty(catalog.Tweaks);
        Assert.Single(catalog.Rejected);
    }

    [Fact]
    public void Duplicate_ids_and_unknown_conditions_are_rejected()
    {
        const string json = """
            { "category": "Gaming", "tweaks": [
                { "id": "x", "title": "A", "description": "a", "actions": [ { "type": "registry", "path": "HKCU\\Software\\Test", "name": "A", "value": "1" } ] },
                { "id": "x", "title": "B", "description": "b", "actions": [ { "type": "registry", "path": "HKCU\\Software\\Test", "name": "B", "value": "1" } ] },
                { "id": "y", "title": "C", "description": "c", "recommendWhen": [ "gamer" ], "actions": [ { "type": "registry", "path": "HKCU\\Software\\Test", "name": "C", "value": "1" } ] }
            ] }
            """;

        var catalog = TweakCatalog.Load([("t.json", json)]);

        Assert.Single(catalog.Tweaks);
        Assert.Equal(2, catalog.Rejected.Count);
    }
}
