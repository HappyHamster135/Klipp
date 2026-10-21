using System;
using System.Collections.Generic;
using System.IO;
using Klipp.Core.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Graphics.DirectX;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private static readonly Dictionary<string, byte[]> PreviewLutTables = new(StringComparer.OrdinalIgnoreCase);

    private static EffectTransferTable3D? CreatePreviewLutTable(CanvasDevice device, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        var file = new FileInfo(path);
        var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        if (!PreviewLutTables.TryGetValue(key, out var bytes))
        {
            bytes = CubeLutData.Parse(File.ReadLines(path)).BuildBlueFastTransferTable();
            if (PreviewLutTables.Count >= 8)
                PreviewLutTables.Clear();
            PreviewLutTables[key] = bytes;
        }
        return EffectTransferTable3D.CreateFromBytes(device, bytes, 65, 65, 65, DirectXPixelFormat.R32G32B32A32Float);
    }
}
