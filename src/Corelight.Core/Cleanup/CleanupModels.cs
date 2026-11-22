namespace Corelight.Core.Cleanup;

public sealed record CleanupCategory(string Id, string Title, string Description, bool DefaultSelected);

internal enum CleanupFilter { All, Thumbnails, Dumps, BrokenShortcuts }
internal sealed record CleanupRule(string CategoryId, string Root, TimeSpan MinimumAge, CleanupFilter Filter = CleanupFilter.All);

internal sealed record CleanupDirectoryScope(string Root, IReadOnlyList<string> ProtectedPaths)
{
    internal bool Includes(string path) =>
        string.Equals(Path.GetDirectoryName(path), Root, StringComparison.OrdinalIgnoreCase)
        && !ProtectedPaths.Any(protectedPath => CleanupCatalog.IsSameOrWithin(protectedPath, path));
}

public sealed record CleanupItem(string CategoryId, string Path, long Bytes)
{
    internal FileStamp Stamp { get; init; } = null!;
    internal CleanupRule Rule { get; init; } = null!;
    internal string? ShortcutTarget { get; init; }
    public bool IsDirectory { get; internal init; }
}

public sealed record CleanupIssue(string Path, string Reason);
public sealed record RecycleBinState(long Bytes, long Items);

public sealed class CleanupScan
{
    internal CleanupScan(IReadOnlyList<CleanupItem> items, IReadOnlyList<CleanupIssue> issues, RecycleBinState? recycleBin, DateTime time)
    { Items = items; Issues = issues; RecycleBin = recycleBin; CreatedUtc = time; }
    public IReadOnlyList<CleanupItem> Items { get; }
    public IReadOnlyList<CleanupIssue> Issues { get; }
    public RecycleBinState? RecycleBin { get; }
    public DateTime CreatedUtc { get; }
}

public sealed record CleanupResult(int DeletedFiles, long DeletedBytes, long ReleasedBytes,
    int SkippedFiles, IReadOnlyList<CleanupIssue> Issues, bool Cancelled, bool RecycleBinEmptied, long RecycleBinBytes)
{
    public int DeletedDirectories { get; internal init; }
    public int SkippedDirectories { get; internal init; }
}

internal sealed record FileStamp(uint Volume, ulong Id, long Length, long Creation, long Modified, long Allocated);

public static class CleanupSize
{
    public static string Format(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double size = Math.Max(0, bytes); var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.##} {units[unit]}";
    }
}
