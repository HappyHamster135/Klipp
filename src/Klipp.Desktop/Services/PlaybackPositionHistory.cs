using System;
using System.IO;
using Klipp.Core.Playback;

namespace Klipp.Desktop.Services;

internal static class PlaybackPositionHistory
{
    internal static PlaybackPositionStore Shared { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klipp", "playback-positions.json"));
}
