using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Riji.Windows;

public static class ScreenCapture
{
    // Capture the complete virtual desktop in physical pixels, including negative monitor coordinates.
    public static void SavePng(string destination)
    {
        var oldContext = SetThreadDpiAwarenessContext(-4);
        try
        {
            var x = GetSystemMetrics(76); var y = GetSystemMetrics(77);
            var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
            if (width <= 0 || height <= 0 || (long)width * height > 100_000_000) throw new IOException("显示器范围无效或超过可处理大小。");
            using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            var temporary = destination + ".tmp";
            bitmap.Save(temporary, ImageFormat.Png);
            File.Move(temporary, destination);
        }
        finally { if (oldContext != 0) SetThreadDpiAwarenessContext(oldContext); }
    }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}

public static class SecretVault
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public nint Data; }
    public static string Protect(string value) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public static string Unprotect(string value) => Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false));

    // DPAPI binds credentials to the current Windows account; no plaintext key is persisted.
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            Blob output;
            var ok = protect ? CryptProtectData(ref input, "Riji", 0, 0, 0, 1, out output) : CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output);
            if (!ok) throw new CryptographicException("无法使用当前 Windows 账户保护或读取 API Key。");
            try { var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); CryptographicOperations.ZeroMemory(bytes); }
    }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string description, nint entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint pointer);
}
