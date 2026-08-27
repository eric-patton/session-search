using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SessionSearch.Infrastructure.Windows;

[SupportedOSPlatform("windows")]
public sealed class AuthenticodeExecutableTrustVerifier : IExecutableTrustVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00aac56b-cd44-11d0-8cc2-00c04fc295ee");

    public ExecutableTrustVerification Verify(
        string canonicalPath,
        TrustedExecutableProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        ArgumentNullException.ThrowIfNull(profile);

        try
        {
            using var stream = new FileStream(
                canonicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            int trustStatus = VerifyEmbeddedSignature(canonicalPath);
            if (trustStatus != 0)
            {
                return new ExecutableTrustVerification(
                    false,
                    null,
                    null,
                    Failure: $"WinVerifyTrust returned 0x{trustStatus:X8}.");
            }

            (string publisher, string signerSubject) = ReadSignerIdentity(canonicalPath);
            stream.Position = 0;
            string identity = Convert.ToHexString(SHA256.HashData(stream));
            return new ExecutableTrustVerification(
                true,
                publisher,
                identity,
                IsVerifiedPackageAlias: IsVerifiedTerminalPackageBinary(canonicalPath, profile),
                SignerSubject: signerSubject);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                CryptographicException or
                ArgumentException)
        {
            return new ExecutableTrustVerification(
                false,
                null,
                null,
                Failure: exception.GetType().Name);
        }
    }

    private static int VerifyEmbeddedSignature(string path)
    {
        var fileInfo = new WinTrustFileInfo(path);
        IntPtr fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var trustData = new WinTrustData(fileInfoPointer);
            Guid action = GenericVerifyV2;
            return NativeMethods.WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeCoTaskMem(fileInfoPointer);
        }
    }

    private static (string Publisher, string SignerSubject) ReadSignerIdentity(string path)
    {
#pragma warning disable SYSLIB0057
        using X509Certificate embeddedCertificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
        using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(
            embeddedCertificate.GetRawCertData());
        return (
            certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
            certificate.Subject);
    }

    private static bool IsVerifiedTerminalPackageBinary(
        string path,
        TrustedExecutableProfile profile)
    {
        if (profile.Kind != TrustedExecutableKind.WindowsTerminal ||
            !string.Equals(Path.GetFileName(path), "wt.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.Contains(
                @"\WindowsApps\Microsoft.WindowsTerminal_",
                StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(
                @"\WindowsApps\Microsoft.WindowsTerminalPreview_",
                StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo
    {
        public WinTrustFileInfo(string path)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = path;
        }

        public uint StructSize;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string FilePath;

        public IntPtr FileHandle;

        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        private const uint UiNone = 2;
        private const uint RevocationChecksNone = 0;
        private const uint UnionChoiceFile = 1;
        private const uint StateActionIgnore = 0;
        private const uint ProviderFlagSafer = 0x00000100;
        private const uint ProviderFlagCacheOnlyUrlRetrieval = 0x00001000;
        private const uint UiContextExecute = 0;

        public WinTrustData(IntPtr fileInfo)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = UiNone;
            RevocationChecks = RevocationChecksNone;
            UnionChoice = UnionChoiceFile;
            FileInfo = fileInfo;
            StateAction = StateActionIgnore;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = ProviderFlagSafer | ProviderFlagCacheOnlyUrlRetrieval;
            UiContext = UiContextExecute;
            SignatureSettings = IntPtr.Zero;
        }

        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    private static class NativeMethods
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
        internal static extern int WinVerifyTrust(
            IntPtr windowHandle,
            ref Guid actionId,
            ref WinTrustData trustData);
    }
}
