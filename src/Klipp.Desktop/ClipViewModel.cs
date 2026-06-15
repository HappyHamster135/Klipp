using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Klipp.Desktop;

/// <summary>
/// View model för ett klipp i biblioteket. Implementerar INotifyPropertyChanged
/// så UI uppdateras automatiskt när properties ändras.
/// </summary>
public sealed class ClipViewModel : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _meta = string.Empty;
    private string _duration = string.Empty;
    private string _filePath = string.Empty;
    private bool _isNew;

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string Meta
    {
        get => _meta;
        set => SetField(ref _meta, value);
    }

    public string Duration
    {
        get => _duration;
        set => SetField(ref _duration, value);
    }

    /// <summary>Full sökväg till klippets MP4-fil på disk.</summary>
    public string FilePath
    {
        get => _filePath;
        set => SetField(ref _filePath, value);
    }

    public bool IsNew
    {
        get => _isNew;
        set => SetField(ref _isNew, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
