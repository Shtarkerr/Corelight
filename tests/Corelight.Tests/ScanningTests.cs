using Corelight.Core.Catalog;
using Corelight.Core.Scanning;
using Corelight.Core.Tweaks;
using Corelight.Core.SystemInfo;

namespace Corelight.Tests;

public class ScanningTests
{
    [Fact]
    public void Suggestions_are_sorted_by_descending_impact_and_keep_equal_items_in_order()
    {
        TweakStatus Status(string id, TweakImpact impact, TweakState state = TweakState.NotApplied) =>
            new(new TweakDefinition { Id = id, Title = id, Description = id, Impact = impact }, state, null, true);
        var report = new ScanReport(new HardwareProfile(), [
            Status("low", TweakImpact.Low), Status("medium-a", TweakImpact.Medium),
            Status("applied", TweakImpact.High, TweakState.Applied), Status("high", TweakImpact.High),
            Status("medium-b", TweakImpact.Medium)]);

        Assert.Equal(new[] { "high", "medium-a", "medium-b", "low" }, report.Suggestions.Select(s => s.Tweak.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Widgets_policy_denial_is_explained_and_already_disabled_policy_is_not_written(bool alreadyDisabled)
    {
        var sys = new TestSystem();
        const string path = @"HKLM\SOFTWARE\Policies\Microsoft\Dsh";
        sys.Registry.SetValue(path, "AllowNewsAndInterests", Corelight.Core.Platform.RegValue.DWord(alreadyDisabled ? 0u : 1u));
        sys.Registry.DenyWrites = true;
        var tweak = TweakCatalog.LoadEmbedded().Find("interface.widgets")!;
        var engine = sys.CreateEngine();

        if (alreadyDisabled)
        {
            Assert.Equal(TweakState.Applied, await engine.DetectAsync(tweak));
            Assert.True((await engine.ApplyAsync(tweak)).Confirmed);
        }
        else
        {
            var error = await Assert.ThrowsAsync<TweakException>(() => engine.ApplyAsync(tweak));
            Assert.Contains("Windows запретила", error.Message);
            Assert.Contains("AllowNewsAndInterests", error.Message);
            Assert.Equal("1", sys.Registry.GetValue(path, "AllowNewsAndInterests")!.Data);
            Assert.Null(sys.Backups.Get(tweak.Id));
            Assert.False(new TweakStatus(tweak, TweakState.NotApplied, null, true).IsPreselected);
        }
    }

    [Theory]
    [InlineData("apps.outlook-new")]
    [InlineData("apps.teams-personal")]
    [InlineData("gaming.game-dvr")]
    public void Requested_defaults_are_selected_and_explain_lost_functionality(string id)
    {
        var tweak = TweakCatalog.LoadEmbedded().Find(id)!;
        var status = new TweakStatus(tweak, TweakState.NotApplied, null, tweak.Recommended);

        Assert.True(status.IsPreselected);
        Assert.Equal(TweakRisk.Caution, tweak.Risk);
        Assert.False(string.IsNullOrWhiteSpace(tweak.Warning));
    }

    [Fact]
    public void Other_optional_features_remain_manual_choices()
    {
        var catalog = TweakCatalog.LoadEmbedded();
        foreach (var id in new[] { "gaming.mouse-precision", "gaming.power-ultimate", "apps.xbox-game-bar" })
        {
            var status = new TweakStatus(catalog.Find(id)!, TweakState.NotApplied, null, true);
            Assert.False(status.IsPreselected);
        }
    }

    [Fact]
    public void Explicit_default_does_not_select_applied_or_unavailable_features()
    {
        var tweak = TweakCatalog.LoadEmbedded().Find("apps.outlook-new")!;

        Assert.False(new TweakStatus(tweak, TweakState.Applied, null, true).IsPreselected);
        Assert.False(new TweakStatus(tweak, TweakState.NotApplicable, null, true).IsPreselected);
        Assert.False(new TweakStatus(tweak, TweakState.NotApplied, "Недоступно", true).IsPreselected);
        Assert.False(new TweakStatus(tweak, TweakState.NotApplied, null, false).IsPreselected);
    }
}
