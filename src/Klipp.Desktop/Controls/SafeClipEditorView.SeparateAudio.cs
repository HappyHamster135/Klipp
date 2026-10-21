using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml.Controls;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private readonly Dictionary<string, (long Length, long Modified, IReadOnlyList<MediaAudioStreamInfo> Streams)> _sourceAudioStreams = new(StringComparer.OrdinalIgnoreCase);

    private async Task<bool> SeparateProjectSourceAudioAsync(EditorProject project)
    {
        var changed = false;
        foreach (var clip in project.Clips.Where(clip => !clip.SourceAudioSeparated).ToList())
        {
            var streams = await GetSourceAudioStreamsAsync(clip.SourcePath);
            if (streams is null)
                continue;
            EditorSourceAudioService.SeparateClipAudioStreams(project, clip, streams);
            changed = true;
        }
        // Earlier projects already stored stream identity. Discover the role
        // from media metadata once, without changing their edits or names.
        foreach (var audio in project.AudioTracks.Where(audio => audio.SourceAudioRole.Length == 0 && audio.SourceClipId.Length > 0))
        {
            var streams = await GetSourceAudioStreamsAsync(audio.SourcePath);
            var role = streams?.FirstOrDefault(stream => stream.Index == audio.SourceAudioStreamIndex)?.CaptureRole;
            audio.SourceAudioRole = role switch
            {
                "microphone" => "microphone", "game audio" => "system",
                "discord" => "discord", "music" => "music", _ => string.Empty
            };
            changed |= audio.SourceAudioRole.Length > 0;
        }
        if (changed)
            project.Normalize();
        return changed;
    }

    private async Task<IReadOnlyList<MediaAudioStreamInfo>?> GetSourceAudioStreamsAsync(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            return null;
        var file = new FileInfo(sourcePath);
        long length, modified;
        try { length = file.Length; modified = file.LastWriteTimeUtc.Ticks; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        if (_sourceAudioStreams.TryGetValue(sourcePath, out var cached) && cached.Length == length && cached.Modified == modified)
            return cached.Streams;
        var info = await _mediaProbeService.GetMediaInfoAsync(sourcePath);
        if (info is null)
            return null;
        var streams = info.AudioStreams;
        _sourceAudioStreams[sourcePath] = (length, modified, streams);
        return streams;
    }

    private EditorProjectAudioTrack? GetSelectedVideoSourceAudio()
    {
        return GetSelectedVideoSourceAudioClips().FirstOrDefault();
    }

    private IReadOnlyList<EditorProjectAudioTrack> GetSelectedVideoSourceAudioClips()
    {
        var sourceClipId = LayerTimeline.GetSelectedMediaClip()?.Id ?? GetSelectedAudioTrack()?.SourceClipId;
        return string.IsNullOrEmpty(sourceClipId) ? [] : _project?.AudioTracks
            .Where(track => track.SourceClipId == sourceClipId).ToList() ?? [];
    }

    private void SelectVideoSourceAudio()
    {
        var clips = GetSelectedVideoSourceAudioClips();
        if (clips.Count == 0)
        {
            ProjectStatusText.Text = "This video has no source audio clip";
            return;
        }
        if (clips.Count == 1)
        {
            SelectAudioClipForAdjustment(clips[0]);
            return;
        }
        var menu = new MenuFlyout();
        foreach (var audio in clips)
        {
            var item = new MenuFlyoutItem { Text = audio.SourceAudioRole switch
                { "system" => "Game audio", "microphone" => "Microphone", "discord" => "Discord", "music" => "Music", _ => audio.Name } };
            item.Click += (_, _) => SelectAudioClipForAdjustment(audio);
            menu.Items.Add(item);
        }
        menu.ShowAt(DetachAudioButton);
    }

}
