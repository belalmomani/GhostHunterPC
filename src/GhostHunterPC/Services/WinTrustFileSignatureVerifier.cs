using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace GhostHunterPC.Services;

public sealed class WinTrustFileSignatureVerifier : IFileSignatureVerifier
{
    private const uint UiChoiceNone = 2;
    private const uint UnionChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint CacheOnlyUrlRetrieval = 0x1000;
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public FileSignatureResult Verify(string filePath)
    {
        IntPtr path = IntPtr.Zero;
        IntPtr fileInfoPointer = IntPtr.Zero;
        var trustData = new WinTrustData();

        try
        {
            path = Marshal.StringToCoTaskMemUni(filePath);
            var fileInfo = new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = path
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

            trustData = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = UiChoiceNone,
                UnionChoice = UnionChoiceFile,
                FileInfo = fileInfoPointer,
                StateAction = StateActionVerify,
                ProviderFlags = CacheOnlyUrlRetrieval
            };

            var result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref trustData);
            if (result == 0)
            {
                string? publisher = null;
                string? detail = null;
                try
                {
                    publisher = ReadPublisher(filePath);
                }
                catch (Exception exception) when (exception is IOException
                    or System.Security.SecurityException
                    or System.Security.Cryptography.CryptographicException)
                {
                    detail = exception.Message;
                }

                return new FileSignatureResult(
                    FileSignatureStatus.Signed,
                    publisher,
                    detail);
            }

            return result == TrustENoSignature
                ? new FileSignatureResult(FileSignatureStatus.Unsigned, null, null)
                : new FileSignatureResult(
                    FileSignatureStatus.VerificationUnavailable,
                    null,
                    $"WinVerifyTrust HRESULT: 0x{result:X8}");
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or MarshalDirectiveException
            or System.Security.SecurityException
            or System.Security.Cryptography.CryptographicException
            or IOException)
        {
            return new FileSignatureResult(
                FileSignatureStatus.VerificationUnavailable,
                null,
                exception.Message);
        }
        finally
        {
            if (trustData.StateData != IntPtr.Zero)
            {
                trustData.StateAction = StateActionClose;
                _ = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref trustData);
            }

            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(fileInfoPointer);
            }

            if (path != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(path);
            }

        }
    }

    private static string? ReadPublisher(string filePath)
    {
#pragma warning disable SYSLIB0057
        using var certificate = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
        using var certificate2 = new X509Certificate2(certificate);
        return certificate2.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr windowHandle, [In] Guid actionId, ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint Size;
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
}
