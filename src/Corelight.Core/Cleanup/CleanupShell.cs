using System.Runtime.InteropServices;
using System.Text;

namespace Corelight.Core.Cleanup;

internal interface ICleanupShell
{
    string? BrokenShortcutTarget(string path);
    bool IsMissingTarget(string target);
    RecycleBinState QueryRecycleBin();
    IReadOnlyList<RecycleBinItem> ListRecycleBin(CancellationToken ct);
    void EmptyRecycleBin();
}

internal sealed class WindowsCleanupShell : ICleanupShell
{
    internal static string? GetDownloadsPath() => Windows.Storage.UserDataPaths.GetDefault().Downloads;

    public string? BrokenShortcutTarget(string path)
    {
        // Рекламируемые MSI-ярлыки могут не иметь обычного файла-цели; их не удаляем.
        if (MsiGetShortcutTarget(path, new StringBuilder(39), new StringBuilder(256), new StringBuilder(39)) == 0) return null;
        object? shell = null, shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            shortcut = ((dynamic)shell!).CreateShortcut(path);
            string target = Environment.ExpandEnvironmentVariables((string)((dynamic)shortcut).TargetPath);
            return IsMissingTarget(target) ? target : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        { return null; }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    public bool IsMissingTarget(string target)
    {
        if (!Path.IsPathFullyQualified(target) || target.StartsWith(@"\\", StringComparison.Ordinal) || target.Contains('%')) return false;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(target)!);
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) return false;
            _ = File.GetAttributes(target); return false;
        }
        catch (FileNotFoundException) { return HasPlainParent(target); }
        catch (DirectoryNotFoundException) { return HasPlainParent(target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static bool HasPlainParent(string target)
    {
        try
        {
            var parent = Path.GetDirectoryName(target);
            while (!string.IsNullOrEmpty(parent))
            {
                try { CleanupFiles.EnsurePlainDirectory(parent); return true; }
                catch (DirectoryNotFoundException) { parent = Path.GetDirectoryName(parent); }
                catch (FileNotFoundException) { parent = Path.GetDirectoryName(parent); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return false;
    }

    public RecycleBinState QueryRecycleBin()
    {
        var info = new RecycleInfo { Size = Marshal.SizeOf<RecycleInfo>() };
        Marshal.ThrowExceptionForHR(SHQueryRecycleBin(null, ref info));
        return new(Math.Max(0, info.Bytes), Math.Max(0, info.Items));
    }

    public IReadOnlyList<RecycleBinItem> ListRecycleBin(CancellationToken ct)
    {
        const int recycleBinFolder = 10; // ShellSpecialFolderConstants.ssfBITBUCKET
        object? shell = null, folder = null, items = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var shellType = Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!;
            shell = Activator.CreateInstance(shellType)!;
            folder = ((dynamic)shell).NameSpace(recycleBinFolder);
            if (folder is null) throw new IOException("Windows не открыла папку корзины.");
            items = ((dynamic)folder).Items();
            if (items is null) throw new IOException("Windows не вернула содержимое корзины.");
            int count = ((dynamic)items).Count;
            var result = new List<RecycleBinItem>(count);
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                object? item = ((dynamic)items).Item(i);
                if (item is null) continue;
                try
                {
                    string name = ((dynamic)item).Name;
                    bool isDirectory = ((dynamic)item).IsFolder;
                    object? size = isDirectory ? null : ((dynamic)item).ExtendedProperty("System.Size");
                    var bytes = size switch
                    {
                        ulong value when value <= long.MaxValue => (long)value,
                        long value when value >= 0 => value,
                        uint value => value,
                        int value when value >= 0 => value,
                        _ => (long?)null,
                    };
                    if (!string.IsNullOrWhiteSpace(name)) result.Add(new(name, bytes, isDirectory));
                }
                finally { ReleaseComObject(item); }
            }
            return result.AsReadOnly();
        }
        finally
        {
            ReleaseComObject(items);
            ReleaseComObject(folder);
            ReleaseComObject(shell);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    public void EmptyRecycleBin() => Marshal.ThrowExceptionForHR(SHEmptyRecycleBin(IntPtr.Zero, null, 1 | 2 | 4));

    [StructLayout(LayoutKind.Sequential)]
    private struct RecycleInfo { public int Size; public long Bytes; public long Items; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin(string? root, ref RecycleInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHEmptyRecycleBinW")]
    private static extern int SHEmptyRecycleBin(IntPtr window, string? root, uint flags);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetShortcutTargetW")]
    private static extern uint MsiGetShortcutTarget(string shortcut, StringBuilder product, StringBuilder feature, StringBuilder component);
}
