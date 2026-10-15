using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CashGear.Backup.Agent.Updates;

/// <summary>Checks a downloaded update before it is installed.</summary>
internal interface IPackageVerifier
{
    /// <summary>
    /// Null when <paramref name="path"/> carries a valid Authenticode signature (chain and revocation checked by
    /// Windows) whose signing certificate's SHA-256 thumbprint is one of <paramref name="publisherThumbprints"/>;
    /// otherwise why not.
    /// </summary>
    string? Problem(string path, IReadOnlyCollection<string> publisherThumbprints);
}

/// <summary><see cref="IPackageVerifier"/> with WinVerifyTrust; the signer is read from the verified signature itself.</summary>
internal sealed class AuthenticodeVerifier : IPackageVerifier
{
    private const uint UiNone = 2;
    private const uint RevokeWholeChain = 1;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint RevocationCheckChainExcludeRoot = 0x80;

    private static readonly Guid s_genericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public string? Problem(string path, IReadOnlyCollection<string> publisherThumbprints)
    {
        if (publisherThumbprints.Count == 0)
        {
            return "No update publisher is configured.";
        }

        var filePath = Marshal.StringToHGlobalUni(Path.GetFullPath(path));
        var fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = filePath }, fileInfo, false);
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = UiNone,
                RevocationChecks = RevokeWholeChain,
                UnionChoice = ChoiceFile,
                FileInfo = fileInfo,
                StateAction = StateActionVerify,
                ProvFlags = RevocationCheckChainExcludeRoot,
            };
            var action = s_genericVerifyV2;
            var result = WinVerifyTrust(-1, ref action, ref data);
            try
            {
                if (result != 0)
                {
                    return Describe(result);
                }

                using var signer = Signer(data.StateData);
                if (signer is null)
                {
                    return "The update's signer could not be read.";
                }

                var thumbprint = signer.GetCertHashString(HashAlgorithmName.SHA256);
                return publisherThumbprints.Any(p => string.Equals(p.Replace(" ", "", StringComparison.Ordinal), thumbprint, StringComparison.OrdinalIgnoreCase))
                    ? null
                    : $"The update is signed by \"{signer.GetNameInfo(X509NameType.SimpleName, false)}\", which is not the CashGear publisher.";
            }
            finally
            {
                data.StateAction = StateActionClose;
                WinVerifyTrust(-1, ref action, ref data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfo);
            Marshal.FreeHGlobal(filePath);
        }
    }

    /// <summary>The leaf certificate of the first signer, from WinVerifyTrust's own state.</summary>
    private static X509Certificate2? Signer(nint stateData)
    {
        var provider = WTHelperProvDataFromStateData(stateData);
        var signer = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if (signer == 0)
        {
            return null;
        }

        // CRYPT_PROVIDER_SGNR: cbStruct, sftVerifyAsOf (FILETIME), csCertChain, then pasCertChain at offset 16.
        // CRYPT_PROVIDER_CERT: cbStruct, then pCert (pointer aligned).
        var chain = Marshal.ReadIntPtr(signer, 16);
        if (chain == 0 || Marshal.ReadInt32(signer, 12) == 0)
        {
            return null;
        }

        var context = Marshal.ReadIntPtr(chain, IntPtr.Size);
        return context == 0 ? null : new X509Certificate2(context);
    }

    private static string Describe(int result) => unchecked((uint)result) switch
    {
        0x800B0100 or 0x800B0003 => "The update is not signed (or is not an installer at all).",
        0x80096010 or 0x800B0004 => "The update's signature does not match its contents.",
        0x800B010C => "The certificate that signed the update has been revoked.",
        0x80092013 or 0x80092012 => "Windows could not check whether the update's certificate was revoked; tried again later.",
        0x800B0109 or 0x800B010A => "The update's certificate is not trusted by this PC.",
        0x800B0101 => "The update's certificate has expired.",
        _ => $"Windows did not accept the update's signature (0x{result:X8}).",
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public nint FilePath;
        public nint File;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint FileInfo;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [DllImport("wintrust.dll")]
    private static extern int WinVerifyTrust(nint window, ref Guid action, ref WinTrustData data);

    [DllImport("wintrust.dll")]
    private static extern nint WTHelperProvDataFromStateData(nint stateData);

    [DllImport("wintrust.dll")]
    private static extern nint WTHelperGetProvSignerFromChain(nint providerData, uint signerIndex, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);
}
