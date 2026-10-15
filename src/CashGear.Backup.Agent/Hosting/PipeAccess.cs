using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CashGear.Backup.Agent.Hosting;

/// <summary>Who may open the agent's pipe.</summary>
internal static class PipeAccess
{
    /// <summary>
    /// SYSTEM, Administrators and the agent's own account get full control (only they can serve the pipe).
    /// Interactive users may read and write but not create pipe instances, so no user can stand up a rogue
    /// agent on the same name. Remote (network), anonymous and service logons are not let in at all.
    /// </summary>
    public static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[]
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            WindowsIdentity.GetCurrent().User!,
        }.Distinct())
        {
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));
        return security;
    }
}
