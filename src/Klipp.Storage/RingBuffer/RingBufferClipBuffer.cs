using Klipp.Core.Abstractions;
using Klipp.Core.Enums;
using Klipp.Core.Models;

namespace Klipp.Storage.RingBuffer;

/// <summary>
/// In-memory ring buffer för kodade samples. Stödjer "spara senaste N sekunder"
/// retroaktivt via <see cref="FlushLastSecondsAsync"/>. Tråd-säker.
/// </summary>
/// <remarks>
/// Video lagras som <see cref="GroupOfPictures"/> så eviction alltid är GOP-aligned
/// (filer blir spelbara). Audio lagras separat och matchas in vid flush.
/// </remarks>
public sealed class RingBufferClipBuffer : IClipBuffer
{
    private readonly LinkedList<GroupOfPictures> _videoGops = new();
    private readonly LinkedList<EncodedSample> _audioSamples = new();
    private readonly Lock _gate = new();
    private readonly Func<IMp4Writer> _writerFactory;
    private readonly RecordingSettings _settings;
    private readonly long _maxDurationTicks;
    private bool _disposed;

    /// <summary>
    /// Skapar en ny ring buffer.
    /// </summary>
    /// <param name="writerFactory">Skapar en ny <see cref="IMp4Writer"/> per flush. Buffern äger inte writer:n.</param>
    /// <param name="settings">Inspelningsinställningar (används vid flush till MP4).</param>
    public RingBufferClipBuffer(Func<IMp4Writer> writerFactory, RecordingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(writerFactory);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        _writerFactory = writerFactory;
        _settings = settings;
        _maxDurationTicks = settings.RingBufferSeconds * 10_000_000L;
    }

    /// <inheritdoc/>
    public double BufferedSeconds
    {
        get
        {
            lock (_gate)
            {
                if (_videoGops.Count == 0) return 0;
                var first = _videoGops.First!.Value.StartTimestamp;
                var last = _videoGops.Last!.Value.EndTimestamp;
                return (last - first) / 10_000_000.0;
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask AppendAsync(EncodedSample sample, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (sample.Type == SampleType.Video)
            {
                AppendVideoLocked(sample);
                EvictOldVideoLocked();
            }
            else
            {
                _audioSamples.AddLast(sample);
                EvictOldAudioLocked();
            }
        }

        return ValueTask.CompletedTask;
    }

    private void AppendVideoLocked(EncodedSample sample)
    {
        if (sample.IsKeyframe)
        {
            _videoGops.AddLast(new GroupOfPictures(sample));
            return;
        }

        // Icke-keyframe: appenda till nuvarande GOP, eller droppa om vi inte
        // sett en keyframe än (vanligt vid uppstart).
        if (_videoGops.Last is null) return;
        _videoGops.Last.Value.Append(sample);
    }

    private void EvictOldVideoLocked()
    {
        if (_videoGops.Count <= 1) return;

        var latestEnd = _videoGops.Last!.Value.EndTimestamp;
        var cutoff = latestEnd - _maxDurationTicks;

        // Eviktera GOP:er vars hela innehåll är äldre än cutoff.
        // Behåll alltid minst 1 GOP så vi har en startpunkt.
        while (_videoGops.Count > 1 && _videoGops.First!.Value.EndTimestamp < cutoff)
        {
            _videoGops.RemoveFirst();
        }
    }

    private void EvictOldAudioLocked()
    {
        if (_audioSamples.Count == 0) return;

        var last = _audioSamples.Last!.Value;
        var latestEnd = last.Timestamp + last.Duration;
        var cutoff = latestEnd - _maxDurationTicks;

        while (_audioSamples.Count > 0 && _audioSamples.First!.Value.Timestamp < cutoff)
        {
            _audioSamples.RemoveFirst();
        }
    }

    /// <inheritdoc/>
    public async Task<double> FlushLastSecondsAsync(
        int seconds,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (seconds < 1) throw new ArgumentException("seconds must be at least 1.", nameof(seconds));
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("outputPath required.", nameof(outputPath));

        // Snapshot: kopiera ut alla relevanta samples under lock, släpp lock,
        // sen gör den långsamma MP4-skrivningen utan att blockera capture-tråden.
        List<EncodedSample> samplesToWrite;
        long startTimestamp;

        lock (_gate)
        {
            if (_videoGops.Count == 0) return 0;

            var latestEnd = _videoGops.Last!.Value.EndTimestamp;
            var requestedStart = latestEnd - seconds * 10_000_000L;

            // Hitta sista GOP som startar <= requestedStart (innehåller eller föregår startpunkten).
            var firstGopNode = _videoGops.First!;
            while (firstGopNode.Next is not null && firstGopNode.Next.Value.StartTimestamp <= requestedStart)
            {
                firstGopNode = firstGopNode.Next;
            }

            startTimestamp = firstGopNode.Value.StartTimestamp;

            samplesToWrite = new List<EncodedSample>();
            for (var node = firstGopNode; node is not null; node = node.Next)
            {
                samplesToWrite.AddRange(node.Value.Samples);
            }

            foreach (var audio in _audioSamples)
            {
                if (audio.Timestamp + audio.Duration > startTimestamp)
                {
                    samplesToWrite.Add(audio);
                }
            }
        }

        // Skrivning sker UTANFÖR lock — kan ta hundratals ms och får inte blockera capture.
        await using var writer = _writerFactory();
        await writer.InitializeAsync(outputPath, _settings, cancellationToken).ConfigureAwait(false);

        foreach (var sample in samplesToWrite.OrderBy(s => s.Timestamp))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteSampleAsync(sample, cancellationToken).ConfigureAwait(false);
        }

        await writer.FinalizeAsync(cancellationToken).ConfigureAwait(false);

        var endTimestamp = samplesToWrite.Max(s => s.Timestamp + s.Duration);
        return (endTimestamp - startTimestamp) / 10_000_000.0;
    }

    /// <inheritdoc/>
    public void Clear()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            _videoGops.Clear();
            _audioSamples.Clear();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        lock (_gate)
        {
            _videoGops.Clear();
            _audioSamples.Clear();
        }
        return ValueTask.CompletedTask;
    }
}
