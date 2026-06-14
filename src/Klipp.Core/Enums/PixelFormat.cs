namespace Klipp.Core.Enums;

/// <summary>
/// Pixelformat för raw video-frames innan kodning.
/// Värdena matchar DXGI_FORMAT där det är möjligt.
/// </summary>
public enum PixelFormat
{
    /// <summary>32-bit BGRA, 8 bits per kanal. Default för Windows Graphics Capture.</summary>
    Bgra8,

    /// <summary>NV12 — 12 bits per pixel, YUV 4:2:0. Native format för de flesta hårdvaru-encoders.</summary>
    Nv12,

    /// <summary>RGB10A2 — 10-bit per färgkanal, för HDR-innehåll.</summary>
    Rgb10A2
}
