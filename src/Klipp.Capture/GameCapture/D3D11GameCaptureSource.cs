using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Klipp.Capture.Models;
using Klipp.Capture.Video;
using Klipp.Core.Capture;
using Klipp.Core.Enums;
using Klipp.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace Klipp.Capture.GameCapture;

/// <summary>
/// Opt-in x64 D3D11 SDR capture. The native bridge copies clean pixels before it draws
/// notifications. It never changes focus, display modes or process security settings.
/// </summary>
public sealed class D3D11GameCaptureSource : IVideoCaptureSource
{
    private static readonly ConcurrentDictionary<Guid, D3D11GameCaptureSource> Connections = new();
    private readonly Guid _identity = Guid.NewGuid();
    private readonly int _frameRate, _canvasWidth, _canvasHeight;
    private readonly string _hookPath;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<VideoFrame> _frames = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(1)
        { SingleWriter = true, SingleReader = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<(uint Type, byte[] Payload)> _commands = Channel.CreateBounded<(uint, byte[])>(new BoundedChannelOptions(16)
        { SingleWriter = false, SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private NamedPipeServerStream? _pipe;
    private GameHookAttachment? _attachment;
    private Task? _readerTask, _writerTask, _heartbeatTask, _detachTask;
    private long _origin, _captured, _delivered, _dropped, _invalid, _lastFrameUtcTicks;
    private int _actualWidth, _actualHeight, _started, _disposed, _ready, _closed, _disconnectSent;
    private uint _processId;
    private bool _everQueuedPopup, _failedWithoutCleanAck;
    private DateTime? _graceUntil;

    public D3D11GameCaptureSource(CaptureTarget target, int frameRate, int canvasWidth, int canvasHeight, string? hookPath = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (frameRate is < 1 or > 240) throw new ArgumentOutOfRangeException(nameof(frameRate));
        if (canvasWidth <= 0 || canvasHeight <= 0 || canvasWidth > 16384 || canvasHeight > 16384 || (long)canvasWidth * canvasHeight * 4 > 512L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(canvasWidth));
        Target = target; _frameRate = frameRate; _canvasWidth = canvasWidth; _canvasHeight = canvasHeight;
        _hookPath = hookPath ?? Path.Combine(AppContext.BaseDirectory, "Klipp.GameHook.dll");
    }

    public CaptureTarget Target { get; }
    public string? FailureMessage { get; private set; }
    public bool IsCapturing { get; private set; }
    public bool SupportsInGameNotifications => Volatile.Read(ref _ready) != 0 && Volatile.Read(ref _closed) == 0 &&
        DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastFrameUtcTicks) < TimeSpan.FromSeconds(3).Ticks;
    public event EventHandler? TargetClosed;

    public CaptureSourceStatistics GetStatistics()
    {
        var ticks = Interlocked.Read(ref _lastFrameUtcTicks);
        return new(IsCapturing, Interlocked.Read(ref _captured), Interlocked.Read(ref _delivered), Interlocked.Read(ref _dropped),
            Interlocked.Read(ref _invalid), ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null,
            Volatile.Read(ref _actualWidth), Volatile.Read(ref _actualHeight));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Create a new game capture stream to restart capture.");
        if (!File.Exists(_hookPath)) throw new FileNotFoundException("The optional D3D11 game capture component is not installed.", _hookPath);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        startup.CancelAfter(TimeSpan.FromSeconds(10));
        var pipeName = "Klipp.GameCapture." + Guid.NewGuid().ToString("N");
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        try
        {
            // A stopped source can retain its bridge for a saved/stopped popup. A new
            // capture replaces it only after its positive clean-Present acknowledgement.
            foreach (var previous in Connections.Values.Where(source => source.Target.Handle == Target.Handle ||
                (Target.ProcessId != 0 && source._processId == Target.ProcessId)).ToArray())
                await previous.DetachForFallbackAsync().ConfigureAwait(false);
            _attachment = GameHookAttachment.Open(Target); _processId = _attachment.ProcessId;
            _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 64 * 1024, 64 * 1024);
            Connections[_identity] = this;
            var connection = _pipe.WaitForConnectionAsync(startup.Token);
            await Task.Run(() => _attachment.Attach(_hookPath,
                GameHookProtocol.StartArguments(pipeName, nonce, Target.Handle, _frameRate)), startup.Token).ConfigureAwait(false);
            await connection.ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var client) || client != _processId)
                throw new InvalidDataException("The game bridge peer could not be authenticated.");
            var hello = await GameHookProtocol.ReadAsync(_pipe, startup.Token).ConfigureAwait(false);
            if (hello.Type != GameHookProtocol.Hello || hello.Payload.Length != 76 ||
                BinaryPrimitives.ReadUInt32LittleEndian(hello.Payload) != GameHookProtocol.Magic ||
                BinaryPrimitives.ReadUInt32LittleEndian(hello.Payload.AsSpan(4)) != GameHookProtocol.Version ||
                BinaryPrimitives.ReadUInt32LittleEndian(hello.Payload.AsSpan(8)) != _processId ||
                !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(nonce), hello.Payload.AsSpan(12)))
                throw new InvalidDataException("The game bridge session could not be authenticated.");
            _origin = Stopwatch.GetTimestamp(); IsCapturing = true;
            _writerTask = WriteCommandsAsync(); _heartbeatTask = SendHeartbeatsAsync(); _readerTask = ReadFramesAsync();
            await _firstFrame.Task.WaitAsync(startup.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            FailureMessage ??= error is OperationCanceledException && !cancellationToken.IsCancellationRequested
                ? "The game did not produce a supported D3D11 SDR frame in time." : error.Message;
            IsCapturing = false; Volatile.Write(ref _ready, 0);
            _frames.Writer.TryComplete();
            throw new InvalidOperationException(FailureMessage, error);
        }
    }

    private async Task ReadFramesAsync()
    {
        SharedGameFrames? shared = null;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var message = await GameHookProtocol.ReadAsync(_pipe!, _lifetime.Token).ConfigureAwait(false);
                if (message.Type == GameHookProtocol.Failure)
                {
                    FailureMessage ??= Encoding.UTF8.GetString(message.Payload); Volatile.Write(ref _ready, 0);
                    Interlocked.Increment(ref _invalid);
                    _firstFrame.TrySetException(new InvalidOperationException(FailureMessage));
                    if (IsCapturing) { IsCapturing = false; TargetClosed?.Invoke(this, EventArgs.Empty); }
                    _frames.Writer.TryComplete();
                    // Keep reading the bounded clean-Present acknowledgement before WGC fallback.
                    continue;
                }
                if (message.Type == GameHookProtocol.Stopped)
                {
                    var clean = message.Payload.Length == 4 && BinaryPrimitives.ReadUInt32LittleEndian(message.Payload) == 1;
                    if (clean)
                    {
                        _stopped.TrySetResult(true);
                        if (IsCapturing && Volatile.Read(ref _disconnectSent) == 0)
                        {
                            FailureMessage ??= "The game capture bridge disconnected."; IsCapturing = false;
                            _firstFrame.TrySetException(new InvalidOperationException(FailureMessage));
                            _frames.Writer.TryComplete(); TargetClosed?.Invoke(this, EventArgs.Empty);
                        }
                        break;
                    }
                    // A frozen game can resume later. Keep the authenticated channel
                    // alive for its later positive acknowledgement, never assume clean.
                    continue;
                }
                if (message.Type != GameHookProtocol.Frame) throw new InvalidDataException("Unexpected game bridge message.");
                var info = GameHookProtocol.FrameInfo.Parse(message.Payload);
                if (shared?.Generation != info.Generation)
                {
                    Volatile.Write(ref _ready, 0); shared?.Dispose(); shared = null;
                    shared = new SharedGameFrames(_attachment!, info);
                }
                var read = shared.ReadLatest(info);
                if (read is null) continue;
                Interlocked.Exchange(ref _lastFrameUtcTicks, DateTime.UtcNow.Ticks);
                Volatile.Write(ref _actualWidth, info.Width); Volatile.Write(ref _actualHeight, info.Height);
                if (Volatile.Read(ref _ready) == 0)
                {
                    await _commands.Writer.WriteAsync((GameHookProtocol.Ready, GameHookProtocol.UInt(info.Generation)), _lifetime.Token).ConfigureAwait(false);
                    Volatile.Write(ref _ready, 1); _firstFrame.TrySetResult();
                }
                if (!IsCapturing) continue;
                Interlocked.Increment(ref _captured);
                var pixels = read.Value.Pixels;
                if (info.Width != _canvasWidth || info.Height != _canvasHeight)
                    pixels = BgraCaptureFrameFit.Fit(pixels, info.Width, info.Height, _canvasWidth, _canvasHeight);
                var timestamp = Math.Max(0, (long)((read.Value.Timestamp - _origin) * (10_000_000d / Stopwatch.Frequency)));
                var frame = new VideoFrame(_canvasWidth, _canvasHeight, PixelFormat.Bgra8, timestamp, pixels);
                if (_frames.Reader.TryRead(out _)) Interlocked.Increment(ref _dropped);
                if (!_frames.Writer.TryWrite(frame)) Interlocked.Increment(ref _dropped);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            FailureMessage ??= error.Message; Volatile.Write(ref _ready, 0);
            _firstFrame.TrySetException(new InvalidOperationException(FailureMessage, error));
            _stopped.TrySetResult(!_everQueuedPopup);
            if (IsCapturing) { IsCapturing = false; TargetClosed?.Invoke(this, EventArgs.Empty); }
            _frames.Writer.TryComplete();
        }
        finally { Volatile.Write(ref _ready, 0); shared?.Dispose(); }
    }

    private async Task WriteCommandsAsync()
    {
        try
        {
            await foreach (var command in _commands.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                await GameHookProtocol.WriteAsync(_pipe!, command.Type, command.Payload, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            FailureMessage ??= error.Message; Volatile.Write(ref _ready, 0);
            _firstFrame.TrySetException(new InvalidOperationException(FailureMessage, error));
            _stopped.TrySetResult(!_everQueuedPopup);
        }
    }

    private async Task SendHeartbeatsAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(600));
            do { await _commands.Writer.WriteAsync((GameHookProtocol.Heartbeat, GameHookProtocol.UInt(IsCapturing ? 1u : 0u)), _lifetime.Token).ConfigureAwait(false); }
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (ChannelClosedException) { }
    }

    public async IAsyncEnumerable<VideoFrame> ReadSamplesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        { Interlocked.Increment(ref _delivered); yield return frame; }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        IsCapturing = false; _frames.Writer.TryComplete();
        lock (_gate)
        {
            if (_graceUntil is null && _closed == 0)
            {
                _graceUntil = DateTime.UtcNow.AddSeconds(12);
                _ = EndGraceAsync();
            }
        }
        return Task.CompletedTask;
    }

    private async Task EndGraceAsync()
    {
        try { await Task.Delay(TimeSpan.FromSeconds(12), _lifetime.Token).ConfigureAwait(false); await DetachForFallbackAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception) { /* The registry retains a failed clean-Present barrier for the next WGC attempt. */ }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) return new ValueTask(StopAsync());
        return ValueTask.CompletedTask;
    }

    public Task DetachForFallbackAsync()
    {
        lock (_gate)
        {
            if (_detachTask?.IsFaulted == true && _closed == 0) _detachTask = null;
            return _detachTask ??= DetachCoreAsync();
        }
    }

    private async Task DetachCoreAsync()
    {
        IsCapturing = false; Volatile.Write(ref _ready, 0); _frames.Writer.TryComplete();
        var clean = !_everQueuedPopup;
        var keepBarrier = false;
        try
        {
            if (_pipe?.IsConnected == true && _writerTask is not null)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                if (Interlocked.Exchange(ref _disconnectSent, 1) == 0)
                {
                    await _commands.Writer.WriteAsync((GameHookProtocol.Hide, []), deadline.Token).ConfigureAwait(false);
                    await _commands.Writer.WriteAsync((GameHookProtocol.Disconnect, []), deadline.Token).ConfigureAwait(false);
                }
                clean = await _stopped.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            clean = !_everQueuedPopup || !IsWindow(Target.Handle);
            keepBarrier = !clean && _pipe?.IsConnected == true && _readerTask?.IsCompleted == false;
        }
        finally
        {
            if (!keepBarrier)
            {
                Volatile.Write(ref _closed, 1); _lifetime.Cancel(); _commands.Writer.TryComplete();
                _pipe?.Dispose();
                foreach (var task in new[] { _readerTask, _writerTask, _heartbeatTask })
                    if (task is not null) { try { await task.ConfigureAwait(false); } catch (Exception) { } }
                _attachment?.Dispose(); _attachment = null;
            }
        }
        // A closed game has no remaining displayed surface. A live frozen game must
        // acknowledge a clean Present before a monitor/WGC capture can begin.
        if (!clean && !IsWindow(Target.Handle)) clean = true;
        _failedWithoutCleanAck = !clean;
        if (clean) Connections.TryRemove(_identity, out _);
        else throw new InvalidOperationException("The game has not presented a clean frame after its notification. Resume or close the game before switching capture source.");
    }

    public static async Task DetachAllForFallbackAsync()
    {
        foreach (var source in Connections.Values.ToArray())
        {
            if (source._failedWithoutCleanAck && !IsWindow(source.Target.Handle))
            {
                if (source._closed != 0) { Connections.TryRemove(source._identity, out _); continue; }
                source._stopped.TrySetResult(true);
            }
            await source.DetachForFallbackAsync().ConfigureAwait(false);
        }
    }

    public static bool IsNotificationTargetReady(nint hwnd, uint processId) => Find(hwnd, processId) is not null;

    public static bool TryShowNotification(nint hwnd, uint processId, ReadOnlyMemory<byte> premultipliedBgra,
        int width, int height, int x, int y, int durationMs, bool animate)
    {
        var source = Find(hwnd, processId);
        if (source is null || width <= 0 || height <= 0 || width > 1024 || height > 512 || premultipliedBgra.Length != (long)width * height * 4) return false;
        durationMs = Math.Clamp(durationMs, 100, 12000);
        if (source._graceUntil is { } grace) durationMs = Math.Min(durationMs, (int)(grace - DateTime.UtcNow).TotalMilliseconds);
        if (durationMs < 100) return false;
        var payload = new byte[checked(24 + width * height * 4)];
        BinaryPrimitives.WriteInt32LittleEndian(payload, width); BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), height);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), x); BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(12), y);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16), durationMs); BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(20), animate ? 1 : 0);
        premultipliedBgra.Span.CopyTo(payload.AsSpan(24));
        if (!source._commands.Writer.TryWrite((GameHookProtocol.Popup, payload))) return false;
        source._everQueuedPopup = true;
        return true;
    }

    public static void HideNotification(nint hwnd, uint processId)
    {
        foreach (var source in Connections.Values.Where(source => source.Target.Handle == hwnd && source._processId == processId))
            source._commands.Writer.TryWrite((GameHookProtocol.Hide, []));
    }

    private static D3D11GameCaptureSource? Find(nint hwnd, uint pid) => Connections.Values.FirstOrDefault(source =>
        source.Target.Handle == hwnd && source._processId == pid && source.SupportsInGameNotifications);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
}
