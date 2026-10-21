using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Klipp.Encoding.Audio;

/// <summary>
/// Fångar systemljudet (det som spelas i högtalarna) via WASAPI loopback.
/// </summary>
/// <remarks>
/// Använder NAudio:s WasapiLoopbackCapture, som internt använder Windows WASAPI
/// loopback-mekanism — samma som OBS, Discord och ShadowPlay. Fungerar på alla
/// moderna Windows utan att användaren behöver aktivera "Stereo Mix".
///
/// Ljudet levereras som raw PCM via <see cref="DataAvailable"/>-eventet. Formatet
/// (sample rate, kanaler, bit-djup) anges av <see cref="WaveFormat"/> — typiskt
/// 32-bit float, 44.1 eller 48 kHz, stereo.
/// </remarks>
public sealed class AudioLoopbackCapture : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private bool _disposed;

    /// <summary>
    /// Ljudformatet som fångas. Null innan Start() anropats.
    /// </summary>
    public WaveFormat? WaveFormat => _capture?.WaveFormat;

    /// <summary>
    /// Triggas löpande med nya PCM-samples. Arg innehåller en byte-buffer + längd.
    /// </summary>
    public event EventHandler<AudioDataEventArgs>? DataAvailable;

    /// <summary>Triggas när capturen stoppats (av Stop eller fel).</summary>
    public event EventHandler? CaptureStopped;

    /// <summary>
    /// Startar loopback-capture av systemets standard-renderingsenhet
    /// (det som ljudet spelas på just nu).
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_capture is not null) throw new InvalidOperationException("Capture is already running.");

        // WasapiLoopbackCapture använder standard-renderingsenheten by default
        _capture = new WasapiLoopbackCapture();

        _capture.DataAvailable += (_, e) =>
        {
            // e.Buffer är återanvänd av NAudio — vi får bara läsa e.BytesRecorded bytes
            DataAvailable?.Invoke(this, new AudioDataEventArgs(e.Buffer, e.BytesRecorded));
        };

        _capture.RecordingStopped += (_, _) =>
        {
            CaptureStopped?.Invoke(this, EventArgs.Empty);
        };

        _capture.StartRecording();
    }

    /// <summary>Stoppar capturen.</summary>
    public void Stop()
    {
        _capture?.StopRecording();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_capture is not null)
        {
            try { _capture.StopRecording(); } catch { }
            _capture.Dispose();
            _capture = null;
        }
    }
}

/// <summary>
/// PCM-data från loopback-capturen. Buffer är NAudios återanvända buffert —
/// kopiera datan om du behöver behålla den efter att eventet returnerat.
/// </summary>
public sealed class AudioDataEventArgs : EventArgs
{
    public byte[] Buffer { get; }
    public int BytesRecorded { get; }

    public AudioDataEventArgs(byte[] buffer, int bytesRecorded)
    {
        Buffer = buffer;
        BytesRecorded = bytesRecorded;
    }
}
