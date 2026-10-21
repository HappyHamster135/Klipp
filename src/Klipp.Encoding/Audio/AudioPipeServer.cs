using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace Klipp.Encoding.Audio;

/// <summary>
/// Named pipe-server som tar emot raw PCM-ljud och gör det tillgängligt för FFmpeg
/// att läsa som en input-ström (parallellt med video på stdin).
/// </summary>
/// <remarks>
/// FFmpeg-processen har bara en stdin (som vi pipar video till). För att ge FFmpeg
/// en ANDRA ström använder vi en namngiven Windows-pipe. FFmpeg öppnar den som
/// "-i \\.\pipe\NAMN". Detta är samma teknik OBS använder för fler-ströms-input.
/// </remarks>
public sealed class AudioPipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeServerStream? _pipe;
    private volatile bool _connected;
    private Task? _connectionTask;
    private bool _disposed;

    public AudioPipeServer()
    {
        // Unikt namn per instans så flera inspelningar inte krockar
        _pipeName = $"klipp_audio_{Guid.NewGuid():N}";
    }

    /// <summary>Pipens fulla sökväg som FFmpeg använder: \\.\pipe\NAMN.</summary>
    public string PipePath => $@"\\.\pipe\{_pipeName}";

    /// <summary>True när FFmpeg har anslutit och vi kan skriva.</summary>
    public bool IsConnected => _connected;

    /// <summary>
    /// Skapar pipen och börjar vänta på att FFmpeg ansluter (i bakgrunden).
    /// Anropas innan FFmpeg startas.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _pipe = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.Out,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        // Vänta på anslutning i bakgrunden — FFmpeg ansluter när det startat
        _connectionTask = WaitForConnectionAsync();
    }

    private async Task WaitForConnectionAsync()
    {
        try
        {
            await _pipe!.WaitForConnectionAsync().ConfigureAwait(false);
            _connected = true;
        }
        catch
        {
            // Pipe stängdes innan anslutning — OK vid avbruten inspelning
        }
    }

    /// <summary>
    /// Writes PCM in order. The first write waits for FFmpeg instead of dropping
    /// the beginning of the selected audio stream during encoder startup.
    /// </summary>
    public async Task WriteAsync(byte[] buffer, int count, CancellationToken cancellationToken = default)
    {
        if (_disposed || _pipe is null)
            return;

        if (!_connected && _connectionTask is not null)
            await _connectionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_disposed || !_connected || _pipe is null || !_pipe.IsConnected) return;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || !_connected || _pipe is null || !_pipe.IsConnected)
                return;

            await _pipe.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // FFmpeg stängde sin ände — sluta skriva
            _connected = false;
        }
        catch (ObjectDisposedException)
        {
            _connected = false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _connected = false;

        if (_pipe is not null)
        {
            try
            {
                if (_pipe.IsConnected)
                {
                    _pipe.Flush();
                    _pipe.Disconnect();
                }
            }
            catch { }

            await _pipe.DisposeAsync();
            _pipe = null;
        }

        _writeLock.Dispose();
    }
}
