using System.Runtime.InteropServices;
using Corelight.App.Services;
using Corelight.App.ViewModels;
using Corelight.Core.Cleanup;
using Microsoft.Extensions.Logging.Abstractions;

namespace Corelight.Tests;

public sealed class CleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Corelight-cleanup-" + Guid.NewGuid().ToString("N"));
    private string? _downloadsRoot;
    private string? _downloadsPath;
    private readonly FakeShell _shell = new();
    private string Temp => Path.Combine(_root, "Temp");
    private string Cache => Path.Combine(_root, "Cache");
    private string Desktop => Path.Combine(_root, "Desktop");
    private string Documents => Path.Combine(_root, "Documents");
    private string Downloads => Path.Combine(_downloadsRoot ??= Path.Combine(AppContext.BaseDirectory, "Corelight-cleanup-" + Guid.NewGuid().ToString("N")), "Downloads");
    private DiskCleanupService Service(params CleanupRule[] rules) => new(rules.Length == 0
        ? [new("user-temp", Temp, TimeSpan.FromDays(7)), new("shaders", Cache, TimeSpan.FromDays(1))] : rules, _shell, NullLogger<DiskCleanupService>.Instance, () => _downloadsPath);

    private string FileAt(string folder, string name, int days = 14)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "test data");
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-days));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-days));
        return path;
    }

    private string DesktopFolder(string name, int days = 14)
    {
        var path = Directory.CreateDirectory(Path.Combine(Desktop, name)).FullName;
        var date = DateTime.UtcNow.AddDays(-days);
        Directory.SetCreationTimeUtc(path, date);
        Directory.SetLastWriteTimeUtc(path, date);
        return path;
    }

    private DiskCleanupService DirectoryService(Func<IReadOnlyList<CleanupDirectoryScope>>? getScopes = null) => new(
        [new("shortcuts", Desktop, TimeSpan.FromDays(1), CleanupFilter.BrokenShortcuts)],
        _shell, NullLogger<DiskCleanupService>.Instance, getDirectoryScopes: getScopes ?? (() => CleanupCatalog.DirectoryScopes(_root, Desktop, Documents, [])));

    [Fact]
    public async Task Analysis_is_read_only_and_only_includes_old_files_in_allowed_roots()
    {
        var old = FileAt(Temp, "old.tmp"); var recent = FileAt(Temp, "recent.tmp", 0); var personal = FileAt(_root, "personal.txt");
        var scan = await Service().AnalyzeAsync();
        Assert.Equal(old, Assert.Single(scan.Items).Path);
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
    public async Task Downloads_are_opt_in_and_only_reviewed_files_are_deleted()
    {
        _downloadsPath = Downloads;
        var nested = Path.Combine(Downloads, "nested");
        var download = FileAt(nested, "recent.zip", 0);
        var keep = FileAt(Downloads, "keep.txt");
        var outside = FileAt(_downloadsRoot!, "outside.txt");
        var svc = Service();

        var scan = await svc.AnalyzeAsync();
        Assert.False(svc.Categories.Single(c => c.Id == "downloads").DefaultSelected);
        Assert.Equal(2, scan.Items.Count);
        Assert.All(scan.Items, file => Assert.Equal("downloads", file.CategoryId));
        await svc.CleanAsync(scan, svc.Categories.Where(c => c.DefaultSelected).Select(c => c.Id).ToArray());
        Assert.True(File.Exists(download));

        scan = await svc.AnalyzeAsync();
        var added = FileAt(Downloads, "after-analysis.zip");
        var result = await svc.CleanAsync(scan, ["downloads"], excludedPaths: new HashSet<string>([keep]));

        Assert.Equal(1, result.DeletedFiles);
        Assert.False(File.Exists(download));
        Assert.True(Directory.Exists(nested));
        Assert.All(new[] { keep, outside, added }, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task Downloads_moved_after_analysis_are_left_untouched()
    {
        _downloadsPath = Downloads;
        var original = FileAt(Downloads, "original.zip");
        var redirected = FileAt(Path.Combine(_downloadsRoot!, "Other"), "new-location.zip");
        var svc = Service();
        var scan = await svc.AnalyzeAsync();
        Assert.Equal(original, Assert.Single(scan.Items).Path);
        _downloadsPath = Path.GetDirectoryName(redirected);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CleanAsync(scan, ["downloads"]));

        Assert.True(File.Exists(original));
        Assert.True(File.Exists(redirected));
    }

    [Fact]
    public async Task Downloads_redirected_to_a_drive_root_are_skipped()
    {
        _downloadsPath = Path.GetPathRoot(Temp);
        var regular = FileAt(Temp, "regular.tmp");
        var scan = await Service().AnalyzeAsync();

        Assert.Equal(regular, Assert.Single(scan.Items).Path);
        Assert.Contains(scan.Issues, issue => issue.Path == "Загрузки");
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
        Assert.Empty((await svc.AnalyzeAsync()).Items);
        // Старый снимок инвалидирован, но проверяем также исходный токен удаления напрямую.
        Assert.Throws<IOException>(() => CleanupFiles.Delete(scan.Items[0], () => true));
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
        var scan = await svc.AnalyzeAsync(); Assert.Single(scan.Items);
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
    public async Task Analysis_offers_new_empty_folders_on_desktop_and_in_documents_outside_protected_paths()
    {
        var empty = DesktopFolder("Empty");
        var recent = Directory.CreateDirectory(Path.Combine(Desktop, "Recent")).FullName;
        var documentFolder = Directory.CreateDirectory(Path.Combine(Documents, "Recent")).FullName;
        var appFolder = Directory.CreateDirectory(Path.Combine(Documents, "My Games")).FullName;
        var hidden = DesktopFolder("Hidden");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        var system = DesktopFolder("System");
        File.SetAttributes(system, File.GetAttributes(system) | FileAttributes.System);
        var redirected = DesktopFolder("AppData");
        var protectedParent = DesktopFolder("Personal");
        var nested = DesktopFolder(Path.Combine("Parent", "Empty"));
        var config = FileAt(DesktopFolder("Configured"), "desktop.ini");
        File.SetAttributes(config, FileAttributes.Hidden | FileAttributes.System);
        var startMenu = Directory.CreateDirectory(Path.Combine(_root, "Start Menu", "Empty")).FullName;
        var svc = DirectoryService(() => CleanupCatalog.DirectoryScopes(_root, Desktop, Documents, [redirected, Path.Combine(protectedParent, "Documents")]));

        var scan = await svc.AnalyzeAsync();

        Assert.Equal(new[] { empty, recent, documentFolder }.Order(), scan.Items.Select(item => item.Path).Order());
        Assert.All(scan.Items, item => { Assert.Equal("shortcuts", item.CategoryId); Assert.True(item.IsDirectory); Assert.Equal(0, item.Bytes); });
        Assert.All(new[] { empty, recent, documentFolder, appFolder, hidden, system, redirected, protectedParent, nested, startMenu },
            path => Assert.True(Directory.Exists(path)));
        Assert.True(File.Exists(config));
        Assert.False(svc.Categories.Single(category => category.Id == "shortcuts").DefaultSelected);
        var driveRoot = Path.GetPathRoot(_root)!;
        Assert.Empty(CleanupCatalog.DirectoryScopes(_root, startMenu, driveRoot, []));
        Assert.Empty(CleanupCatalog.DirectoryScopes(_root, Desktop, Documents, [driveRoot]));
        Assert.False(Assert.Single(CleanupCatalog.DirectoryScopes(_root, Desktop, recent, [])).Includes(recent));
    }

    [Fact]
    public async Task Cleanup_deletes_only_individually_selected_empty_folders_and_counts_them_separately()
    {
        var keep = DesktopFolder("Keep");
        var remove = DesktopFolder("Remove");
        var service = DirectoryService();

        var scan = await service.AnalyzeAsync();
        Assert.Equal(2, scan.Items.Count);
        var categoryOnly = await service.CleanAsync(scan, ["shortcuts"]);
        Assert.Equal(0, categoryOnly.DeletedDirectories);
        Assert.True(Directory.Exists(remove));

        scan = await service.AnalyzeAsync();
        var result = await service.CleanAsync(scan, ["shortcuts"], selectedDirectories: new HashSet<string>([remove]));
        Assert.Equal(1, result.DeletedDirectories);
        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(0, result.DeletedBytes);
        Assert.False(Directory.Exists(remove));
        Assert.True(Directory.Exists(keep));
        Assert.True(Directory.Exists(Desktop));
    }

    [Fact]
    public async Task Cleanup_page_requires_selection_of_both_folder_and_category()
    {
        var path = DesktopFolder("Empty");
        var viewModel = new CleanupViewModel(DirectoryService(), new AppState(null!), null!, NullLogger<CleanupViewModel>.Instance);
        var category = viewModel.Categories.Single(item => item.Category.Id == "shortcuts");
        viewModel.SelectedCategory = category;

        await viewModel.AnalyzeCommand.ExecuteAsync(null);

        Assert.Equal("Неработающие ярлыки и пустые папки", category.Title);
        var folder = Assert.Single(viewModel.VisibleItems);
        Assert.Equal(path, folder.Path);
        Assert.False(folder.IsSelected);
        category.IsSelected = true;
        Assert.False(viewModel.CanClean);
        folder.IsSelected = true;
        Assert.True(viewModel.CanClean);
        Assert.Contains("папок 1", viewModel.SelectionSummary);
        category.IsSelected = false;
        Assert.False(viewModel.CanClean);
    }

    [Fact]
    public async Task Cleanup_preserves_folders_that_are_replaced_filled_or_newly_protected()
    {
        var replaced = DesktopFolder("Replaced");
        var changed = DesktopFolder("Changed");
        var protectedPath = DesktopFolder("Protected");
        IReadOnlyList<string> protectedPaths = [];
        var service = DirectoryService(() => CleanupCatalog.DirectoryScopes(_root, Desktop, Documents, protectedPaths));
        var scan = await service.AnalyzeAsync();
        Assert.Equal(3, scan.Items.Count);

        var original = Path.Combine(Desktop, "Original");
        Directory.Move(replaced, original);
        Directory.CreateDirectory(replaced);
        Directory.SetCreationTimeUtc(replaced, Directory.GetCreationTimeUtc(original));
        Directory.SetLastWriteTimeUtc(replaced, Directory.GetLastWriteTimeUtc(original));
        var modified = Directory.GetLastWriteTimeUtc(changed);
        var added = FileAt(changed, "Added.txt");
        Directory.SetLastWriteTimeUtc(changed, modified);
        protectedPaths = [protectedPath];

        var result = await service.CleanAsync(scan, ["shortcuts"], selectedDirectories: new HashSet<string>([replaced, changed, protectedPath]));

        Assert.Equal(0, result.DeletedDirectories);
        Assert.Equal(3, result.SkippedDirectories);
        Assert.All(new[] { original, replaced, changed, protectedPath }, path => Assert.True(Directory.Exists(path)));
        Assert.True(File.Exists(added));
    }

    [Fact]
    public async Task Directories_with_alternate_data_streams_are_preserved()
    {
        var existing = DesktopFolder("WithStream");
        var changed = DesktopFolder("Changed");
        File.WriteAllText(existing + ":metadata", "keep");
        Directory.SetLastWriteTimeUtc(existing, DateTime.UtcNow.AddDays(-14));
        var service = DirectoryService();
        var scan = await service.AnalyzeAsync();
        Assert.Equal(changed, Assert.Single(scan.Items).Path);

        var modified = Directory.GetLastWriteTimeUtc(changed);
        File.WriteAllText(changed + ":metadata", "keep");
        Directory.SetLastWriteTimeUtc(changed, modified);
        var result = await service.CleanAsync(scan, ["shortcuts"], selectedDirectories: new HashSet<string>([changed]));

        Assert.Equal(1, result.SkippedDirectories);
        Assert.Equal("keep", File.ReadAllText(changed + ":metadata"));
        Assert.Equal("keep", File.ReadAllText(existing + ":metadata"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Directory_link_is_excluded_from_analysis_and_cannot_replace_a_selected_folder()
    {
        var candidate = DesktopFolder("Candidate");
        var target = Directory.CreateDirectory(Path.Combine(_root, "External")).FullName;
        var link = Path.Combine(Desktop, "Link");
        Directory.CreateSymbolicLink(link, target);
        try
        {
            var service = DirectoryService();
            var scan = await service.AnalyzeAsync();
            Assert.Equal(candidate, Assert.Single(scan.Items).Path);
            Directory.Move(candidate, Path.Combine(_root, "Original"));
            Directory.CreateSymbolicLink(candidate, target);
            try
            {
                var result = await service.CleanAsync(scan, ["shortcuts"], selectedDirectories: new HashSet<string>([candidate]));
                Assert.Equal(1, result.SkippedDirectories);
                Assert.True(Directory.Exists(candidate));
                Assert.True(Directory.Exists(target));
            }
            finally { Directory.Delete(candidate); }
        }
        finally { Directory.Delete(link); }
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
            Assert.Empty((await svc.AnalyzeAsync()).Items);
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
        var scan = await svc.AnalyzeAsync(); Assert.Single(scan.Items);
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
        DeleteFixture(_root, Path.GetTempPath());
        if (_downloadsRoot is not null) DeleteFixture(_downloadsRoot, AppContext.BaseDirectory);
    }

    private static void DeleteFixture(string root, string parent)
    {
        var full = Path.GetFullPath(root);
        if (!CleanupFiles.IsWithin(full, parent) || !Path.GetFileName(full).StartsWith("Corelight-cleanup-")) throw new InvalidOperationException("Invalid fixture path");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string name, string existing, IntPtr security);
}
