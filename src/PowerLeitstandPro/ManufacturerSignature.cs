using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace PowerControlProWpf;

internal static class ManufacturerSignature
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfo
    {
        internal uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] internal string Path;
        internal IntPtr Handle, Subject;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        internal uint Size; internal IntPtr Callback, Client; internal uint Ui, Revocation, Choice;
        internal IntPtr File; internal uint Action; internal IntPtr State, Url;
        internal uint Flags, Context; internal IntPtr Signature;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptQueryObject(uint type, [MarshalAs(UnmanagedType.LPWStr)] string file,
        uint contents, uint formats, uint flags, out uint encoding, out uint content, out uint format,
        out IntPtr store, out IntPtr message, out IntPtr context);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptMsgGetParam(IntPtr message, uint type, uint index, IntPtr data, ref uint size);
    [DllImport("crypt32.dll")]
    private static extern IntPtr CertFindCertificateInStore(IntPtr store, uint encoding, uint flags, uint findType, IntPtr parameter, IntPtr previous);
    [DllImport("crypt32.dll")] private static extern bool CertFreeCertificateContext(IntPtr certificate);
    [DllImport("crypt32.dll")] private static extern bool CertCloseStore(IntPtr store, uint flags);
    [DllImport("crypt32.dll")] private static extern bool CryptMsgClose(IntPtr message);
    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext
    {
        internal uint Encoding; internal IntPtr Encoded; internal uint Size; internal IntPtr Info, Store;
    }

    private static X509Certificate2? ReadSigner(string file)
    {
        if (!CryptQueryObject(1, file, 1 << 10, 2, 0, out var encoding, out _, out _, out var store, out var message, out _)) return null;
        IntPtr info = IntPtr.Zero, certificate = IntPtr.Zero;
        try
        {
            uint size = 0;
            if (!CryptMsgGetParam(message, 7, 0, IntPtr.Zero, ref size) || size > 1024 * 1024) return null;
            info = Marshal.AllocHGlobal((int)size);
            if (!CryptMsgGetParam(message, 7, 0, info, ref size)) return null;
            certificate = CertFindCertificateInStore(store, encoding, 0, 0x000B0000, info, IntPtr.Zero);
            if (certificate == IntPtr.Zero) return null;
            var context = Marshal.PtrToStructure<CertificateContext>(certificate);
            if (context.Size > 1024 * 1024) return null;
            var bytes = new byte[(int)context.Size]; Marshal.Copy(context.Encoded, bytes, 0, bytes.Length);
            return X509CertificateLoader.LoadCertificate(bytes);
        }
        finally
        {
            if (certificate != IntPtr.Zero) CertFreeCertificateContext(certificate);
            if (info != IntPtr.Zero) Marshal.FreeHGlobal(info);
            if (message != IntPtr.Zero) CryptMsgClose(message);
            if (store != IntPtr.Zero) CertCloseStore(store, 0);
        }
    }

    internal static bool Verify(string file, string publisher) => Verify(file, publisher, null);
    internal static bool Verify(string file, string publisher, IReadOnlyCollection<string>? signerNames)
    {
        var info = new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), Path = file };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Choice = 1, File = pointer, Action = 1, Flags = 0x1000 };
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) return false;
            using var certificate = ReadSigner(file);
            if (certificate is null) return false;
            var organizations = certificate.SubjectName.Decode(X500DistinguishedNameFlags.UseNewLines | X500DistinguishedNameFlags.DoNotUseQuotes).Split('\n').Select(x => x.Trim());
            return organizations.Any(x => x.Equals("O=" + publisher, StringComparison.OrdinalIgnoreCase))
                && (signerNames is null || signerNames.Contains(certificate.GetNameInfo(X509NameType.SimpleName, false), StringComparer.Ordinal));
        }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
        finally
        {
            data.Action = 2; WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<FileInfo>(pointer); Marshal.FreeHGlobal(pointer);
        }
    }
}
