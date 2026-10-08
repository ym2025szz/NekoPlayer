using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using NekoPlayer.Core.Interfaces;

namespace NekoPlayer.Infrastructure.Online;

/// <summary>DPAPI CurrentUser storage, separate from settings, exports and gateway scratch directories.</summary>
public sealed class WindowsAccountVault(IUserDataPaths paths)
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    public string FilePath => Path.Combine(paths.ConfigDirectory, "accounts.dpapi");

    public async Task<Dictionary<string, string>> ReadAsync(CancellationToken token = default)
    {
        var gate = Gates.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try { return ReadCore(); }
        finally { gate.Release(); }
    }

    public async Task SetAsync(string providerId, string? credential, CancellationToken token = default)
    {
        var gate = Gates.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            var entries = ReadCore();
            if (credential is null) entries.Remove(providerId); else entries[providerId] = credential;
            Directory.CreateDirectory(paths.ConfigDirectory);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(entries);
            byte[] encrypted;
            try { encrypted = Transform(plaintext, protect: true); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, encrypted, token);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, FilePath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }

    private Dictionary<string, string> ReadCore()
    {
        if (!File.Exists(FilePath)) return new(StringComparer.Ordinal);
        if (new FileInfo(FilePath).Length > 131072) throw new InvalidDataException("账号凭证文件超过大小限制");
        var plaintext = Transform(File.ReadAllBytes(FilePath), protect: false);
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext) ?? []; }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("账号凭证保存需要 Windows DPAPI");
        var input = new DataBlob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var output = default(DataBlob);
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var success = protect ? CryptProtectData(ref input, "NekoPlayer accounts", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取或保存本机账号凭证");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size);
                LocalFree(output.Data);
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
