using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LocalBridge.WindowsShared;

internal static class Dpapi
{
    private const int CryptprotectUiForbidden = 0x1;

    public static byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);
    public static byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (input.Length == 0) return Array.Empty<byte>();
        var inputPointer = Marshal.AllocHGlobal(input.Length);
        try
        {
            Marshal.Copy(input, 0, inputPointer, input.Length);
            var inputBlob = new DataBlob { Length = input.Length, Data = inputPointer };
            DataBlob outputBlob;
            var succeeded = protect
                ? CryptProtectData(ref inputBlob, "LocalBridge device identity", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out outputBlob);
            if (!succeeded) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var output = new byte[outputBlob.Length];
                Marshal.Copy(outputBlob.Data, output, 0, output.Length);
                return output;
            }
            finally
            {
                for (var index = 0; index < outputBlob.Length; index++) Marshal.WriteByte(outputBlob.Data, index, 0);
                LocalFree(outputBlob.Data);
            }
        }
        finally
        {
            for (var index = 0; index < input.Length; index++) Marshal.WriteByte(inputPointer, index, 0);
            Marshal.FreeHGlobal(inputPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
