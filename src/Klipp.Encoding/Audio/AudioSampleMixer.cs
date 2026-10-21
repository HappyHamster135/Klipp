using NAudio.Wave;
using System.Diagnostics;

namespace Klipp.Encoding.Audio;

/// <summary>Mixes stereo inputs, or retains clock-aligned stereo pairs for independent editing.</summary>
public sealed class AudioSampleMixer
{
    private readonly (ISampleProvider Provider, float Gain)[] _inputs;
    private float[] _scratch = [];
    private readonly bool _separateSources;
    private readonly bool _limiterEnabled;
    private readonly float[] _limiterGains;
    private readonly float[] _peaks;
    private readonly long[] _clipTimes;
    private readonly long[] _limiterTimes;
    public int OutputChannels { get; }

    public AudioSampleMixer(IEnumerable<(ISampleProvider Provider, double Gain)> inputs, bool separateSources = false, bool limiterEnabled = false)
    {
        _inputs = inputs.Select(input =>
        {
            if (input.Provider.WaveFormat.SampleRate != 48000 || input.Provider.WaveFormat.Channels != 2)
                throw new ArgumentException("Mixer inputs must be 48 kHz stereo.", nameof(inputs));
            return (input.Provider, CaptureAudioOptions.SafeGain(input.Gain));
        }).ToArray();
        if (_inputs.Length > 4) throw new ArgumentException("Capture supports up to four independent sources.", nameof(inputs));
        _separateSources = separateSources;
        _limiterEnabled = limiterEnabled;
        OutputChannels = separateSources ? Math.Max(1, _inputs.Length) * 2 : 2;
        _limiterGains = Enumerable.Repeat(1f, _inputs.Length).ToArray();
        _peaks = new float[_inputs.Length];
        _clipTimes = new long[_inputs.Length];
        _limiterTimes = new long[_inputs.Length];
    }

    public void Read(float[] output, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > output.Length || count % OutputChannels != 0) throw new ArgumentOutOfRangeException(nameof(count));
        Array.Clear(output, 0, count);
        var inputCount = count / OutputChannels * 2;
        if (_scratch.Length < inputCount) _scratch = new float[inputCount];
        for (var source = 0; source < _inputs.Length; source++)
        {
            var input = _inputs[source];
            var read = input.Provider.Read(_scratch, 0, inputCount);
            var peak = 0f;
            for (var index = 0; index < read; index++)
                if (float.IsFinite(_scratch[index])) peak = Math.Max(peak, Math.Abs(_scratch[index] * input.Gain));
            var now = Stopwatch.GetTimestamp();
            Volatile.Write(ref _peaks[source], peak);
            if (peak >= 0.999f) Interlocked.Exchange(ref _clipTimes[source], now);
            var ceilingGain = peak > .98f ? .98f / peak : 1f;
            var release = 1f - MathF.Exp(-inputCount / 2f / 48000f / .08f);
            var limiterGain = _limiterEnabled ? Math.Min(ceilingGain, _limiterGains[source] + (1 - _limiterGains[source]) * release) : 1f;
            _limiterGains[source] = limiterGain;
            if (_limiterEnabled && limiterGain < .999f) Interlocked.Exchange(ref _limiterTimes[source], now);
            for (var index = 0; index < read; index++)
            {
                var sample = _scratch[index];
                var destination = _separateSources ? index / 2 * OutputChannels + source * 2 + index % 2 : index;
                if (float.IsFinite(sample)) output[destination] += sample * input.Gain * limiterGain;
            }
        }
        for (var index = 0; index < count; index++)
            output[index] = Math.Clamp(output[index], -1, 1);
    }

    internal (double Peak, bool IsClipping, bool LimiterActive) GetLevel(int source)
    {
        var now = Stopwatch.GetTimestamp();
        return (Math.Clamp(Volatile.Read(ref _peaks[source]), 0, 2),
            (now - Interlocked.Read(ref _clipTimes[source])) / (double)Stopwatch.Frequency < 1.5,
            (now - Interlocked.Read(ref _limiterTimes[source])) / (double)Stopwatch.Frequency < .5);
    }
}
