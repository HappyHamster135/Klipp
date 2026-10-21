using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.DynamicDependency;

namespace Klipp.Desktop;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
#if !MICROSOFT_WINDOWSAPPSDK_SELFCONTAINED
        InitializeWindowsAppSdk();
#endif

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }

#if !MICROSOFT_WINDOWSAPPSDK_SELFCONTAINED
    private static void InitializeWindowsAppSdk()
    {
        var minVersion = new PackageVersion(Microsoft.WindowsAppSDK.Runtime.Version.UInt64);
        if (Bootstrap.TryInitialize(
                Microsoft.WindowsAppSDK.Release.MajorMinor,
                Microsoft.WindowsAppSDK.Release.VersionTag,
                minVersion,
                Bootstrap.InitializeOptions.OnNoMatch_ShowUI,
                out var hresult))
        {
            return;
        }

        Environment.Exit(hresult);
    }
#endif
}
