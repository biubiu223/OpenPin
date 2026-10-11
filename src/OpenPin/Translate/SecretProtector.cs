using System.Runtime.InteropServices;
using System.Text;
using OpenPin.Core;

namespace OpenPin.Translate;

/// <summary>
/// Per-user encryption of API keys with DPAPI (CryptProtectData / CryptUnprotectData, crypt32 — no package needed).
/// Protected values look like <c>dpapi:BASE64</c>. Values without the prefix are treated as legacy plain text.
/// Never log the plain or protected value.
/// </summary>
internal static class SecretProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OpenPin.Translate.v1");

    /// <summary>Encrypt for the current Windows user. Empty input → empty output.</summary>
    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = Encoding.UTF8.GetBytes(plain);
        var output = Crypt(data, protect: true);
        return Prefix + Convert.ToBase64String(output);
    }

    /// <summary>Decrypt a value produced by <see cref="Protect"/>. Returns "" when it cannot be decrypted (other user / machine).</summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored; // legacy / hand-edited plain value
        try
        {
            var data = Convert.FromBase64String(stored[Prefix.Length..]);
            return Encoding.UTF8.GetString(Crypt(data, protect: false));
        }
        catch (Exception ex)
        {
            Log.Warn($"A stored translation key could not be decrypted ({ex.GetType().Name}); it must be entered again");
            return "";
        }
    }

    public static bool IsProtected(string? stored) => stored != null && stored.StartsWith(Prefix, StringComparison.Ordinal);

    private static byte[] Crypt(byte[] input, bool protect)
    {
        var inBlob = new DATA_BLOB();
        var entropyBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            inBlob.cbData = input.Length;
            inBlob.pbData = inHandle.AddrOfPinnedObject();
            entropyBlob.cbData = Entropy.Length;
            entropyBlob.pbData = entropyHandle.AddrOfPinnedObject();
            bool ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            inHandle.Free();
            entropyHandle.Free();
            if (outBlob.pbData != IntPtr.Zero)
            {
                // Wipe the plain text copy before freeing it.
                if (!protect) for (int i = 0; i < outBlob.cbData; i++) Marshal.WriteByte(outBlob.pbData, i, 0);
                LocalFree(outBlob.pbData);
            }
        }
    }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
