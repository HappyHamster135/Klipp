using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Klipp.Encoding.Audio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Klipp.Desktop;

public sealed class CaptureAudioMeterRow(CaptureAudioSource source) : INotifyPropertyChanged
{
    public CaptureAudioSource Source { get; } = source;
    public string Name => Source switch
    {
        CaptureAudioSource.Microphone => "Microphone", CaptureAudioSource.Discord => "Discord",
        CaptureAudioSource.Music => "Music", _ => "Game / desktop"
    };
    private double _level;
    private string _status = "Silent";
    private string _brushKey = "KlippAccentBrush";
    private DateTimeOffset _clipUntil, _limitUntil;
    public double Level { get => _level; private set { if (Math.Abs(_level - value) < .25) return; _level = value; Changed(); } }
    public string Status { get => _status; private set { if (_status == value) return; _status = value; Changed(); } }
    public Brush LevelBrush => (Brush)Application.Current.Resources[_brushKey];
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(CaptureAudioLevel level, DateTimeOffset now)
    {
        if (level.IsClipping) _clipUntil = now.AddSeconds(1);
        if (level.LimiterActive) _limitUntil = now.AddSeconds(1);
        Level = Math.Clamp(level.Peak, 0, 1) * 100;
        Status = now < _clipUntil ? "Clipping" : now < _limitUntil ? "Limited"
            : level.Peak > .0001 ? $"{20 * Math.Log10(level.Peak):0} dB" : "Silent";
        var key = now < _clipUntil ? "KlippDangerBrush" : now < _limitUntil ? "KlippWarningBrush" : "KlippAccentBrush";
        if (_brushKey != key) { _brushKey = key; Changed(nameof(LevelBrush)); }
    }

    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
