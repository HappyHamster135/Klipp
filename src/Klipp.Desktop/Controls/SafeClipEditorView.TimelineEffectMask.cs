using System;
using System.Numerics;
using Klipp.Core.Editing;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private sealed class TimelineEffectMask : IDisposable
    {
        private readonly CanvasDevice _device;
        private readonly int _width;
        private readonly int _height;
        private readonly CanvasLinearGradientBrush _horizontal;
        private readonly CanvasLinearGradientBrush _vertical;
        private readonly CanvasGeometry _horizontalCorner;
        private readonly CanvasGeometry _verticalCorner;
        private CanvasRadialGradientBrush? _ellipse;
        private double _ellipseFeather = -1;
        private EditorEffectRegion? _lastRegion;
        private string? _lastShape;
        private double _lastFeather;
        public CanvasRenderTarget Surface { get; }

        public TimelineEffectMask(CanvasDevice device, int width, int height)
        {
            _device = device; _width = width; _height = height;
            Surface = new CanvasRenderTarget(device, width, height, 96,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
            var transparentWhite = Windows.UI.Color.FromArgb(0, 255, 255, 255);
            _horizontal = new CanvasLinearGradientBrush(device, transparentWhite, Microsoft.UI.Colors.White)
                { StartPoint = Vector2.Zero, EndPoint = Vector2.UnitX };
            _vertical = new CanvasLinearGradientBrush(device, transparentWhite, Microsoft.UI.Colors.White)
                { StartPoint = Vector2.Zero, EndPoint = Vector2.UnitY };
            _horizontalCorner = CanvasGeometry.CreatePolygon(device, [Vector2.Zero, Vector2.UnitY, Vector2.One]);
            _verticalCorner = CanvasGeometry.CreatePolygon(device, [Vector2.Zero, Vector2.UnitX, Vector2.One]);
        }

        public void Update(EditorEffectRegion region, string shape, double feather)
        {
            region = EditorEffectRegionMath.Normalize(region);
            feather = Math.Clamp(feather, 0, 1);
            if (_lastRegion == region && _lastShape == shape && _lastFeather == feather) return;
            var rect = new Rect(region.X * _width, region.Y * _height,
                region.Width * _width, region.Height * _height);
            using var drawing = Surface.CreateDrawingSession();
            drawing.Clear(Microsoft.UI.Colors.Transparent);
            drawing.Antialiasing = CanvasAntialiasing.Aliased;
            if (shape == "ellipse")
            {
                if (feather <= .00001)
                    drawing.FillEllipse((float)(rect.X + rect.Width / 2), (float)(rect.Y + rect.Height / 2),
                        (float)(rect.Width / 2), (float)(rect.Height / 2), Microsoft.UI.Colors.White);
                else
                {
                    if (_ellipse is null || _ellipseFeather != feather)
                    {
                        _ellipse?.Dispose();
                        _ellipse = new CanvasRadialGradientBrush(_device,
                        [
                            new CanvasGradientStop { Position = 0, Color = Microsoft.UI.Colors.White },
                            new CanvasGradientStop { Position = (float)(1 - feather), Color = Microsoft.UI.Colors.White },
                            new CanvasGradientStop { Position = 1, Color = Windows.UI.Color.FromArgb(0, 255, 255, 255) }
                        ]);
                        _ellipseFeather = feather;
                    }
                    _ellipse.Center = new Vector2((float)(rect.X + rect.Width / 2), (float)(rect.Y + rect.Height / 2));
                    _ellipse.RadiusX = (float)(rect.Width / 2);
                    _ellipse.RadiusY = (float)(rect.Height / 2);
                    drawing.FillEllipse(_ellipse.Center, _ellipse.RadiusX, _ellipse.RadiusY, _ellipse);
                }
            }
            else if (feather <= .00001)
            {
                drawing.FillRectangle(rect, Microsoft.UI.Colors.White);
            }
            else
            {
                // min(distance to each edge) is piecewise linear. Draw its edge
                // bands and corner triangles directly, with no blur outside the
                // region and no CPU mask upload while a handle is moving.
                var f = (float)(Math.Min(rect.Width, rect.Height) * feather / 2);
                var x = (float)rect.X; var y = (float)rect.Y;
                var right = (float)rect.Right; var bottom = (float)rect.Bottom;
                var innerWidth = Math.Max(0, (float)rect.Width - 2 * f);
                var innerHeight = Math.Max(0, (float)rect.Height - 2 * f);
                drawing.Antialiasing = CanvasAntialiasing.Aliased;
                drawing.Blend = CanvasBlend.Copy;
                if (innerWidth > 0 && innerHeight > 0)
                    drawing.FillRectangle(x + f, y + f, innerWidth, innerHeight, Microsoft.UI.Colors.White);
                void Band(float left, float top, float width, float height, CanvasLinearGradientBrush brush)
                {
                    if (width == 0 || height == 0) return;
                    drawing.Transform = Matrix3x2.CreateScale(width, height) * Matrix3x2.CreateTranslation(left, top);
                    drawing.FillRectangle(0, 0, 1, 1, brush);
                }
                Band(x, y + f, f, innerHeight, _horizontal);
                Band(right, y + f, -f, innerHeight, _horizontal);
                Band(x + f, y, innerWidth, f, _vertical);
                Band(x + f, bottom, innerWidth, -f, _vertical);
                void Corner(float left, float top, float sx, float sy)
                {
                    drawing.Transform = Matrix3x2.CreateScale(sx * f, sy * f) * Matrix3x2.CreateTranslation(left, top);
                    drawing.FillGeometry(_horizontalCorner, _horizontal);
                    drawing.FillGeometry(_verticalCorner, _vertical);
                }
                Corner(x, y, 1, 1); Corner(right, y, -1, 1);
                Corner(x, bottom, 1, -1); Corner(right, bottom, -1, -1);
                drawing.Transform = Matrix3x2.Identity;
            }
            _lastRegion = region; _lastShape = shape; _lastFeather = feather;
        }

        public void Dispose()
        {
            _ellipse?.Dispose(); _horizontal.Dispose(); _vertical.Dispose();
            _horizontalCorner.Dispose(); _verticalCorner.Dispose(); Surface.Dispose();
        }
    }
}
