using Microsoft.Extensions.Logging.Abstractions;
using Corelight.Core.Actions;
using Corelight.Core.Catalog;
using Corelight.Core.Platform;
using Corelight.Core.Scanning;
using Corelight.Core.Tweaks;

namespace Corelight.Tests;

public sealed class OneDriveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Corelight-OneDrive-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRegistry _registry = new();
    private readonly InstallerRunner _runner = new();
    private readonly OneDriveLocations _locations;
    private readonly WindowsOneDriveControl _control;

    public OneDriveTests()
    {
        _locations = new(Path.Combine(_root, "user-client"), Path.Combine(_root, "machine-client"),
            Path.Combine(_root, "machine-x86-client"), Path.Combine(_root, "Windows"), Path.Combine(_root, "Corelight"), Path.Combine(_root, "Profile"));
        _control = new(_registry, _runner, NullLogger<WindowsOneDriveControl>.Instance, _locations);
        WriteFile(Path.Combine(_locations.WindowsRoot, "System32", "OneDriveSetup.exe"));
    }

    [Fact]
    public void Bundled_setup_alone_is_not_an_installed_client()
        => Assert.Equal(OneDriveScope.None, _control.GetInstalledScope());

    [Fact]
    public async Task Catalogue_suggests_installed_OneDrive_but_requires_manual_selection()
    {
        var sys = new TestSystem();
        var tweak = TweakCatalog.LoadEmbedded().Find("apps.onedrive")!;
        Assert.IsType<OneDriveAction>(Assert.Single(tweak.Actions));
        sys.OneDrive.Scope = OneDriveScope.CurrentUser;
        var status = new TweakStatus(tweak, await sys.CreateEngine().DetectAsync(tweak), null, tweak.Recommended);
        Assert.True(status.IsSuggestion);
        Assert.False(status.IsPreselected);
        Assert.Equal(TweakRisk.Caution, tweak.Risk);
        sys.OneDrive.Scope = OneDriveScope.None;
        Assert.False(new TweakStatus(tweak, await sys.CreateEngine().DetectAsync(tweak), null, tweak.Recommended).IsSuggestion);
    }

    [Theory]
    [InlineData(OneDriveScope.CurrentUser)]
    [InlineData(OneDriveScope.AllUsers)]
    public async Task Removal_caches_setup_and_revert_restores_original_scope_without_touching_documents(OneDriveScope scope)
    {
        var executable = ClientExecutable(scope);
        WriteFile(executable);
        var document = Path.Combine(_locations.ProfileRoot, "OneDrive", "important.txt");
        WriteFile(document, "User document must remain unchanged");
        var sys = new TestSystem();
        var platform = new PlatformServices(_registry, sys.Services, sys.Tasks, sys.Apps, sys.Power, _control);
        var engine = new TweakEngine(platform, sys.Backups, NullLogger<TweakEngine>.Instance);
        var tweak = TestSystem.Tweak(new OneDriveAction());
        _runner.Installer = (path, args) =>
        {
            Assert.Equal(_control.CachedInstaller(scope), path);
            Assert.True(File.Exists(path));
            Assert.Equal(scope == OneDriveScope.AllUsers, args.Contains("/allusers"));
            if (args.Contains("/uninstall")) File.Delete(executable);
            else WriteFile(executable);
            return new ProcessResult(0, "", "");
        };

        await engine.ApplyAsync(tweak);
        Assert.False(File.Exists(executable));
        Assert.NotNull(sys.Backups.Get(tweak.Id));
        File.Delete(Path.Combine(_locations.WindowsRoot, "System32", "OneDriveSetup.exe"));
        await engine.RevertAsync(tweak);

        Assert.Equal(scope, _control.GetInstalledScope());
        Assert.Null(sys.Backups.Get(tweak.Id));
        Assert.Equal("User document must remain unchanged", File.ReadAllText(document));
        Assert.Equal(2, _runner.NativeCalls);
    }

    [Theory]
    [InlineData("Desktop")]
    [InlineData("Personal")]
    [InlineData("My Pictures")]
    public async Task Redirected_important_folder_blocks_removal_before_any_installer_runs(string name)
    {
        WriteFile(ClientExecutable(OneDriveScope.CurrentUser));
        var cloud = Path.Combine(_root, "CustomCloud");
        _registry.SetValue(@"HKCU\Software\Microsoft\OneDrive\Accounts\Business1", "UserFolder", RegValue.String(cloud));
        _registry.SetValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", name, RegValue.String(Path.Combine(cloud, "SavedFolder")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _control.UninstallAsync());

        Assert.Contains("резервное копирование", error.Message);
        Assert.Equal(0, _runner.NativeCalls);
        Assert.Equal(0, _runner.SignatureChecks);
        Assert.True(File.Exists(ClientExecutable(OneDriveScope.CurrentUser)));
    }

    [Fact]
    public async Task Similar_folder_prefix_does_not_block_unrelated_local_documents()
    {
        var executable = ClientExecutable(OneDriveScope.CurrentUser);
        WriteFile(executable);
        _registry.SetValue(@"HKCU\Software\Microsoft\OneDrive\Accounts\Personal", "UserFolder", RegValue.String(Path.Combine(_root, "Cloud")));
        _registry.SetValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", "Personal", RegValue.String(Path.Combine(_root, "CloudArchive", "Docs")));
        _runner.Installer = (_, _) => { File.Delete(executable); return new ProcessResult(0, "", ""); };

        await _control.UninstallAsync();

        Assert.Equal(1, _runner.NativeCalls);
    }

    [Fact]
    public async Task Missing_setup_leaves_client_installed()
    {
        WriteFile(ClientExecutable(OneDriveScope.CurrentUser));
        File.Delete(Path.Combine(_locations.WindowsRoot, "System32", "OneDriveSetup.exe"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _control.UninstallAsync());

        Assert.Equal(0, _runner.NativeCalls);
        Assert.Equal(OneDriveScope.CurrentUser, _control.GetInstalledScope());
    }

    [Fact]
    public async Task Untrusted_setup_is_not_executed()
    {
        WriteFile(ClientExecutable(OneDriveScope.CurrentUser));
        _runner.SignatureExitCode = 1;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _control.UninstallAsync());

        Assert.Contains("подписи Microsoft", error.Message);
        Assert.Equal(0, _runner.NativeCalls);
        Assert.Equal(OneDriveScope.CurrentUser, _control.GetInstalledScope());
    }

    [Fact]
    public async Task Mixed_installation_scopes_are_not_uninstalled_ambiguously()
    {
        WriteFile(ClientExecutable(OneDriveScope.CurrentUser));
        WriteFile(ClientExecutable(OneDriveScope.AllUsers));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _control.UninstallAsync());

        Assert.Equal(0, _runner.NativeCalls);
        Assert.Equal(0, _runner.SignatureChecks);
    }

    [Fact]
    public async Task Failed_reinstall_preserves_backup_for_retry()
    {
        var scope = OneDriveScope.CurrentUser;
        var executable = ClientExecutable(scope);
        WriteFile(executable);
        var sys = new TestSystem();
        var engine = new TweakEngine(new PlatformServices(_registry, sys.Services, sys.Tasks, sys.Apps, sys.Power, _control), sys.Backups, NullLogger<TweakEngine>.Instance);
        var tweak = TestSystem.Tweak(new OneDriveAction());
        _runner.Installer = (_, args) =>
        {
            if (args.Contains("/uninstall")) { File.Delete(executable); return new ProcessResult(0, "", ""); }
            return new ProcessResult(1, "Download unavailable", "");
        };
        await engine.ApplyAsync(tweak);

        await Assert.ThrowsAsync<TweakException>(() => engine.RevertAsync(tweak));

        Assert.NotNull(sys.Backups.Get(tweak.Id));
        Assert.True(File.Exists(_control.CachedInstaller(scope)));
    }

    private string ClientExecutable(OneDriveScope scope) => Path.Combine(scope == OneDriveScope.CurrentUser ? _locations.UserRoot : _locations.MachineRoot, "OneDrive.exe");

    private static void WriteFile(string path, string contents = "Fake Microsoft installer used only by a fake runner")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(_root);
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("Corelight-OneDrive-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to delete outside the test directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }

    private sealed class InstallerRunner : IProcessRunner
    {
        public int SignatureExitCode { get; set; }
        public int SignatureChecks { get; private set; }
        public int NativeCalls { get; private set; }
        public Func<string, IReadOnlyList<string>, ProcessResult>? Installer { get; set; }
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default)
        {
            if (Path.GetFileName(fileName).Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
            {
                SignatureChecks++;
                Assert.Contains(@"O=Microsoft Corporation", arguments.Last());
                return Task.FromResult(new ProcessResult(SignatureExitCode, "", ""));
            }
            NativeCalls++;
            return Task.FromResult(Installer?.Invoke(fileName, arguments) ?? new ProcessResult(0, "", ""));
        }
    }
}
