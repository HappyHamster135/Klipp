using Klipp.Desktop.Controls;
using Microsoft.Windows.ApplicationModel.DynamicDependency;

// Default checks render the actual Win2D graph without showing a window.
// Opt-in modes exercise synthetic video decoders or a disposable notification;
// none load user projects or write app settings.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!Bootstrap.TryInitialize(Microsoft.WindowsAppSDK.Release.MajorMinor,
            Microsoft.WindowsAppSDK.Release.VersionTag,
            new PackageVersion(Microsoft.WindowsAppSDK.Runtime.Version.UInt64),
            Bootstrap.InitializeOptions.None, out var error))
            throw new InvalidOperationException($"Windows App Runtime unavailable: 0x{error:X8}");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (args.Length == 2 && args[0] == "--instance-check-child")
        {
            var result = AppInstanceChecks.RunChild(args[1]);
            Bootstrap.Shutdown();
            return result;
        }
        if (args.Contains("--instance-checks"))
        {
            AppInstanceChecks.Run();
            Bootstrap.Shutdown();
            return 0;
        }
        if (args.Contains("--tray-checks"))
        {
            Exception? failure = null;
            Microsoft.UI.Xaml.Application.Start(_ =>
            {
                var app = new HeadlessApplication();
                var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(dispatcher));
                dispatcher.TryEnqueue(async () =>
                {
                    try { await TrayIconChecks.RunAsync(); }
                    catch (Exception exception) { failure = exception; }
                    finally { app.Exit(); }
                });
            });
            Bootstrap.Shutdown();
            if (failure is not null) throw failure;
            return 0;
        }
        if (args.Contains("--capture-source-targets"))
        {
            Microsoft.UI.Xaml.Application.Start(_ =>
            {
                var app = new HeadlessApplication();
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() => CaptureSourceTargets.Open(app));
            });
            Bootstrap.Shutdown();
            return 0;
        }
        if (args.Contains("--notification-checks") || args.Contains("--fullscreen-notification-checks"))
        {
            Exception? failure = null;
            Microsoft.UI.Xaml.Application.Start(_ =>
            {
                var app = new HeadlessApplication();
                var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(dispatcher));
                dispatcher.TryEnqueue(async () =>
                {
                    try { await CaptureNotificationChecks.RunAsync(args.Contains("--fullscreen-notification-checks")); }
                    catch (Exception exception) { failure = exception; }
                    finally { app.Exit(); }
                });
            });
            Bootstrap.Shutdown();
            if (failure is not null) throw failure;
            return 0;
        }
        if (args.Contains("--video-range-checks"))
        {
            Microsoft.UI.Xaml.Application.Start(_ =>
            {
                var app = new HeadlessApplication();
                MediaCopyChecks.RunRangeChecks();
                app.Exit();
            });
            Console.WriteLine("PASS: early frame-server mode keeps paused video color range and regional FX outside pixels");
            Bootstrap.Shutdown();
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--video-copy")
        {
            MediaCopyChecks.Run(args[1]);
            Bootstrap.Shutdown();
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--video-copy-with-element")
        {
            Microsoft.UI.Xaml.Application.Start(_ =>
            {
                var app = new HeadlessApplication();
                MediaCopyChecks.Run(args[1], attachElement: true);
                app.Exit();
            });
            Bootstrap.Shutdown();
            return 0;
        }
        SafeClipEditorView.RunTimelineEffectPixelChecks();
        Console.WriteLine("PASS: native Win2D regional FX pixels, alpha, strength, timing, stacking, and disposal");
        Bootstrap.Shutdown();
        return 0;
    }
}

internal sealed class HeadlessApplication : Microsoft.UI.Xaml.Application;
