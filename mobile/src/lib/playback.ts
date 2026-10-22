type FullscreenVideo = HTMLVideoElement & { webkitEnterFullscreen?: () => void };

// Safari's native video fullscreen can remain available when document fullscreen is not.
export async function enterVideoFullscreen(video: FullscreenVideo): Promise<void> {
  if (video.requestFullscreen && document.fullscreenEnabled) {
    try { await video.requestFullscreen(); return; }
    catch (error) { if (!video.webkitEnterFullscreen) throw error; }
  }
  if (video.webkitEnterFullscreen) { video.webkitEnterFullscreen(); return; }
  if (video.requestFullscreen) { await video.requestFullscreen(); return; }
  throw new Error('Fullscreen is unavailable in this browser.');
}

export interface PlaybackPosition {
  time: number;
  playing: boolean;
  muted: boolean;
  volume: number;
  rate: number;
}

export function rememberPlayback(video: HTMLVideoElement): PlaybackPosition {
  return {
    time: Number.isFinite(video.currentTime) ? video.currentTime : 0,
    playing: !video.paused && !video.ended,
    muted: video.muted,
    volume: video.volume,
    rate: video.playbackRate,
  };
}

export function restorePlayback(video: HTMLVideoElement, position: PlaybackPosition): boolean {
  if (!Number.isFinite(video.duration) || video.readyState < 3 || video.seeking) return false;
  try {
    video.muted = position.muted;
    video.volume = position.volume;
    video.playbackRate = position.rate;
    // Finish seeking before resuming. Some WebKit players reset an early seek
    // to the first decoded frame, which the caller verifies at seeked.
    video.pause();
    video.currentTime = Math.max(0, Math.min(position.time, Math.max(0, video.duration - 0.01)));
    return true;
  } catch { return false; }
}
