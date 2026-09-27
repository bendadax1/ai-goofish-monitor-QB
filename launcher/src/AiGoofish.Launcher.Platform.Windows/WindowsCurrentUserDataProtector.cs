using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AiGoofish.Launcher.Platform.Windows;

internal sealed class WindowsCurrentUserDataProtector
{
    private const uint CryptprotectUiForbidden = 0x1;
    private const int MaximumBlobBytes = 64 * 1024;

    public byte[] Protect(byte[] plaintext, byte[] entropy)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(entropy);
        return Transform(plaintext, entropy, protect: true);
    }

    public byte[] Unprotect(byte[] ciphertext, byte[] entropy)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(entropy);
        return Transform(ciphertext, entropy, protect: false);
    }

    private static byte[] Transform(byte[] input, byte[] entropy, bool protect)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("实例基础凭据保护只在 Windows 上可用。");
        }

        if (input.Length == 0 || input.Length > MaximumBlobBytes || entropy.Length == 0 || entropy.Length > MaximumBlobBytes)
        {
            throw new InstanceSecretsFormatException("DPAPI 输入大小无效或超过 64 KiB 上限。");
        }

        var inputBlob = default(DataBlob);
        var entropyBlob = default(DataBlob);
        var outputBlob = default(DataBlob);
        try
        {
            inputBlob = Allocate(input);
            entropyBlob = Allocate(entropy);
            var succeeded = protect
                ? CryptProtectData(
                    ref inputBlob,
                    description: null,
                    ref entropyBlob,
                    reserved: IntPtr.Zero,
                    prompt: IntPtr.Zero,
                    flags: CryptprotectUiForbidden,
                    out outputBlob)
                : CryptUnprotectData(
                    ref inputBlob,
                    description: IntPtr.Zero,
                    ref entropyBlob,
                    reserved: IntPtr.Zero,
                    prompt: IntPtr.Zero,
                    flags: CryptprotectUiForbidden,
                    out outputBlob);
            if (!succeeded)
            {
                throw new InstanceSecretsProtectionException(
                    protect ? "加密" : "解密",
                    Marshal.GetLastWin32Error());
            }

            if (outputBlob.Data == IntPtr.Zero || outputBlob.Length <= 0 || outputBlob.Length > MaximumBlobBytes)
            {
                throw new InstanceSecretsFormatException("DPAPI 输出为空或超过 64 KiB 上限。");
            }

            var output = new byte[outputBlob.Length];
            Marshal.Copy(outputBlob.Data, output, startIndex: 0, outputBlob.Length);
            return output;
        }
        finally
        {
            FreeHGlobal(ref inputBlob, zeroBeforeFree: true);
            FreeHGlobal(ref entropyBlob, zeroBeforeFree: false);
            FreeLocal(ref outputBlob, zeroBeforeFree: !protect);
        }
    }

    private static DataBlob Allocate(byte[] value)
    {
        if (value.Length == 0)
        {
            throw new ArgumentException("DPAPI 输入不能为空。", nameof(value));
        }

        var pointer = Marshal.AllocHGlobal(value.Length);
        try
        {
            Marshal.Copy(value, startIndex: 0, pointer, value.Length);
            return new DataBlob(value.Length, pointer);
        }
        catch
        {
            Marshal.FreeHGlobal(pointer);
            throw;
        }
    }

    private static void FreeHGlobal(ref DataBlob blob, bool zeroBeforeFree)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        if (zeroBeforeFree && blob.Length > 0)
        {
            Marshal.Copy(new byte[blob.Length], startIndex: 0, blob.Data, blob.Length);
        }

        Marshal.FreeHGlobal(blob.Data);
        blob = default;
    }

    private static void FreeLocal(ref DataBlob blob, bool zeroBeforeFree)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        if (zeroBeforeFree && blob.Length > 0)
        {
            Marshal.Copy(new byte[blob.Length], startIndex: 0, blob.Data, blob.Length);
        }

        _ = LocalFree(blob.Data);
        blob = default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public DataBlob(int length, IntPtr data)
        {
            Length = length;
            Data = data;
        }

        public int Length;
        public IntPtr Data;
    }

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("Crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("Kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
