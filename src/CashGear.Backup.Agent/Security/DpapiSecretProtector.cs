using System.Security.Cryptography;
using System.Text;
using CashGear.Backup.Core;

namespace CashGear.Backup.Agent.Security;

/// <summary>Turns repository passwords into blobs safe to store in state.db, and back.</summary>
internal interface ISecretProtector
{
    byte[] Protect(SecureSecret secret);

    SecureSecret Unprotect(byte[] blob);
}

/// <summary>
/// DPAPI with machine scope (plan, "Secrets") plus an application-specific entropy, so a blob only opens on
/// this machine and only through this code path. state.db itself is readable by SYSTEM and Administrators
/// only (installer ACL), which is what keeps other local users out.
/// </summary>
internal sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] s_entropy = "CashGear.Backup/repository-password/v1"u8.ToArray();

    public byte[] Protect(SecureSecret secret)
    {
        var plain = Encoding.UTF8.GetBytes(secret.Reveal());
        try
        {
            return ProtectedData.Protect(plain, s_entropy, DataProtectionScope.LocalMachine);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public SecureSecret Unprotect(byte[] blob)
    {
        var plain = ProtectedData.Unprotect(blob, s_entropy, DataProtectionScope.LocalMachine);
        var chars = Encoding.UTF8.GetChars(plain);
        try
        {
            return new SecureSecret(chars);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            Array.Clear(chars);
        }
    }
}
