using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Corelight.Core.Cleanup;

/// <summary>Проверяет пути и удаляет открытые файлы и пустые папки по дескриптору.</summary>
internal static class CleanupFiles
{
    private const uint ReadAttributes = 0x80, DeleteAccess = 0x10000, OpenExisting = 3;
    private const uint OpenReparsePoint = 0x200000, BackupSemantics = 0x2000000;
    private const FileAttributes UnsafeAttributes = FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.System | FileAttributes.ReadOnly | (FileAttributes)0x400000;
    private const FileAttributes UnsafeDirectoryAttributes = UnsafeAttributes | FileAttributes.Hidden | FileAttributes.Encrypted | (FileAttributes)0x40000;

    internal static bool IsWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var prefix = Path.GetFullPath(root);
        if (!Path.EndsInDirectorySeparator(prefix)) prefix += Path.DirectorySeparatorChar;
        return fullPath.Length > prefix.Length && fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static void ValidatePath(string path, string root)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || !IsWithin(path, root)
            || path[Path.GetPathRoot(path)!.Length..].Contains(':'))
            throw new IOException("Путь вне разрешённой папки.");
        EnsurePlainDirectory(root);
    }

    internal static void EnsurePlainDirectory(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Папка-ссылка пропущена.");
            current = current.Parent;
        }
    }

    internal static FileStamp Inspect(string path, string root)
    {
        ValidatePath(path, root);
        using var handle = Open(path, ReadAttributes, 1 | 2 | 4, false);
        VerifyFinalPath(handle, path);
        return Stamp(handle);
    }

    internal static FileStamp? InspectEmptyDirectory(string path, string root)
    {
        ValidatePath(path, root);
        if (!IsOrdinaryDirectory(File.GetAttributes(path))) return null;
        using var handle = Open(path, ReadAttributes, 1 | 2, true);
        VerifyFinalPath(handle, path);
        var stamp = Stamp(handle, directory: true);
        if (Directory.EnumerateFileSystemEntries(path).Any() || HasDataStreams(handle)) return null;
        return stamp;
    }

    internal static long Delete(CleanupItem item, Func<bool> stillEligible)
    {
        ValidatePath(item.Path, item.Rule.Root);
        var parents = new List<SafeFileHandle>();
        try
        {
            // Запрет переименования папок на время удаления защищает от подмены пути между проверкой и действием.
            var root = item.IsDirectory ? Path.GetPathRoot(item.Path)! : Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Rule.Root));
            var directory = Path.GetDirectoryName(item.Path)!;
            var paths = new Stack<string>();
            for (var current = directory; ; current = Path.GetDirectoryName(current)!)
            {
                paths.Push(current);
                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
                if (!IsWithin(current, root)) throw new IOException("Родитель вне разрешённой папки.");
            }
            foreach (var path in paths)
            {
                var parent = Open(path, ReadAttributes, 1 | 2, true);
                parents.Add(parent);
                VerifyFinalPath(parent, path);
                if (!GetFileInformationByHandle(parent, out var info)) ThrowLastError();
                if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0) throw new IOException("Папка-ссылка пропущена.");
            }
            using var handle = Open(item.Path, ReadAttributes | DeleteAccess, 0, item.IsDirectory);
            VerifyFinalPath(handle, item.Path);
            var currentStamp = Stamp(handle, item.IsDirectory);
            if (currentStamp != item.Stamp) throw new IOException("Элемент изменился после анализа; нужен новый анализ.");
            if (!stillEligible() || item.IsDirectory && HasDataStreams(handle))
                throw new IOException("Элемент больше не подходит для очистки.");
            var disposition = new Disposition { Delete = true };
            // Windows проверяет пустоту папки одновременно с установкой признака удаления.
            if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<Disposition>())) ThrowLastError();
            return currentStamp.Allocated;
        }
        finally { foreach (var parent in parents) parent.Dispose(); }
    }

    private static SafeFileHandle Open(string path, uint access, uint share, bool directory)
    {
        var handle = CreateFile(path, access, share, IntPtr.Zero, OpenExisting, OpenReparsePoint | (directory ? BackupSemantics : 0), IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose();
        throw new IOException(new Win32Exception(error).Message, new Win32Exception(error));
    }

    private static bool IsOrdinaryDirectory(FileAttributes attributes) =>
        (attributes & (UnsafeDirectoryAttributes | FileAttributes.Directory)) == FileAttributes.Directory;

    private static bool HasDataStreams(SafeFileHandle handle)
    {
        var buffer = new byte[1024];
        if (GetFileStreamInformation(handle, 7, buffer, (uint)buffer.Length)) return true;
        var error = Marshal.GetLastWin32Error();
        const int endOfFile = 38;
        if (error == endOfFile) return false;
        throw new IOException("Не удалось проверить потоки данных папки.", new Win32Exception(error));
    }

    private static FileStamp Stamp(SafeFileHandle handle, bool directory = false)
    {
        if (!GetFileInformationByHandle(handle, out var info)) ThrowLastError();
        var attributes = (FileAttributes)info.Attributes;
        if (info.Links != 1 || (directory ? !IsOrdinaryDirectory(attributes) : (attributes & (UnsafeAttributes | FileAttributes.Directory)) != 0))
            throw new IOException("Ссылка, защищённый или облачный элемент пропущен.");
        if (!GetFileInformationByHandleEx(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>())) ThrowLastError();
        return new(info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow,
            ((long)info.SizeHigh << 32) | info.SizeLow, Time(info.Creation), Time(info.Modified), directory ? 0 : standard.Allocation);
    }

    private static long Time(System.Runtime.InteropServices.ComTypes.FILETIME time) => ((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;

    private static void VerifyFinalPath(SafeFileHandle handle, string expected)
    {
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new IOException("Не удалось проверить конечный путь файла.");
        var actual = buffer.ToString();
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!string.Equals(Path.TrimEndingDirectorySeparator(actual), Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Конечный путь изменён или проходит через ссылку.");
    }

    private static void ThrowLastError() => throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Accessed, Modified;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo { public long Allocation, End; public uint Links; public byte Pending, Directory; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Disposition { [MarshalAs(UnmanagedType.U1)] public bool Delete; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfo info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out StandardInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileStreamInformation(SafeFileHandle file, int infoClass, [Out] byte[] buffer, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref Disposition info, uint size);
}
