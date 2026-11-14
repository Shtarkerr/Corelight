using Corelight.Core.Catalog;
using Corelight.Core.Scanning;
using Corelight.Core.Tweaks;

namespace Corelight.Tests;

public class ScanningTests
{
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
