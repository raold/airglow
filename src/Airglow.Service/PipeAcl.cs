using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;

namespace Airglow.Service;

public static class PipeAcl
{
    public static void Configure(NamedPipeTransportOptions options)
    {
        options.CurrentUserOnly = false;
        options.PipeSecurity = Create();
    }

    public static PipeSecurity Create()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        Allow(security, WellKnownSidType.LocalSystemSid, PipeAccessRights.FullControl);
        Allow(security, WellKnownSidType.BuiltinAdministratorsSid, PipeAccessRights.FullControl);
        Allow(security, WellKnownSidType.AuthenticatedUserSid, PipeAccessRights.ReadWrite);
        if (WindowsIdentity.GetCurrent().User is { } self)
            security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static void Allow(PipeSecurity security, WellKnownSidType sid, PipeAccessRights rights) =>
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid, null), rights, AccessControlType.Allow));
}
