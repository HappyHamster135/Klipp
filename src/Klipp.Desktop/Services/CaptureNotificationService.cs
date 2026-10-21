using System;
using System.Diagnostics;
using Klipp.Core.Models;
using Microsoft.UI.Dispatching;

namespace Klipp.Desktop.Services;

/// <summary>One nonactivating popup and a small coalescing backlog, owned by the UI dispatcher.</summary>
internal sealed class CaptureNotificationService : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueueTimer _presentationTimer;
    private readonly CaptureNotificationQueue _queue = new();
    private CaptureNotificationWindow? _window;
    private CaptureNotificationMessage? _visible;
    private DateTimeOffset _firstShown;
    private DateTimeOffset _dismissAt;
    private bool _disposed;
    private DateTimeOffset _retryAfter;
    private CaptureNotificationContext? _presentationContext;
    private readonly CaptureGameNotificationPresenter _gamePresenter = new();
    private bool _presentedInGame;

    public CaptureNotificationService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += OnTimerTick;
        _presentationTimer = dispatcher.CreateTimer();
        _presentationTimer.IsRepeating = true;
        _presentationTimer.Interval = TimeSpan.FromMilliseconds(250);
        _presentationTimer.Tick += OnPresentationTick;
    }

    public void Show(CaptureNotificationMessage message)
    {
        if (_disposed || DateTimeOffset.UtcNow < _retryAfter) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => Show(message));
            return;
        }
        var now = DateTimeOffset.UtcNow;
        if (_visible is { } visible && CaptureNotificationQueue.ReplacesVisible(visible.Kind, message.Kind))
        {
            if (visible.Kind != message.Kind) _firstShown = now;
            _dismissAt = CaptureNotificationQueue.CoalescedDeadline(_firstShown, now, message.DurationSeconds);
            Present(message);
            return;
        }
        _queue.Enqueue(message, now);
        if (_visible is null) ShowNext();
    }

    private void ShowNext()
    {
        _timer.Stop();
        _presentationTimer.Stop();
        if (_disposed) return;
        _visible = null;
        var now = DateTimeOffset.UtcNow;
        if (_queue.Dequeue(now) is not { } next)
        {
            _gamePresenter.Hide();
            _presentedInGame = false;
            _window?.HideNotification();
            return;
        }
        _firstShown = now;
        _dismissAt = now.AddSeconds(next.DurationSeconds);
        Present(next);
    }

    private void Present(CaptureNotificationMessage message)
    {
        try
        {
            if (_window is null)
            {
                _window = new CaptureNotificationWindow();
                _window.DismissRequested += OnDismissRequested;
            }
            _visible = message;
            var context = CaptureNotificationContext.Read();
            _presentedInGame = _gamePresenter.TryShow(message, context, _dismissAt - DateTimeOffset.UtcNow);
            if (_presentedInGame)
                _window.HideNotification(immediate: true);
            else
            {
                _gamePresenter.Hide();
                _window.ShowNotification(message, context.Passive, context.FullScreen);
            }
            _presentationContext = context;
            StartTimer();
            _presentationTimer.Start();
        }
        catch (Exception exception)
        {
            // Notification failure must never interrupt saving a clip or recording.
            Debug.WriteLine($"Capture notification unavailable: {exception}");
            // A resolution/display transition can briefly make placement or
            // exclusion unavailable. Hide this popup, then allow a later event
            // to retry instead of disabling feedback for the entire session.
            _retryAfter = DateTimeOffset.UtcNow.AddSeconds(2);
            _queue.Clear();
            CloseWindow();
        }
    }

    private void StartTimer()
    {
        _timer.Stop();
        var remaining = _dismissAt - DateTimeOffset.UtcNow;
        _timer.Interval = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
        _timer.Start();
    }

    private void OnTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (DateTimeOffset.UtcNow < _dismissAt) StartTimer();
        else ShowNext();
    }

    private void OnPresentationTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || _visible is null || _window is null) return;
        try
        {
            var context = CaptureNotificationContext.Read();
            if (_presentationContext == context) return;
            var inGame = _gamePresenter.TryShow(_visible, context, _dismissAt - DateTimeOffset.UtcNow);
            if (inGame) _window.HideNotification(immediate: true);
            else
            {
                _gamePresenter.Hide();
                if (_presentedInGame) _window.ShowNotification(_visible, context.Passive, context.FullScreen);
                else _window.UpdatePresentation(context.Passive, context.FullScreen);
            }
            _presentedInGame = inGame;
            _presentationContext = context;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Capture notification presentation unavailable: {exception}");
            _retryAfter = DateTimeOffset.UtcNow.AddSeconds(2);
            _queue.Clear();
            CloseWindow();
        }
    }

    private void OnDismissRequested(object? sender, EventArgs args) => ShowNext();

    private void CloseWindow()
    {
        _timer.Stop();
        _presentationTimer.Stop();
        _presentationContext = null;
        _gamePresenter.Hide();
        _presentedInGame = false;
        _visible = null;
        if (_window is not { } window) return;
        _window = null;
        window.DismissRequested -= OnDismissRequested;
        try { window.Close(); } catch (Exception exception) { Debug.WriteLine(exception); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(Dispose);
            return;
        }
        _disposed = true;
        _timer.Tick -= OnTimerTick;
        _presentationTimer.Tick -= OnPresentationTick;
        _queue.Clear();
        CloseWindow();
        _gamePresenter.Dispose();
    }
}
