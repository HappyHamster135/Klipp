using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
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
    private string _thumbnailPath = string.Empty;
    private bool _isNew;
    private bool _isFavorite;
    private string _tagsText = string.Empty;
    private string _collection = string.Empty;
    private long _fileSizeBytes;
    private System.DateTime _lastWriteTime;

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
    /// <summary>Sökväg till thumbnail-bilden. Tom om ingen genererats än.</summary>
    public string ThumbnailPath
    {
        get => _thumbnailPath;
        set => SetField(ref _thumbnailPath, value);
    }
    public bool IsNew
    {
        get => _isNew;
        set => SetField(ref _isNew, value);
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetField(ref _isFavorite, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FavoriteGlyph)));
        }
    }

    public string FavoriteGlyph => IsFavorite ? "\uE735" : "\uE734";

    public string TagsText
    {
        get => _tagsText;
        set
        {
            if (SetField(ref _tagsText, value ?? string.Empty))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OrganizationText)));
        }
    }

    public string Collection
    {
        get => _collection;
        set
        {
            if (SetField(ref _collection, value ?? string.Empty))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OrganizationText)));
        }
    }

    public string OrganizationText => string.Join(
        "  •  ",
        new[] { Collection, TagsText }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public long FileSizeBytes
    {
        get => _fileSizeBytes;
        set => SetField(ref _fileSizeBytes, value);
    }

    public System.DateTime LastWriteTime
    {
        get => _lastWriteTime;
        set => SetField(ref _lastWriteTime, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
