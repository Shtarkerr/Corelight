using System.Security.AccessControl;
using System.Security.Principal;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Corelight.Core;

/// <summary>Проверяет хранилище, которому доверяет экземпляр Corelight с повышенными правами.</summary>
public static class ProtectedDataDirectory
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private const FileSystemRights WriteRights = FileSystemRights.Write | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership |
        (FileSystemRights)unchecked((int)0x50000000); // GENERIC_ALL | GENERIC_WRITE.

    public static void Initialize()
    {
        EnsureDirectory(AppPaths.DataDirectory, migrateLegacyPermissions: true);
        EnsureDirectory(AppPaths.LogDirectory, migrateLegacyPermissions: true);
        ValidateFileIfPresent(AppPaths.BackupFile);
        foreach (var path in Directory.EnumerateFiles(AppPaths.LogDirectory, "log-*.txt"))
            ValidateFileIfPresent(path);
    }

    internal static void EnsureDirectory(string path, bool migrateLegacyPermissions = false)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists)
        {
            dir.Create(NewDirectorySecurity());
        }

        RejectReparsePoint(dir);
        var current = dir.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (migrateLegacyPermissions && HasLegacyPermissions(current))
            MigrateLegacyPermissions(dir.FullName);
        ValidateSecurity(dir.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), dir.FullName);
    }

    internal static void ValidateFileIfPresent(string path)
    {
        var file = new FileInfo(path);
        try { RejectReparsePoint(file); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        ValidateSecurity(file.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), file.FullName);
    }

    private static DirectorySecurity NewDirectorySecurity(bool preserveUserRead = false)
    {
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(SystemAccount, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        if (preserveUserRead)
            security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static bool HasLegacyPermissions(FileSystemSecurity security)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsTrusted(owner))
            return false;
        var acl = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl;
        if (acl is null || acl.Count != 5 || acl.Cast<GenericAce>().Any(rule => rule.AceType != AceType.AccessAllowed))
            return false;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        bool Matches(SecurityIdentifier sid, FileSystemRights rights, InheritanceFlags inheritance, PropagationFlags propagation = PropagationFlags.None) =>
            rules.Any(rule => rule.IdentityReference.Equals(sid) && rule.FileSystemRights == rights &&
                rule.InheritanceFlags == inheritance && rule.PropagationFlags == propagation);

        // Штатный DACL ProgramData, унаследованный или скопированный целиком.
        return rules.Length == 5 && rules.All(rule => rule.AccessControlType == AccessControlType.Allow) &&
            (rules.All(rule => rule.IsInherited) || rules.All(rule => !rule.IsInherited)) &&
            Matches(Administrators, FileSystemRights.FullControl, inherit) &&
            Matches(SystemAccount, FileSystemRights.FullControl, inherit) &&
            Matches(CreatorOwner, (FileSystemRights)0x10000000, inherit, PropagationFlags.InheritOnly) &&
            Matches(Users, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, inherit) &&
            Matches(Users, FileSystemRights.Write, InheritanceFlags.ContainerInherit);
    }

    private static unsafe void MigrateLegacyPermissions(string path)
    {
        const uint readAttributes = 0x80, readControl = 0x20000, writeDac = 0x40000, openExisting = 3;
        const uint openReparsePoint = 0x200000, backupSemantics = 0x2000000;
        const int fileObject = 1, fileAttributeTagInfo = 9;
        const uint ownerInformation = 1, daclInformation = 4, protectedDacl = 0x80000000;
        using var handle = CreateFile(path, readAttributes | readControl | writeDac, 1 | 2,
            IntPtr.Zero, openExisting, openReparsePoint | backupSemantics, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, fileAttributeTagInfo, out var info, 8))
            throw new IOException($"Не удалось открыть каталог для проверки прав: {path}", new Win32Exception(Marshal.GetLastWin32Error()));
        if (((FileAttributes)info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new IOException($"Путь к каталогу данных изменился: {path}");

        var error = GetSecurityInfo(handle, fileObject, ownerInformation | daclInformation,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var descriptor);
        if (error != 0) throw new IOException($"Не удалось прочитать права: {path}", new Win32Exception((int)error));
        try
        {
            var bytes = new byte[GetSecurityDescriptorLength(descriptor)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var current = new DirectorySecurity();
            current.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Owner | AccessControlSections.Access);
            if (!HasLegacyPermissions(current))
            {
                ValidateSecurity(current, path);
                return;
            }

            var updated = new RawSecurityDescriptor(NewDirectorySecurity(preserveUserRead: true).GetSecurityDescriptorBinaryForm(), 0);
            var acl = new byte[updated.DiscretionaryAcl!.BinaryLength];
            updated.DiscretionaryAcl.GetBinaryForm(acl, 0);
            fixed (byte* dacl = acl)
                error = SetSecurityInfo(handle, fileObject, daclInformation | protectedDacl,
                    IntPtr.Zero, IntPtr.Zero, (IntPtr)dacl, IntPtr.Zero);
            if (error != 0) throw new IOException($"Не удалось защитить каталог данных: {path}", new Win32Exception((int)error));
        }
        finally { LocalFree(descriptor); }
    }

    private static void RejectReparsePoint(FileSystemInfo info)
    {
        if ((File.GetAttributes(info.FullName) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Хранилище Corelight содержит перенаправление: {info.FullName}");
    }

    internal static void ValidateSecurity(FileSystemSecurity security, string? path = null)
    {
        var context = path is null ? "" : $"\n{path}";
        var acl = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl;
        if (acl is null)
            throw new UnauthorizedAccessException("Список прав доступа к хранилищу Corelight отсутствует." + context);
        if (acl.Cast<GenericAce>().Any(rule => rule.AceType is not (AceType.AccessAllowed or AceType.AccessDenied)))
            throw new UnauthorizedAccessException("Хранилище Corelight использует нестандартные правила доступа." + context);
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsTrusted(owner))
            throw new UnauthorizedAccessException("Владельцем каталога данных или файла должны быть администраторы или Windows." + context);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow &&
                (rule.FileSystemRights & WriteRights) != 0 &&
                !IsTrusted((SecurityIdentifier)rule.IdentityReference) &&
                !(rule.IdentityReference.Equals(CreatorOwner) && (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0))
                throw new UnauthorizedAccessException("Каталог данных Corelight или его файл доступен для записи обычным пользователям." + context);
        }
    }

    private static bool IsTrusted(SecurityIdentifier sid) => sid.Equals(Administrators) || sid.Equals(SystemAccount);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo { public uint Attributes, ReparseTag; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileAttributeTagInfo info, uint size);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint securityInformation, IntPtr owner,
        IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(SafeFileHandle handle, int objectType, uint securityInformation, IntPtr owner,
        IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("advapi32.dll")]
    private static extern int GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
