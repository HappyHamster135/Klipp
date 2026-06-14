using Klipp.Core.Enums;

namespace Klipp.Core.Models;

/// <summary>
/// En enskild video-frame med pixel-data och metadata om när den capturades.
/// Pixel-formatet anges av <see cref="Format"/> — vanligtvis BGRA8 (4 bytes per pixel).
/// </summary>
/// <param name="Width">Bredd i pixlar.</param>
/// <param name="Height">Höjd i pixlar.</param>
/// <param name="Format">Pixelformat (BGRA8, NV12, osv).</param>
/// <param name="Timestamp">När framen capturades, i 100-nanosekundsenheter (Media Foundation-standard).</param>
/// <param name="PixelData">Tightly-packed pixel-bytes (ingen rad-padding). Längd = Width * Height * BytesPerPixel.</param>
public readonly record struct VideoFrame(
    int Width,
    int Height,
    PixelFormat Format,
    long Timestamp,
    ReadOnlyMemory<byte> PixelData);
