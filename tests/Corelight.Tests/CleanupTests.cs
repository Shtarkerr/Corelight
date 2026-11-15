using System.Runtime.InteropServices;
using Corelight.Core.Cleanup;
using Microsoft.Extensions.Logging.Abstractions;

namespace Corelight.Tests;

public sealed class CleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Corelight-cleanup-" + Guid.NewGuid().ToString("N"));
    private readonly FakeShell _shell = new();
    private string Temp => Path.Combine(_root, "Temp");
    private string Cache => Path.Combine(_root, "Cache");
    private DiskCleanupService Service(params CleanupRule[] rules) => new(rules.Length == 0
        ? [new("user-temp", Temp, TimeSpan.FromDays(7)), new("shaders", Cache, TimeSpan.FromDays(1))] : rules, _shell, NullLogger<DiskCleanupService>.Instance);

    private string FileAt(string folder, string name, int days = 14)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "test data");
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-days));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-days));
        return path;
    }

    [Fact]
    public async Task Analysis_is_read_only_and_only_includes_old_files_in_allowed_roots()
    {
        var old = FileAt(Temp, "old.tmp"); var recent = FileAt(Temp, "recent.tmp", 0); var personal = FileAt(_root, "personal.txt");
        var scan = await Service().AnalyzeAsync();
        Assert.Equal(old, Assert.Single(scan.Files).Path);
        Assert.All(new[] { old, recent, personal }, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task Only_selected_categories_and_files_are_deleted()
    {
        var remove = FileAt(Temp, "remove.tmp"); var keep = FileAt(Temp, "keep.tmp"); var cache = FileAt(Cache, "shader.bin");
        var svc = Service(); var scan = await svc.AnalyzeAsync();
        var result = await svc.CleanAsync(scan, ["user-temp"], excludedPaths: new HashSet<string>([keep], StringComparer.OrdinalIgnoreCase));
        Assert.Equal(1, result.DeletedFiles); Assert.True(result.ReleasedBytes > 0);
        Assert.False(File.Exists(remove)); Assert.True(File.Exists(keep)); Assert.True(File.Exists(cache)); Assert.Equal(0, _shell.EmptyCalls);
    }

    [Fact]
    public async Task Locked_changed_and_new_files_are_preserved()
    {
        var locked = FileAt(Temp, "locked.tmp"); var changed = FileAt(Temp, "changed.tmp");
        var svc = Service(); var scan = await svc.AnalyzeAsync();
        var added = FileAt(Temp, "new-after-scan.tmp"); File.AppendAllText(changed, "changed");
        using var stream = File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await svc.CleanAsync(scan, ["user-temp"]);
        Assert.Equal(0, result.DeletedFiles); Assert.Equal(2, result.SkippedFiles);
        Assert.All(new[] { locked, changed, added }, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task Replacing_file_after_analysis_is_not_allowed()
    {
        var path = FileAt(Temp, "replace.tmp"); var svc = Service(); var scan = await svc.AnalyzeAsync();
        File.Move(path, Path.Combine(_root, "original.tmp")); FileAt(Temp, "replace.tmp");
        var result = await svc.CleanAsync(scan, ["user-temp"]);
        Assert.Equal(1, result.SkippedFiles); Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Analysis_can_only_be_used_once_and_new_analysis_invalidates_old_one()
    {
        FileAt(Temp, "old.tmp"); var svc = Service(); var first = await svc.AnalyzeAsync(); var second = await svc.AnalyzeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CleanAsync(first, ["user-temp"]));
        await svc.CleanAsync(second, ["user-temp"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CleanAsync(second, ["user-temp"]));
    }

    [Fact]
    public async Task Hard_links_are_not_cleaned_even_when_added_after_analysis()
    {
        var path = FileAt(Temp, "linked.tmp"); var svc = Service(); var scan = await svc.AnalyzeAsync();
        Assert.True(CreateHardLink(Path.Combine(_root, "personal.txt"), path, IntPtr.Zero));
        Assert.Empty((await svc.AnalyzeAsync()).Files);
        // Старый снимок инвалидирован, но проверяем также исходный токен удаления напрямую.
        Assert.Throws<IOException>(() => CleanupFiles.Delete(scan.Files[0], () => true));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("../personal.txt")]
    [InlineData("../TempOther/personal.txt")]
    public void Traversal_and_similar_prefixes_are_rejected(string relative)
    {
        Assert.Throws<IOException>(() => CleanupFiles.ValidatePath(Path.GetFullPath(Path.Combine(Temp, relative)), Temp));
    }

    [Fact]
    public async Task Recycle_bin_requires_explicit_selection()
    {
        var svc = Service(); Assert.False(svc.Categories.Single(c => c.Id == "recycle-bin").DefaultSelected);
        await svc.CleanAsync(await svc.AnalyzeAsync(), []); Assert.Equal(0, _shell.EmptyCalls);
        var result = await svc.CleanAsync(await svc.AnalyzeAsync(), ["recycle-bin"]);
        Assert.Equal(1, _shell.EmptyCalls); Assert.True(result.RecycleBinEmptied); Assert.Equal(1024, result.RecycleBinBytes);
    }

    [Fact]
    public async Task Recycle_bin_cannot_be_emptied_if_analysis_failed()
    {
        _shell.FailQuery = true; var svc = Service(); var scan = await svc.AnalyzeAsync();
        Assert.Null(scan.RecycleBin); Assert.NotEmpty(scan.Issues);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CleanAsync(scan, ["recycle-bin"]));
        Assert.Equal(0, _shell.EmptyCalls);
    }

    [Fact]
    public async Task Shortcut_whose_target_reappeared_is_preserved()
    {
        var shortcut = FileAt(Temp, "lost.lnk"); _shell.BrokenTarget = Path.Combine(_root, "target.exe");
        var svc = Service(new CleanupRule("shortcuts", Temp, TimeSpan.FromDays(1), CleanupFilter.BrokenShortcuts));
        var scan = await svc.AnalyzeAsync(); Assert.Single(scan.Files);
        _shell.Missing = false;
        var result = await svc.CleanAsync(scan, ["shortcuts"]);
        Assert.Equal(1, result.SkippedFiles); Assert.True(File.Exists(shortcut));
    }

    [Fact]
    public async Task Broken_shortcuts_can_be_deleted_and_non_shortcut_files_are_preserved()
    {
        var shortcut = FileAt(Temp, "lost.lnk"); var document = FileAt(Temp, "document.txt");
        _shell.BrokenTarget = Path.Combine(_root, "target.exe");
        var svc = Service(new CleanupRule("shortcuts", Temp, TimeSpan.FromDays(1), CleanupFilter.BrokenShortcuts));
        var result = await svc.CleanAsync(await svc.AnalyzeAsync(), ["shortcuts"]);
        Assert.Equal(1, result.DeletedFiles); Assert.False(File.Exists(shortcut)); Assert.True(File.Exists(document));
    }

    [Fact]
    public async Task Cancellation_between_files_returns_partial_report()
    {
        FileAt(Temp, "a.tmp"); FileAt(Temp, "b.tmp"); var svc = Service(); var scan = await svc.AnalyzeAsync();
        using var cancel = new CancellationTokenSource();
        var result = await svc.CleanAsync(scan, ["user-temp"], new CallbackProgress(() => cancel.Cancel()), cancel.Token);
        Assert.True(result.Cancelled); Assert.InRange(result.DeletedFiles, 0, 1);
        Assert.NotEmpty(Directory.GetFiles(Temp));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Directory_link_swapped_in_after_analysis_cannot_delete_external_data()
    {
        var sub = Path.Combine(Temp, "Sub"); var file = FileAt(sub, "data.tmp");
        var svc = Service(); var scan = await svc.AnalyzeAsync();
        var moved = Path.Combine(_root, "Personal"); Directory.Move(sub, moved);
        Directory.CreateSymbolicLink(sub, moved);
        try
        {
            var result = await svc.CleanAsync(scan, ["user-temp"]);
            Assert.Equal(1, result.SkippedFiles); Assert.True(File.Exists(Path.Combine(moved, "data.tmp")));
            Assert.Empty((await svc.AnalyzeAsync()).Files);
        }
        finally { Directory.Delete(sub); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Native_shortcut_checker_preserves_network_virtual_and_valid_targets()
    {
        var shell = new WindowsCleanupShell();
        Assert.False(shell.IsMissingTarget(@"\\offline-server\share\absent.exe"));
        Assert.False(shell.IsMissingTarget("shell:AppsFolder"));
        var existing = FileAt(_root, "existing.exe"); Assert.False(shell.IsMissingTarget(existing));
        Assert.True(shell.IsMissingTarget(Path.Combine(_root, "absent.exe")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Real_broken_shortcut_is_removed_while_valid_shortcut_remains()
    {
        Directory.CreateDirectory(Temp);
        var validTarget = FileAt(_root, "app.exe");
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        try
        {
            foreach (var name in new[] { "valid", "broken" })
            {
                var path = Path.Combine(Temp, name + ".lnk");
                var shortcut = ((dynamic)shell).CreateShortcut(path);
                try { shortcut.TargetPath = name == "valid" ? validTarget : Path.Combine(_root, "missing.exe"); shortcut.Save(); }
                finally { Marshal.FinalReleaseComObject(shortcut); }
                File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-14));
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-14));
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        var svc = new DiskCleanupService([new("shortcuts", Temp, TimeSpan.FromDays(1), CleanupFilter.BrokenShortcuts)], new WindowsCleanupShell(), NullLogger<DiskCleanupService>.Instance);
        var scan = await svc.AnalyzeAsync(); Assert.Single(scan.Files);
        var result = await svc.CleanAsync(scan, ["shortcuts"]);
        Assert.Equal(1, result.DeletedFiles); Assert.True(File.Exists(Path.Combine(Temp, "valid.lnk")));
        Assert.True(File.Exists(validTarget));
    }

    private sealed class FakeShell : ICleanupShell
    {
        public string? BrokenTarget { get; set; }
        public bool Missing { get; set; } = true;
        public int EmptyCalls { get; private set; }
        public bool FailQuery { get; set; }
        public string? BrokenShortcutTarget(string path) => BrokenTarget;
        public bool IsMissingTarget(string path) => Missing;
        public RecycleBinState QueryRecycleBin() => FailQuery ? throw new IOException("Bin unavailable") : EmptyCalls == 0 ? new(1024, 1) : new(0, 0);
        public void EmptyRecycleBin() => EmptyCalls++;
    }
    private sealed class CallbackProgress(Action action) : IProgress<string> { public void Report(string value) => action(); }

    public void Dispose()
    {
        var full = Path.GetFullPath(_root);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("Corelight-cleanup-")) throw new InvalidOperationException("Invalid fixture path");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string name, string existing, IntPtr security);
}
