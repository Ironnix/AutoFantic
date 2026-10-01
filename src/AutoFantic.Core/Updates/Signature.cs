using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using static AutoFantic.Core.Texts;

namespace AutoFantic.Core.Updates;

/// <summary>
/// Who signed a file (Authenticode), after Windows checked that the signature is intact and
/// trusted. Used by the updater: once AuFantic is signed, an update must be signed by the same
/// publisher, so a swapped or unsigned exe is never installed.
/// </summary>
public static class Signature
{
    /// <summary>The signer's name (the certificate's subject) if the file carries a valid signature; null if it's unsigned, changed or not trusted.</summary>
    public static string? Of(string path)
    {
        if (!File.Exists(path) || !Verify(path))
            return null;
        try
        {
#pragma warning disable SYSLIB0057 // the signer of a signed file; X509CertificateLoader has no replacement for this
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return certificate.Subject;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Throws if <paramref name="current"/> is signed and <paramref name="next"/> isn't signed by the
    /// same publisher. An unsigned AuFantic (a build from source, the releases before signing)
    /// accepts any update that passed the checksum.
    /// </summary>
    public static void CheckSamePublisher(string current, string next)
    {
        if (Of(current) is not { } publisher)
            return;
        if (Of(next) is not { } other)
            throw new InvalidDataException(T("The download isn't signed, but this AuFantic is. Nothing was changed."));
        if (other != publisher)
            throw new InvalidDataException(T($"The download is signed by someone else ({other}). Nothing was changed."));
    }

    // WinVerifyTrust: is the signature intact and from a trusted certificate? No window, no online revocation check.
    private static bool Verify(string path)
    {
        var file = new FileInfoData { Size = (uint)Marshal.SizeOf<FileInfoData>(), FilePath = path };
        IntPtr filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfoData>());
        try
        {
            Marshal.StructureToPtr(file, filePointer, false);
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(),
                UiChoice = 2,          // WTD_UI_NONE
                RevocationChecks = 0,  // WTD_REVOKE_NONE
                UnionChoice = 1,       // WTD_CHOICE_FILE
                File = filePointer,
                ProviderFlags = 0x1000, // WTD_CACHE_ONLY_URL_RETRIEVAL: never goes online
            };
            return WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<FileInfoData>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfoData
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref TrustData data);
}
