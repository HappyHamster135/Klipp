using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Foundation.Collections;
using Windows.Media.Playback;

namespace Klipp.Desktop.Services;

/// <summary>
/// Keeps native players and their selected streams warm while applying the full
/// editor gain before the playback volume stage. One atomic value changes per
/// voice; moving a gain control never reopens media or seeks a decoder.
/// </summary>
public sealed class PreviewAudioGainEffect : IDisposable
{
    public const string RuntimeClassName = "Klipp.AudioEffects.Gain";
    private const string LibraryName = "Klipp.AudioEffects.dll";
    private static readonly ConditionalWeakTable<MediaPlayer, PreviewAudioGainEffect> Effects = new();
    private static int _attachmentFailed;
    private ulong _stateId;
    private double _gain = double.NaN;
    private bool _disposed;
    public bool IsAttached => _stateId != 0 && !_disposed;
    public static bool HasAttachmentFailure => Volatile.Read(ref _attachmentFailed) != 0;

    private PreviewAudioGainEffect(MediaPlayer player)
    {
        try
        {
            // Registration-free custom WinRT components require Windows 1903.
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)) throw new PlatformNotSupportedException();
            _stateId = CreateState();
            if (_stateId == 0) throw new InvalidOperationException("Could not create an audio preview gain state.");
            player.AddAudioEffect(RuntimeClassName, false, new PropertySet { ["StateId"] = _stateId });
        }
        catch (Exception exception)
        {
            if (_stateId != 0) { ReleaseState(_stateId); _stateId = 0; }
            Interlocked.Exchange(ref _attachmentFailed, 1);
            Debug.WriteLine($"[Klipp] Full audio preview gain unavailable: {exception.Message}");
        }
    }

    public static bool SetGain(MediaPlayer player, double gain)
    {
        ArgumentNullException.ThrowIfNull(player);
        var effect = Effects.GetValue(player, static current => new PreviewAudioGainEffect(current));
        gain = double.IsFinite(gain) ? Math.Clamp(gain, 0, 32) : 0;
        if (!effect.IsAttached)
        {
            player.Volume = Math.Clamp(gain, 0, 1);
            return false;
        }
        if (gain != effect._gain)
        {
            SetStateGain(effect._stateId, (float)gain);
            effect._gain = gain;
        }
        // Amplification belongs to the float DSP stage, not MediaPlayer.Volume
        // (which Windows constrains to 0..1). Keep mute as a separate control.
        if (player.Volume != 1) player.Volume = 1;
        return true;
    }

    public static bool TryGetStatistics(MediaPlayer player, out double inputPeak, out double outputPeak, out ulong processedSamples)
    {
        inputPeak = outputPeak = 0;
        processedSamples = 0;
        if (!Effects.TryGetValue(player, out var effect) || !effect.IsAttached ||
            !GetStateStatistics(effect._stateId, out var input, out var output, out processedSamples)) return false;
        inputPeak = input;
        outputPeak = output;
        return true;
    }

    public static void Release(MediaPlayer player)
    {
        if (!Effects.TryGetValue(player, out var effect)) return;
        Effects.Remove(player);
        effect.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_stateId != 0) { ReleaseState(_stateId); _stateId = 0; }
        GC.SuppressFinalize(this);
    }

    ~PreviewAudioGainEffect() { if (_stateId != 0) ReleaseState(_stateId); }

    [DllImport(LibraryName, EntryPoint = "KlippCreateAudioGainState", CallingConvention = CallingConvention.StdCall)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
    private static extern ulong CreateState();
    [DllImport(LibraryName, EntryPoint = "KlippSetAudioGain", CallingConvention = CallingConvention.StdCall)]
    private static extern void SetStateGain(ulong id, float gain);
    [DllImport(LibraryName, EntryPoint = "KlippGetAudioGainStats", CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStateStatistics(ulong id, out float input, out float output, out ulong samples);
    [DllImport(LibraryName, EntryPoint = "KlippReleaseAudioGainState", CallingConvention = CallingConvention.StdCall)]
    private static extern void ReleaseState(ulong id);
}
