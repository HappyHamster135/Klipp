using System;
using System.Security.Principal;
using System.Threading;

namespace Klipp.Desktop.Services;

/// <summary>Reopening Klipp activates its existing window, including while it is in the tray.</summary>
internal sealed class AppInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private RegisteredWaitHandle? _activationWait;
    private volatile bool _disposed;

    internal bool IsPrimary { get; }

    internal AppInstanceCoordinator(string? instanceKey = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User?.Value ?? Environment.UserName;
        var name = @"Local\" + (instanceKey ?? "Klipp.Desktop") + "." + user;
        // Create the event before acquiring ownership. A second launch during
        // startup can signal it before the first window has finished loading.
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activate");
        _mutex = new Mutex(false, name + ".Instance");
        try { IsPrimary = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
    }

    internal void RequestActivation() => _activation.Set();

    internal void Listen(Action activated)
    {
        if (!IsPrimary || _disposed || _activationWait is not null) return;
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activation,
            (_, _) => { if (!_disposed) activated(); }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _activationWait?.Unregister(null);
        _activationWait = null;
        // Ownership is acquired and released on the application UI thread.
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
        _activation.Dispose();
    }
}
