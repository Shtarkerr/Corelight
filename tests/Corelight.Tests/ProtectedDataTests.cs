using System.Security.AccessControl;
using System.Security.Principal;
using Corelight.Core;

namespace Corelight.Tests;

public sealed class ProtectedDataTests
{
    [Theory]
    [InlineData(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.Read, true)]
    [InlineData(WellKnownSidType.LocalSystemSid, FileSystemRights.ReadAndExecute, true)]
    [InlineData(WellKnownSidType.BuiltinUsersSid, FileSystemRights.Read, false)]
    [InlineData(WellKnownSidType.LocalSystemSid, FileSystemRights.CreateFiles, false)]
    [InlineData(WellKnownSidType.LocalSystemSid, FileSystemRights.ChangePermissions, false)]
    public void Only_trusted_owners_and_writers_are_accepted(WellKnownSidType owner, FileSystemRights userRights, bool allowed)
    {
        var acl = new DirectorySecurity();
        acl.SetOwner(new SecurityIdentifier(owner, null));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            userRights, AccessControlType.Allow));

        if (allowed) ProtectedDataDirectory.ValidateSecurity(acl);
        else Assert.Throws<UnauthorizedAccessException>(() => ProtectedDataDirectory.ValidateSecurity(acl));
    }
}
