using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Airglow.Service;

public static class PipeAcl
{
    public static PipeSecurity Create()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        Allow(security, WellKnownSidType.LocalSystemSid, PipeAccessRights.FullControl);
        Allow(security, WellKnownSidType.BuiltinAdministratorsSid, PipeAccessRights.FullControl);
        Allow(security, WellKnownSidType.AuthenticatedUserSid, PipeAccessRights.ReadWrite);
        return security;
    }

    private static void Allow(PipeSecurity security, WellKnownSidType sid, PipeAccessRights rights) =>
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid, null), rights, AccessControlType.Allow));
}
