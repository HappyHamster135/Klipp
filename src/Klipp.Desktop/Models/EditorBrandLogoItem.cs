using System;
using Klipp.Core.Branding;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Klipp.Desktop.Models;

public sealed class EditorBrandLogoItem
{
    public EditorBrandLogoItem(EditorBrandLogo logo)
    {
        Logo = logo.Clone();
        Preview = new BitmapImage
        {
            DecodePixelWidth = 96,
            UriSource = new Uri(Logo.StoredPath, UriKind.Absolute)
        };
    }

    public EditorBrandLogo Logo { get; }
    public string Id => Logo.Id;
    public string Name => Logo.Name;
    public string StoredPath => Logo.StoredPath;
    public BitmapImage Preview { get; }
}
