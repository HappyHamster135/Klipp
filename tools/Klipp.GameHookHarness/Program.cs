using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Klipp.Capture.GameCapture;
using Klipp.Capture.Models;
using Klipp.Capture.Video;
using Klipp.Core.Models;

internal static class Program
{
    private static int _phase;
    private static readonly long[] CleanFrames = new long[4], CleanBad = new long[4], WgcFrames = new long[4], WgcRed = new long[4], WgcBackground = new long[4];
    private static long _nativeMaximumGap;

    private static async Task<int> Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        SetThreadDpiAwarenessContext(new nint(-4));
        var reportPath = Value(args, "--report");
        D3D11GameCaptureSource? source = null;
        WgcCaptureSource? wgc = null;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        try
        {
            var processes = Process.GetProcessesByName("Klipp.ExclusiveFullscreenHarness").Where(p => p.MainWindowHandle != 0).ToArray();
            if (processes.Length != 1) throw new InvalidOperationException("Start exactly one disposable Klipp.ExclusiveFullscreenHarness fake game first.");
            using var process = processes[0];
            var window = process.MainWindowHandle;
            if (!GetClientRect(window, out var rect)) throw new InvalidOperationException("The fake game window is unavailable.");
            var target = new CaptureTarget { Kind = CaptureTargetKind.Window, Handle = window, DisplayName = "Disposable D3D11 QA game",
                ProcessId = (uint)process.Id, ProcessCreated = process.StartTime.ToFileTimeUtc() };
            var hook = Value(args, "--hook") ?? Path.GetFullPath("src/Klipp.GameHook/bin/x64/Release/Klipp.GameHook.dll");
            var foreground = GetForegroundWindow();
            source = new(target, 30, rect.Right, rect.Bottom, hook);
            if (args.Contains("--expect-rejected"))
            {
                try { await source.StartAsync(lifetime.Token); }
                catch (Exception error)
                {
                    await source.DetachForFallbackAsync(); await source.DisposeAsync(); await D3D11GameCaptureSource.DetachAllForFallbackAsync();
                    Console.WriteLine($"PASS unsupported presentation rejected before popup: {error.Message}");
                    return 0;
                }
                throw new InvalidOperationException("The unsupported presentation was incorrectly accepted.");
            }
            await source.StartAsync(lifetime.Token);
            var cleanTask = Observe(source, native: true, lifetime.Token);
            // Intentional test-only simultaneous post-presentation observer. Production
            // must never enable WGC while an injected game notification is active.
            wgc = new(target, 30, hideCaptureBorder: true, captureCursor: false);
            await wgc.StartAsync(lifetime.Token);
            var wgcTask = Observe(wgc, native: false, lifetime.Token);
            Console.WriteLine($"READY clean bridge: window=0x{window:X}, pid={process.Id}, canvas={rect.Right}x{rect.Bottom}; focus unchanged={foreground == GetForegroundWindow()}");
            var hold = int.TryParse(Value(args, "--hold"), out var holdSeconds) ? holdSeconds : 1;
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(hold, 1, 25)), lifetime.Token);

            if (args.Contains("--resize"))
            {
                var before = source.GetStatistics().CapturedFrames;
                SendMessage(window, 0x8052, 1280, 720);
                var resizedUntil = Stopwatch.StartNew();
                while (resizedUntil.Elapsed < TimeSpan.FromSeconds(4))
                {
                    var stats = source.GetStatistics();
                    if (stats.FrameWidth == 1280 && stats.FrameHeight == 720 && stats.CapturedFrames > before + 3) break;
                    await Task.Delay(40, lifetime.Token);
                }
                var resized = source.GetStatistics();
                if (resized.FrameWidth != 1280 || resized.FrameHeight != 720 || resized.CapturedFrames <= before + 3)
                    throw new InvalidOperationException("Native capture did not recover after ResizeBuffers.");
                Console.WriteLine($"PASS ResizeBuffers: actual=1280x720; delivered canvas remains {rect.Right}x{rect.Bottom}.");
                await Task.Delay(400, lifetime.Token);
            }

            var card = new byte[384 * 192 * 4];
            for (var i = 0; i < card.Length; i += 4) { card[i + 2] = 128; card[i + 3] = 128; }
            Volatile.Write(ref _phase, 1);
            if (!D3D11GameCaptureSource.TryShowNotification(window, (uint)process.Id, card, 384, 192, 16, 16, 6000, false))
                throw new InvalidOperationException("The clean bridge refused its notification.");
            Console.WriteLine("POPUP live: a half-transparent red test card is now part of the game's displayed backbuffer; clean capture must remain blue.");
            await Task.Delay(1800, lifetime.Token);
            if (args.Contains("--pause-barrier"))
            {
                SendMessage(window, 0x8051, 1, 0);
                var rejected = false;
                try { await source.DetachForFallbackAsync(); }
                catch (InvalidOperationException error) { rejected = true; Console.WriteLine("PASS frozen-game fallback refused: " + error.Message); }
                if (!rejected) throw new InvalidOperationException("Fallback incorrectly accepted a frozen displayed notification.");
                SendMessage(window, 0x8051, 0, 0);
                await Task.Delay(100, lifetime.Token);
                await source.DetachForFallbackAsync();
                Console.WriteLine("PASS fallback recovered after the fake game presented a clean frame.");
            }
            else
            {
                await source.StopAsync(); Volatile.Write(ref _phase, 2);
                if (!D3D11GameCaptureSource.TryShowNotification(window, (uint)process.Id, card, 384, 192, 16, 16, 3000, true))
                    throw new InvalidOperationException("The stopped recording bridge did not retain its notification grace period.");
                await Task.Delay(1500, lifetime.Token);
                await source.DetachForFallbackAsync();
                Console.WriteLine("PASS stop retained notification grace, then positively acknowledged a clean Present.");
            }
            Volatile.Write(ref _phase, 3);
            await Task.Delay(900, lifetime.Token);
            var focusPreserved = foreground == GetForegroundWindow();
            var result = new { cleanFrames = CleanFrames, cleanBad = CleanBad, postPresentFrames = WgcFrames,
                postPresentRed = WgcRed, postPresentBackground = WgcBackground, nativeMaximumGapMs = _nativeMaximumGap / 1000d,
                focusPreserved, cleanStats = source.GetStatistics(), failure = source.FailureMessage };
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
            if (reportPath is not null) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!); await File.WriteAllTextAsync(reportPath, json); }
            if (CleanFrames[1] < 5 || CleanBad.Sum() != 0 || WgcRed[1] < 3 || WgcBackground[3] < 3)
                throw new InvalidOperationException("Clean/post-present pixel separation did not satisfy the integration assertions.");
            await D3D11GameCaptureSource.DetachAllForFallbackAsync();
            Console.WriteLine("PASS arbitrary selected-process attach, clean frame pixels, displayed alpha overlay, stop/grace and clean detach.");
            lifetime.Cancel();
            try { await Task.WhenAll(cleanTask, wgcTask); } catch (OperationCanceledException) { }
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
        finally
        {
            lifetime.Cancel();
            if (source is not null) { try { await source.DetachForFallbackAsync(); } catch (Exception error) { Console.WriteLine("Detach barrier: " + error.Message); } await source.DisposeAsync(); }
            if (wgc is not null) await wgc.DisposeAsync();
        }
    }

    private static async Task Observe(IVideoCaptureSource source, bool native, CancellationToken token)
    {
        var previous = Stopwatch.GetTimestamp();
        await foreach (var frame in source.ReadSamplesAsync(token))
        {
            var now = Stopwatch.GetTimestamp();
            var gap = (long)((now - previous) * 1_000_000d / Stopwatch.Frequency); previous = now;
            if (native) InterlockedMax(ref _nativeMaximumGap, gap);
            var phase = Math.Clamp(Volatile.Read(ref _phase), 0, 3);
            var offset = (Math.Min(frame.Height - 1, 120) * frame.Width + Math.Min(frame.Width - 1, 120)) * 4;
            var span = frame.PixelData.Span;
            var blue = span[offset]; var green = span[offset + 1]; var red = span[offset + 2];
            var background = Math.Abs(blue - 65) <= 4 && Math.Abs(green - 45) <= 4 && Math.Abs(red - 26) <= 4;
            var overlaid = red > 105 && green < 35 && blue < 48;
            if (native) { Interlocked.Increment(ref CleanFrames[phase]); if (!background) Interlocked.Increment(ref CleanBad[phase]); }
            else { Interlocked.Increment(ref WgcFrames[phase]); if (overlaid) Interlocked.Increment(ref WgcRed[phase]); if (background) Interlocked.Increment(ref WgcBackground[phase]); }
        }
    }

    private static string? Value(string[] args, string key) { var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
    private static void InterlockedMax(ref long value, long next) { long current; do { current = Interlocked.Read(ref value); if (current >= next) return; } while (Interlocked.CompareExchange(ref value, next, current) != current); }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint value);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
