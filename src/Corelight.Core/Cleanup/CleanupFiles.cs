using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Corelight.Core.Cleanup;

/// <summary>Проверяет пути и удаляет открытый файл по дескриптору, а не повторно по имени.</summary>
internal static class CleanupFiles
{
    private const uint ReadAttributes = 0x80, DeleteAccess = 0x10000, OpenExisting = 3;
    private const uint OpenReparsePoint = 0x200000, BackupSemantics = 0x2000000;
    private const FileAttributes UnsafeAttributes = FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.System | FileAttributes.ReadOnly | (FileAttributes)0x400000;

    internal static bool IsWithin(string path, string root) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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

    internal static long Delete(CleanupFile file, Func<bool> stillEligible)
    {
        ValidatePath(file.Path, file.Rule.Root);
        var parents = new List<SafeFileHandle>();
        try
        {
            // Запрет переименования папок на время удаления защищает от подмены пути между проверкой и действием.
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(file.Rule.Root));
            var directory = Path.GetDirectoryName(file.Path)!;
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
            // Ни совместного чтения, ни записи: используемые файлы не трогаем.
            using var handle = Open(file.Path, ReadAttributes | DeleteAccess, 0, false);
            VerifyFinalPath(handle, file.Path);
            var currentStamp = Stamp(handle);
            if (currentStamp != file.Stamp) throw new IOException("Файл изменился после анализа; нужен новый анализ.");
            if (!stillEligible()) throw new IOException("Файл больше не подходит для очистки.");
            var disposition = new Disposition { Delete = true };
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

    private static FileStamp Stamp(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) ThrowLastError();
        if (((FileAttributes)info.Attributes & (UnsafeAttributes | FileAttributes.Directory)) != 0 || info.Links != 1)
            throw new IOException("Ссылка, защищённый или облачный файл пропущен.");
        if (!GetFileInformationByHandleEx(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>())) ThrowLastError();
        return new(info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow,
            ((long)info.SizeHigh << 32) | info.SizeLow, Time(info.Creation), Time(info.Modified), standard.Allocation);
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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref Disposition info, uint size);
}
