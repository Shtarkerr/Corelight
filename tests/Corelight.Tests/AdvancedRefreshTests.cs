using Corelight.App.Services;
using Corelight.App.ViewModels;
using Corelight.Core.Actions;
using Corelight.Core.Catalog;
using Corelight.Core.Platform;
using Corelight.Core.Scanning;
using Corelight.Core.Startup;
using Corelight.Core.SystemInfo;
using Corelight.Core.Tweaks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Corelight.Tests;

public sealed class AdvancedRefreshTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Reopening_advanced_page_reads_external_change_without_internal_version_increment()
    {
        var sys = new TestSystem();
        const string key = @"HKCU\Software\Corelight.Tests\AdvancedRefresh";
        var tweak = new TweakDefinition
        {
            Id = "test.external-change",
            Title = "Внешнее изменение",
            Description = "Тест обновления страницы",
            Category = TweakCategory.Privacy,
            Actions = [new RegistryValueAction { Path = key, Name = "Flag", Value = "1" }],
        };
        var catalog = new TweakCatalog([tweak], []);
        var engine = sys.CreateEngine();
        var scanner = new Scanner(catalog, engine, new HardwareProbe(sys.Registry, NullLogger<HardwareProbe>.Instance));
        var state = new AppState(scanner);
        var page = new AdvancedViewModel(catalog, engine, scanner, new StartupManager(sys.Registry), null!, state, null!, NullLogger<AdvancedViewModel>.Instance);

        await page.OnNavigatedToAsync();
        Assert.False(GetItem(page).IsOn);
        sys.Registry.SetValue(key, "Flag", RegValue.DWord(1)); // Внешнее изменение: AppState не уведомлён.
        Assert.Equal(0, state.ChangeVersion);

        await page.OnNavigatedToAsync();
        Assert.True(GetItem(page).IsOn);
    }

    private static TweakItemViewModel GetItem(AdvancedViewModel page) =>
        page.Sections.SelectMany(s => s.Items).OfType<TweakItemViewModel>().Single();
}
