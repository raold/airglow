using System.Security.AccessControl;
using System.Security.Principal;

namespace Airglow.Service;

public static class ConfigDirectory
{
    public static void Secure(string path)
    {
        var directory = Directory.CreateDirectory(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        Allow(security, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
        Allow(security, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
        Allow(security, WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute);
        directory.SetAccessControl(security);
    }

    private static void Allow(DirectorySecurity security, WellKnownSidType sid, FileSystemRights rights) =>
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(sid, null),
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
}
