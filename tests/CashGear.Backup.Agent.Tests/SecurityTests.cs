using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using CashGear.Backup.Agent.Hosting;
using CashGear.Backup.Agent.Security;
using CashGear.Backup.Core;

namespace CashGear.Backup.Agent.Tests;

public class SecretProtectorTests
{
    private readonly DpapiSecretProtector _protector = new();

    [Fact]
    public void Round_trips_and_never_stores_the_plaintext()
    {
        const string password = "correct horse battery staple ✓";
        using var secret = new SecureSecret(password);
        var blob = _protector.Protect(secret);

        foreach (var encoded in new[] { Encoding.UTF8.GetBytes(password), Encoding.Unicode.GetBytes(password), Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(password))) })
        {
            Assert.True(blob.AsSpan().IndexOf(encoded) < 0, "the protected blob contains the password");
        }

        using var back = _protector.Unprotect(blob);
        Assert.Equal(password, back.Reveal());
    }

    [Fact]
    public void Each_protection_is_different_and_a_tampered_blob_is_rejected()
    {
        using var secret = new SecureSecret("same password");
        var first = _protector.Protect(secret);
        Assert.NotEqual(first, _protector.Protect(secret));

        first[^5] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => _protector.Unprotect(first));
    }
}

public class PipeAccessTests
{
    [Fact]
    public void The_pipe_admits_system_administrators_the_agent_account_and_interactive_users_only()
    {
        var rules = PipeAccess.CreateSecurity().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();

        Assert.All(rules, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
        Assert.All(rules, r => Assert.False(r.IsInherited));
        var bySid = rules.ToDictionary(r => (SecurityIdentifier)r.IdentityReference, r => r.PipeAccessRights);

        Assert.Equal(PipeAccessRights.FullControl, bySid[new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)]);
        Assert.Equal(PipeAccessRights.FullControl, bySid[new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)]);
        Assert.Equal(PipeAccessRights.FullControl, bySid[WindowsIdentity.GetCurrent().User!]);

        var interactive = bySid[new SecurityIdentifier(WellKnownSidType.InteractiveSid, null)];
        Assert.True(interactive.HasFlag(PipeAccessRights.ReadWrite));
        Assert.False(interactive.HasFlag(PipeAccessRights.CreateNewInstance), "interactive users must not be able to serve the pipe");
        Assert.False(interactive.HasFlag(PipeAccessRights.ChangePermissions));

        foreach (var outsider in new[] { WellKnownSidType.WorldSid, WellKnownSidType.NetworkSid, WellKnownSidType.AnonymousSid, WellKnownSidType.BuiltinUsersSid })
        {
            Assert.DoesNotContain(new SecurityIdentifier(outsider, null), bySid.Keys);
        }
    }
}
