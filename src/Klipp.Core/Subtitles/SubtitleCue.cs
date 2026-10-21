using System;

namespace Klipp.Core.Subtitles;

public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);
