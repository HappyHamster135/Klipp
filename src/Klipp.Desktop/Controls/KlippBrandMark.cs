using Klipp.Core.Branding;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Klipp.Desktop.Controls;

/// <summary>The split-K mark, shared with the application icon and notification renderer.</summary>
public sealed class KlippBrandMark : UserControl
{
    private readonly Polygon[] _shapes;

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(KlippBrandMark),
        new PropertyMetadata(null, OnFillChanged));

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public KlippBrandMark()
    {
        IsHitTestVisible = false;
        var canvas = new Canvas { Width = KlippBrandGeometry.Size, Height = KlippBrandGeometry.Size };
        _shapes = new Polygon[KlippBrandGeometry.Polygons.Length];
        for (var index = 0; index < _shapes.Length; index++)
        {
            var shape = new Polygon();
            foreach (var point in KlippBrandGeometry.Polygons[index]) shape.Points.Add(new Point(point.X, point.Y));
            _shapes[index] = shape;
            canvas.Children.Add(shape);
        }
        Content = new Viewbox { Stretch = Stretch.Uniform, Child = canvas };
        UpdateFill();
    }

    private static void OnFillChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((KlippBrandMark)sender).UpdateFill();

    private void UpdateFill()
    {
        var brush = Fill ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 143, 175, 210));
        foreach (var shape in _shapes) shape.Fill = brush;
    }
}
