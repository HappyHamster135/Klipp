using Klipp.Core.Enums;
using Klipp.Core.Models;

namespace Klipp.Storage.RingBuffer;

/// <summary>
/// En grupp av video-frames som börjar med en keyframe och sträcker sig till
/// nästa keyframe (exklusivt). Detta är den minsta enheten som ring buffern
/// kan eviktera — en GOP är spelbar i sig själv, men individuella frames
/// inom den beror på sina grannar.
/// </summary>
/// <remarks>
/// Klassen är INTE tråd-säker. Anroparen (RingBufferClipBuffer) ansvarar
/// för synchronisering.
/// </remarks>
internal sealed class GroupOfPictures
{
    private readonly List<EncodedSample> _samples = new();

    /// <summary>Tidsstämpel för första (keyframe) samplet, i 100-ns enheter.</summary>
    public long StartTimestamp { get; }

    /// <summary>Tidsstämpel där sista samplet slutar (start + duration).</summary>
    public long EndTimestamp { get; private set; }

    /// <summary>Antal samples i GOP:en.</summary>
    public int Count => _samples.Count;

    /// <summary>Längd i 100-ns enheter.</summary>
    public long Duration => EndTimestamp - StartTimestamp;

    /// <summary>Total byte-storlek (för att beräkna minnesförbrukning).</summary>
    public long TotalBytes { get; private set; }

    /// <summary>Samples i tidsordning, read-only.</summary>
    public IReadOnlyList<EncodedSample> Samples => _samples;

    /// <summary>Skapar en ny GOP. Första samplet måste vara en keyframe.</summary>
    public GroupOfPictures(EncodedSample keyframe)
    {
        if (keyframe.Type != SampleType.Video)
            throw new ArgumentException("GOP startar alltid med en video-keyframe.", nameof(keyframe));
        if (!keyframe.IsKeyframe)
            throw new ArgumentException("Första samplet i en GOP måste vara en keyframe.", nameof(keyframe));

        _samples.Add(keyframe);
        StartTimestamp = keyframe.Timestamp;
        EndTimestamp = keyframe.Timestamp + keyframe.Duration;
        TotalBytes = keyframe.Data.Length;
    }

    /// <summary>Lägger till en P- eller B-frame i GOP:en.</summary>
    public void Append(EncodedSample sample)
    {
        if (sample.Type != SampleType.Video)
            throw new ArgumentException("GOP innehåller bara video-samples.", nameof(sample));
        if (sample.IsKeyframe)
            throw new ArgumentException("Keyframes startar nya GOP:er, kan inte appendas.", nameof(sample));

        _samples.Add(sample);
        EndTimestamp = sample.Timestamp + sample.Duration;
        TotalBytes += sample.Data.Length;
    }
}
