using System.Diagnostics;
using Klipp.Desktop.Services;

internal static class AppInstanceChecks
{
    internal static int RunChild(string key)
    {
        using var instance = new AppInstanceCoordinator(key);
        if (instance.IsPrimary) return 1;
        instance.RequestActivation();
        return 0;
    }

    internal static void Run()
    {
        // The unique key keeps these cross-process checks separate from the
        // user's running app and never opens a window or loads their settings.
        var key = "Klipp.InstanceCheck." + Guid.NewGuid().ToString("N");
        using var activated = new SemaphoreSlim(0);
        using (var instance = new AppInstanceCoordinator(key))
        {
            Require(instance.IsPrimary, "The initial instance did not acquire ownership.");
            LaunchChild(key);
            instance.Listen(() => activated.Release());
            Require(activated.Wait(5000), "A launch during startup lost its activation signal.");
            LaunchChild(key);
            Require(activated.Wait(5000), "A second launch did not activate the existing instance.");
        }
        using var reopened = new AppInstanceCoordinator(key);
        Require(reopened.IsPrimary, "Exiting Klipp left instance ownership locked.");
        Console.WriteLine("PASS: existing-instance activation across processes, startup signal retention and ownership release");
    }

    private static void LaunchChild(string key)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--instance-check-child");
        start.ArgumentList.Add(key);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start instance check.");
        Require(child.WaitForExit(15000), "The secondary instance did not exit promptly.");
        Require(child.ExitCode == 0, "A secondary launch incorrectly became the primary instance.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
