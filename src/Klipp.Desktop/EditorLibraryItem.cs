using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace Klipp.Desktop;

public sealed class EditorLibraryItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnailSource;
    private bool _isFavorite;
    private string _tagsText = string.Empty;
    private string _collection = string.Empty;

    public string FilePath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ThumbnailPath { get; set; } = string.Empty;
    public string DurationLabel { get; set; } = string.Empty;
    public TimeSpan? Duration { get; set; }
    public bool ThumbnailLoadStarted { get; set; }
    public DateTime LastWriteTime { get; set; }
    public long FileSizeBytes { get; set; }

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

    public ImageSource? ThumbnailSource
    {
        get => _thumbnailSource;
        set => SetField(ref _thumbnailSource, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
