using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Capture.GameCapture;
using Klipp.Capture.Models;
using Klipp.Capture.Video;
using Klipp.Core.Models;

namespace Klipp.Desktop.Services;

public static class VideoCaptureSourceFactory
{
    public static RoutedVideoCaptureSource Create(CaptureTarget target, int frameRate,
        int canvasWidth, int canvasHeight, CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        var tryGameCapture = options.EnableExclusiveGameCapture &&
            target.Kind == CaptureTargetKind.Window && RuntimeInformation.ProcessArchitecture == Architecture.X64;
        return new RoutedVideoCaptureSource(target,
            tryGameCapture
                ? () => new D3D11GameCaptureSource(target, frameRate, canvasWidth, canvasHeight)
                : null,
            () => new WgcCaptureSource(target, frameRate, options.HideCaptureBorder, options.CaptureCursor),
            source => ((D3D11GameCaptureSource)source).DetachForFallbackAsync(),
            () => D3D11GameCaptureSource.DetachAllForFallbackAsync());
    }
}

/// <summary>Selects one capture backend before publishing frames to a recorder.</summary>
public sealed class RoutedVideoCaptureSource : IVideoCaptureSource
{
    private readonly Func<IVideoCaptureSource>? _createGameSource;
    private readonly Func<IVideoCaptureSource> _createWindowsSource;
    private readonly Func<IVideoCaptureSource, Task> _detachGameSource;
    private readonly Func<Task> _detachAllGameSources;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _eventGate = new();
    private IVideoCaptureSource? _source;
    private bool _starting;
    private bool _closedDuringStart;
    private bool _forwardEvents;
    private bool _disposed;
    private string? _failureMessage;

    internal RoutedVideoCaptureSource(CaptureTarget target,
        Func<IVideoCaptureSource>? createGameSource, Func<IVideoCaptureSource> createWindowsSource,
        Func<IVideoCaptureSource, Task> detachGameSource, Func<Task> detachAllGameSources)
    {
        Target = target;
        _createGameSource = createGameSource;
        _createWindowsSource = createWindowsSource;
        _detachGameSource = detachGameSource;
        _detachAllGameSources = detachAllGameSources;
    }

    public CaptureTarget Target { get; }
    public bool IsCapturing => Volatile.Read(ref _source)?.IsCapturing == true;
    public string? FailureMessage => Volatile.Read(ref _source)?.FailureMessage ?? _failureMessage;
    public string BackendName { get; private set; } = "Windows capture";
    public string? FallbackReason { get; private set; }
    public event EventHandler? TargetClosed;

    public CaptureSourceStatistics GetStatistics() => Volatile.Read(ref _source)?.GetStatistics() ?? default;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsCapturing) return;
            if (_source is not null) await ReleaseSourceAsync(_source).ConfigureAwait(false);
            _failureMessage = null;
            FallbackReason = null;

            if (_createGameSource is not null)
            {
                IVideoCaptureSource? gameSource = null;
                try
                {
                    gameSource = _createGameSource();
                    await StartSourceAsync(gameSource, cancellationToken).ConfigureAwait(false);
                    BackendName = "DirectX 11 game capture";
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    if (gameSource is not null)
                        await ReleaseSourceAsync(gameSource, detachGameSource: true).ConfigureAwait(false);
                    throw;
                }
                catch (Exception exception)
                {
                    FallbackReason = exception.Message;
                    if (gameSource is not null)
                        await ReleaseSourceAsync(gameSource, detachGameSource: true).ConfigureAwait(false);
                }
            }

            // A hook may still belong to a previous capture or popup. Windows
            // capture can begin only after every hook acknowledges clean output.
            try { await _detachAllGameSources().ConfigureAwait(false); }
            catch (Exception exception)
            {
                _failureMessage = "Game capture could not be safely detached. Windows capture was not started.";
                throw new InvalidOperationException(_failureMessage, exception);
            }
            cancellationToken.ThrowIfCancellationRequested();
            BackendName = "Windows capture";
            var windowsSource = _createWindowsSource();
            try { await StartSourceAsync(windowsSource, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception)
            {
                _failureMessage = exception.Message;
                await ReleaseSourceAsync(windowsSource).ConfigureAwait(false);
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StartSourceAsync(IVideoCaptureSource source, CancellationToken cancellationToken)
    {
        source.TargetClosed += OnSourceTargetClosed;
        lock (_eventGate)
        {
            _source = source;
            _starting = true;
            _closedDuringStart = false;
            _forwardEvents = false;
        }
        await source.StartAsync(cancellationToken).ConfigureAwait(false);
        lock (_eventGate)
        {
            if (_closedDuringStart || !source.IsCapturing)
                throw new InvalidOperationException(source.FailureMessage ?? "The selected capture source closed before recording started.");
            _starting = false;
            _forwardEvents = true;
        }
    }

    private void OnSourceTargetClosed(object? sender, EventArgs args)
    {
        lock (_eventGate)
        {
            if (!ReferenceEquals(sender, _source)) return;
            if (_starting) { _closedDuringStart = true; return; }
            if (!_forwardEvents) return;
        }
        TargetClosed?.Invoke(this, EventArgs.Empty);
    }

    private async Task ReleaseSourceAsync(IVideoCaptureSource source, bool detachGameSource = false)
    {
        source.TargetClosed -= OnSourceTargetClosed;
        lock (_eventGate)
        {
            if (ReferenceEquals(_source, source)) _source = null;
            _starting = false;
            _forwardEvents = false;
        }
        try
        {
            if (detachGameSource) await _detachGameSource(source).ConfigureAwait(false);
        }
        finally { await source.DisposeAsync().ConfigureAwait(false); }
    }

    public IAsyncEnumerable<VideoFrame> ReadSamplesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var source = Volatile.Read(ref _source) ?? throw new InvalidOperationException("Start capture before reading video frames.");
        return source.ReadSamplesAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_eventGate) _forwardEvents = false;
            if (_source is not null) await _source.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_source is not null) await ReleaseSourceAsync(_source).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }
}
