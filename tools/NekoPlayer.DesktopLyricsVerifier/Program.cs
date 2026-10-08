using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using NekoPlayer.App.Views;
using NekoPlayer.Core.Models;

// Two processes own overlapping fixture windows. No input injection, existing-window manipulation,
// foreground activation, or application profile is involved in this verifier.
internal static class Program
{
    private static int _result;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--underlay") return Native.Underlay(int.Parse(args[1]), int.Parse(args[2]));
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/desktop-lyrics-verifier");
        Directory.CreateDirectory(output);
        AppBuilder.Configure<FixtureApplication>().UsePlatformDetect().SetupWithoutStarting();
        using var completion = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try { await VerifyAsync(output); }
            catch (Exception e) { Console.Error.WriteLine(e); _result = 1; }
            finally { completion.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(completion.Token);
        return _result;
    }

    private static async Task VerifyAsync(string output)
    {
        var foreground = Native.GetForegroundWindow();
        var window = new DesktopLyricsWindow();
        var area = window.Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var x = area.X + 8;
        var y = area.Y + 8;
        var startup = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            startup.ArgumentList.Add(typeof(Program).Assembly.Location);
        startup.ArgumentList.Add("--underlay"); startup.ArgumentList.Add(x.ToString()); startup.ArgumentList.Add(y.ToString());
        using var underlayProcess = Process.Start(startup)!;
        try
        {
            var line = await underlayProcess.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var underlay = JsonSerializer.Deserialize<NativeWindow>(line!)!;
            if (underlay.ProcessId == Environment.ProcessId) throw new Exception("Underlay must belong to a different process.");
            window.ApplySettings(new DesktopLyricsSettings());
            window.Width = 360; window.Height = 144;
            window.Position = new PixelPoint(x, y);
            window.UpdateLyrics("桌面歌词跨进程穿透验证", "下一行仍然清晰可见");
            window.Show();
            await Task.Delay(500);
            var handle = window.TryGetPlatformHandle()!.Handle;
            var point = new Native.Point(x + 40, y + 65);
            var results = new List<object>();
            foreach (var locked in new[] { false, true, false, true })
            {
                window.ApplySettings(new DesktopLyricsSettings { IsEnabled = true, IsLocked = locked });
                window.ApplyNativeInteraction();
                await Task.Delay(160);
                var hit = Native.WindowFromPoint(point);
                Native.GetWindowThreadProcessId(hit, out var hitProcess);
                var expected = locked ? new nint(underlay.Handle) : handle;
                var pixels = Capture(window, Path.Combine(output, locked ? "locked.png" : "unlocked.png"));
                var passed = hit == expected && pixels.CyanPixels > 20 && pixels.TransparentPixels > pixels.TotalPixels / 2;
                var item = new
                {
                    Locked = locked, Window = handle.ToInt64(), HitWindow = hit.ToInt64(), Expected = expected.ToInt64(),
                    HitProcessId = hitProcess, UnderlayProcessId = underlay.ProcessId,
                    ExtendedStyle = $"0x{Native.GetWindowLongPtr(handle, -20).ToInt64():X}",
                    ForegroundUnchanged = Native.GetForegroundWindow() == foreground, Pixels = pixels, Passed = passed
                };
                results.Add(item);
                Console.WriteLine(JsonSerializer.Serialize(item));
                if (!passed) _result = 1;
            }
            await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(results,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            window.CloseForShutdown();
            await underlayProcess.StandardInput.WriteLineAsync("close");
            await underlayProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static PixelSummary Capture(Window window, string path)
    {
        var size = PixelSize.FromSize(window.Bounds.Size, window.RenderScaling);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * window.RenderScaling, 96 * window.RenderScaling));
        bitmap.Render(window);
        bitmap.Save(path);
        var stride = size.Width * 4;
        var data = new byte[stride * size.Height];
        var memory = Marshal.AllocHGlobal(data.Length);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), memory, data.Length, stride);
            Marshal.Copy(memory, data, 0, data.Length);
        }
        finally { Marshal.FreeHGlobal(memory); }
        var transparent = 0; var cyan = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            if (data[i + 3] == 0) transparent++;
            if (data[i + 3] > 100 && data[i + 2] < 160 && data[i + 1] > 170 && data[i] > 170) cyan++;
        }
        return new(size.Width * size.Height, transparent, cyan);
    }

    private sealed record PixelSummary(int TotalPixels, int TransparentPixels, int CyanPixels);
    private sealed record NativeWindow(long Handle, int ProcessId);
    private sealed class FixtureApplication : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());
    }

    private static class Native
    {
        private static readonly WindowProcedure Procedure = DefWindowProc;
        public static int Underlay(int x, int y)
        {
            var brush = CreateSolidBrush(0x00603818);
            var instance = GetModuleHandle(null);
            var name = "NekoDesktopLyricsUnderlayFixture";
            var definition = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
                Instance = instance, Background = brush, ClassName = name
            };
            RegisterClassEx(ref definition);
            var handle = CreateWindowEx(0x08000080, name, name, 0x80000000, x, y, 400, 160,
                nint.Zero, nint.Zero, instance, nint.Zero);
            SetWindowPos(handle, new nint(-1), x, y, 400, 160, 0x0010 | 0x0040);
            Console.WriteLine(JsonSerializer.Serialize(new NativeWindow(handle.ToInt64(), Environment.ProcessId)));
            Console.Out.Flush();
            _ = Task.Run(() => { Console.ReadLine(); PostMessage(handle, 0x0010, nint.Zero, nint.Zero); });
            while (IsWindow(handle) && GetMessage(out var message, nint.Zero, 0, 0) > 0)
            { TranslateMessage(ref message); DispatchMessage(ref message); }
            DeleteObject(brush);
            return 0;
        }

        [StructLayout(LayoutKind.Sequential)] public readonly struct Point(int x, int y) { public readonly int X = x; public readonly int Y = y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
        {
            public uint Size; public uint Style; public nint Procedure; public int ClassExtra; public int WindowExtra;
            public nint Instance; public nint Icon; public nint Cursor; public nint Background;
            public string? MenuName; public string ClassName; public nint SmallIcon;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Message
        { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Point; }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProcedure(nint hwnd, uint msg, nint wParam, nint lParam);
        [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] public static extern nint WindowFromPoint(Point point);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hwnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass definition);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string className, string title,
            uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);
        [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out Message message, nint hwnd, uint first, uint last);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
        [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);
        [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    }
}
