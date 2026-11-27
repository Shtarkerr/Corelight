using Corelight.App.Services;
using Corelight.App.ViewModels;
using Corelight.Core.Catalog;
using Corelight.Core.Cleanup;
using Corelight.Core.Scanning;
using Corelight.Core.Tweaks;
using Corelight.Core.SystemInfo;
using Corelight.Core.Actions;
using Corelight.Core.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace Corelight.Tests;

public class ScanningTests
{
    [Fact]
    public void Results_tab_appears_in_order_once_the_first_scan_completes()
    {
        var state = new AppState(null!);
        var navigation = new NavigationService();
        var results = new ScanResultsViewModel(state, null!, navigation, null!, NullLogger<ScanResultsViewModel>.Instance);
        var main = new MainViewModel(state, navigation, null!, null!, NullLogger<MainViewModel>.Instance,
            new HomeViewModel(state, null!, navigation, null!, NullLogger<HomeViewModel>.Instance), results,
            new AdvancedViewModel(null!, null!, state, null!, NullLogger<AdvancedViewModel>.Instance),
            new HistoryViewModel(null!, null!, null!, null!, state, NullLogger<HistoryViewModel>.Instance),
            new CleanupViewModel(new DiskCleanupService([], null!, NullLogger<DiskCleanupService>.Instance), state,
                null!, NullLogger<CleanupViewModel>.Instance));

        Assert.Equal(new[] { "Главная", "Расширенное", "Очистка диска", "История и откат" },
            main.Items.Select(item => item.DisplayTitle));
        navigation.NavigateTo<ScanResultsViewModel>();
        Assert.Null(main.CurrentPage);

        var report = new ScanReport(new HardwareProfile(), []);
        state.SetUserScan(report);
        Assert.Equal(new[] { "Главная", "Результаты", "Расширенное", "Очистка диска", "История и откат" },
            main.Items.Select(item => item.DisplayTitle));
        navigation.NavigateTo<ScanResultsViewModel>();
        Assert.Same(results, main.CurrentPage);
        Assert.True(results.HasScan);

        state.SetUserScan(report);
        Assert.Single(main.Items, item => ReferenceEquals(item.Page, results));
    }

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

    [Fact]
    public void Scan_reports_available_manual_items_without_selecting_them()
    {
        TweakStatus Status(string id, TweakState state, bool recommended, string? reason = null, TweakRisk risk = TweakRisk.Safe) =>
            new(new TweakDefinition { Id = id, Title = id, Description = id, Risk = risk }, state, reason, recommended);
        var report = new ScanReport(new HardwareProfile(),
        [
            Status("suggestion", TweakState.NotApplied, true),
            Status("manual", TweakState.NotApplied, false),
            Status("optional", TweakState.NotApplied, true, risk: TweakRisk.Caution),
            Status("applied", TweakState.Applied, false),
            Status("unavailable", TweakState.NotApplied, false, "Не поддерживается"),
            Status("unknown", TweakState.Unknown, false),
            Status("absent", TweakState.NotApplicable, false)
        ]);

        Assert.Equal(new[] { "optional", "manual" }, report.ManualItems.Select(s => s.Tweak.Id));
        Assert.Equal("unknown", Assert.Single(report.UnknownItems).Tweak.Id);
        Assert.All(report.ManualItems, item => Assert.False(item.IsPreselected));
        Assert.Equal("suggestion", Assert.Single(report.Suggestions, s => s.IsPreselected).Tweak.Id);
    }

    [Fact]
    public async Task Scan_page_keeps_optional_choices_selectable_under_a_collapsible_section()
    {
        var catalog = TweakCatalog.LoadEmbedded();
        TweakStatus Status(string id)
        {
            var tweak = catalog.Find(id)!;
            return new(tweak, TweakState.NotApplied, null, tweak.Recommended);
        }
        var state = new AppState(null!);
        state.SetUserScan(new ScanReport(new HardwareProfile(),
            [Status("apps.outlook-new"), Status("apps.onedrive"), Status("apps.photos")]));
        var page = new ScanResultsViewModel(state, null!, new NavigationService(), null!, NullLogger<ScanResultsViewModel>.Instance);

        await page.OnNavigatedToAsync();

        Assert.False(page.IsManualExpanded);
        var outlook = Assert.Single(Assert.Single(page.Groups).Items);
        Assert.Equal("apps.outlook-new", outlook.Tweak.Id);
        Assert.True(outlook.IsSelected);
        Assert.Collection(page.ManualItems,
            oneDrive =>
            {
                Assert.Equal("apps.onedrive", oneDrive.Tweak.Id);
                Assert.False(oneDrive.IsSelected);
                Assert.True(oneDrive.HasWarning);
            },
            photos =>
            {
                Assert.Equal("apps.photos", photos.Tweak.Id);
                Assert.False(photos.IsSelected);
            });
        Assert.Equal(1, page.SelectedCount);
        Assert.Equal(3, page.TotalCount);

        page.SelectNoneCommand.Execute(null);
        Assert.False(page.ApplyCommand.CanExecute(null));
        page.IsManualExpanded = true;
        page.ManualItems[0].IsSelected = true;
        Assert.True(page.ApplyCommand.CanExecute(null));
        Assert.Equal(1, page.SelectedCount);
        Assert.Contains("выбрано 1", page.ManualItemsHeader);

        await page.OnNavigatedToAsync();
        Assert.True(page.IsManualExpanded);
        Assert.True(page.ManualItems[0].IsSelected);

        page.SelectRecommendedCommand.Execute(null);
        Assert.Equal(2, page.SelectedCount);
        Assert.False(page.ManualItems[1].IsSelected);
        page.ManualItems[1].IsSelected = true;
        Assert.Equal(3, page.SelectedCount);

        state.SetUserScan(state.LastScan!);
        await page.OnNavigatedToAsync();
        Assert.False(page.IsManualExpanded);
        Assert.Equal(1, page.SelectedCount);
    }

    [Fact]
    public async Task Scan_page_selects_only_one_power_plan_across_the_two_lists()
    {
        var catalog = TweakCatalog.LoadEmbedded();
        var high = catalog.Find("gaming.power-high")!;
        var ultimate = catalog.Find("gaming.power-ultimate")!;
        var state = new AppState(null!);
        state.SetUserScan(new ScanReport(new HardwareProfile(),
            [new(high, TweakState.NotApplied, null, true), new(ultimate, TweakState.NotApplied, null, false)]));
        var page = new ScanResultsViewModel(state, null!, new NavigationService(), null!, NullLogger<ScanResultsViewModel>.Instance);
        await page.OnNavigatedToAsync();

        var recommended = Assert.Single(Assert.Single(page.Groups).Items);
        var manual = Assert.Single(page.ManualItems);
        Assert.True(recommended.IsSelected);
        manual.IsSelected = true;
        Assert.False(recommended.IsSelected);
        Assert.Equal(1, page.SelectedCount);

        page.SelectRecommendedCommand.Execute(null);
        Assert.True(recommended.IsSelected);
        Assert.False(manual.IsSelected);
        Assert.Equal(1, page.SelectedCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Automatic_refresh_preserves_existing_choices_until_a_new_user_scan()
    {
        var sys = new TestSystem();
        TweakDefinition Option(string id, bool recommended) => new()
        {
            Id = id, Title = id, Description = id, Recommended = recommended,
            Actions = [new RegistryValueAction { Path = @"HKCU\Software\Corelight.Tests\Selections", Name = id, Value = "1" }]
        };
        var uncheckedItem = Option("unchecked", true);
        var manualItem = Option("manual", false);
        var appliedItem = Option("applied", true);
        var engine = sys.CreateEngine();
        var scanner = new Scanner(new TweakCatalog([uncheckedItem, manualItem, appliedItem], []), engine,
            new HardwareProbe(sys.Registry, NullLogger<HardwareProbe>.Instance));
        var state = new AppState(scanner);
        state.SetUserScan(await scanner.ScanAsync(new HardwareProfile()));
        var page = new ScanResultsViewModel(state, null!, new NavigationService(), null!, NullLogger<ScanResultsViewModel>.Instance);
        await page.OnNavigatedToAsync();
        var recommended = Assert.Single(page.Groups);
        recommended.Items.Single(i => i.Tweak.Id == uncheckedItem.Id).IsSelected = false;
        Assert.Single(page.ManualItems).IsSelected = true;
        page.IsManualExpanded = true;

        await engine.ApplyAsync(appliedItem);
        state.MarkSystemChanged();
        await page.OnNavigatedToAsync();

        Assert.False(Assert.Single(Assert.Single(page.Groups).Items).IsSelected);
        Assert.True(Assert.Single(page.ManualItems).IsSelected);
        Assert.True(page.IsManualExpanded);

        await engine.RevertAsync(appliedItem);
        state.MarkSystemChanged();
        await page.OnNavigatedToAsync();
        Assert.All(Assert.Single(page.Groups).Items, item => Assert.False(item.IsSelected));

        state.SetUserScan(await scanner.ScanAsync(new HardwareProfile()));
        await page.OnNavigatedToAsync();
        Assert.All(Assert.Single(page.Groups).Items, item => Assert.True(item.IsSelected));
        Assert.False(Assert.Single(page.ManualItems).IsSelected);
        Assert.False(page.IsManualExpanded);
    }

    [Fact]
    public async Task Game_mode_uses_the_windows_default_until_explicitly_disabled()
    {
        var sys = new TestSystem();
        var tweak = TweakCatalog.LoadEmbedded().Find("gaming.game-mode")!;
        var engine = sys.CreateEngine();
        Assert.Equal(TweakState.Applied, await engine.DetectAsync(tweak));
        Assert.Empty(sys.Registry.Values);

        sys.Registry.SetValue(@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled", RegValue.DWord(0));
        Assert.Equal(TweakState.NotApplied, await engine.DetectAsync(tweak));
    }

    [Fact]
    public async Task Hags_uses_the_live_driver_state_when_there_is_no_registry_override()
    {
        var sys = new TestSystem();
        var tweak = TweakCatalog.LoadEmbedded().Find("gaming.hags")!;
        var engine = sys.CreateEngine();
        Assert.Equal(TweakState.NotApplied, await engine.DetectAsync(tweak));

        sys.Components.Present.Add(WindowsComponent.HagsEnabled);
        Assert.Equal(TweakState.Applied, await engine.DetectAsync(tweak));
        Assert.Empty(sys.Registry.Values);

        sys.Registry.SetValue(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegValue.DWord(1));
        Assert.Equal(TweakState.NotApplied, await engine.DetectAsync(tweak));
    }

    [Theory]
    [InlineData(false, false, TweakState.NotApplied)]
    [InlineData(true, false, TweakState.Applied)]
    [InlineData(false, true, TweakState.Applied)]
    public async Task Advertising_id_is_disabled_by_user_setting_or_machine_policy(
        bool disabledByUser, bool disabledByPolicy, TweakState expected)
    {
        var sys = new TestSystem();
        var tweak = TweakCatalog.LoadEmbedded().Find("privacy.advertising-id")!;
        sys.Registry.SetValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
            "Enabled", RegValue.DWord(disabledByUser ? 0u : 1u));
        if (disabledByPolicy)
            sys.Registry.SetValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", RegValue.DWord(1));

        Assert.Equal(expected, await sys.CreateEngine().DetectAsync(tweak));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registry_policy_denial_is_explained_and_already_disabled_policy_is_not_written(bool alreadyDisabled)
    {
        var sys = new TestSystem();
        var action = new RegistryValueAction
        {
            Path = @"HKCU\Software\Corelight.Tests",
            Name = "TestPolicy",
            Value = "0"
        };
        sys.Registry.SetValue(action.Path, action.Name, RegValue.DWord(alreadyDisabled ? 0u : 1u));
        sys.Registry.DenyWrites = true;
        var tweak = TestSystem.Tweak(action);
        var engine = sys.CreateEngine();

        if (alreadyDisabled)
        {
            Assert.Equal(TweakState.Applied, await engine.DetectAsync(tweak));
            Assert.True((await engine.ApplyAsync(tweak)).Confirmed);
            return;
        }

        var error = await Assert.ThrowsAsync<TweakException>(() => engine.ApplyAsync(tweak));
        Assert.Contains("Windows запретила", error.Message);
        Assert.Contains(action.Name, error.Message);
        Assert.Null(sys.Backups.Get(tweak.Id));
        var detection = await engine.DetectDetailedAsync(tweak);
        Assert.Equal(TweakState.Unknown, detection.State);
        Assert.Contains("Windows запретила", detection.Error);
        sys.Registry.DenyWrites = false;
        await Assert.ThrowsAsync<TweakException>(() => engine.ApplyAsync(tweak));
        Assert.Equal("1", sys.Registry.GetValue(action.Path, action.Name)!.Data);
    }

    [Theory]
    [InlineData("interface.widgets")]
    [InlineData("apps.web-experience")]
    [InlineData("network.nagle")]
    [InlineData("services.search-indexer")]
    public void Retired_optimizations_are_absent_from_catalog(string id)
    {
        Assert.Null(TweakCatalog.LoadEmbedded().Find(id));
    }

    [Theory]
    [InlineData("apps.bing-news")]
    [InlineData("apps.office-hub")]
    [InlineData("apps.outlook-new")]
    [InlineData("apps.teams-personal")]
    [InlineData("apps.copilot-desktop")]
    [InlineData("apps.cortana")]
    [InlineData("apps.mixed-reality")]
    [InlineData("interface.background-apps")]
    [InlineData("gaming.game-dvr")]
    [InlineData("gaming.mouse-precision")]
    [InlineData("services.parental-controls")]
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
        foreach (var id in new[] { "apps.movies-tv", "gaming.hags", "privacy.error-reporting", "gaming.power-ultimate", "apps.xbox-game-bar" })
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
