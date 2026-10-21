using System;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;
using Klipp.Desktop.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.DirectX;
using Windows.Media.Playback;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    // MediaPlayerElement is external composition content: sampling its visual
    // returns transparent pixels. Frame server mode supplies the decoded GPU
    // surface instead, without a CPU readback or an additional video decoder.
    private sealed partial class ClipEffectPreviewResources : IDisposable
    {
        private readonly MediaPlayer _player;
        private readonly DispatcherQueue _dispatcher;
        private readonly Action<string, Exception> _failed;
        private readonly CanvasDevice _device = CanvasDevice.GetSharedDevice();
        private readonly Image _image;
        private CanvasRenderTarget? _frame;
        private CanvasImageSource? _destination;
        private ICanvasImage? _effect;
        private ICanvasImage? _graded;
        private ArithmeticCompositeEffect? _clipBlend;
        private IReadOnlyList<EditorProjectEffectLayer> _timelineEffects;
        private readonly List<TimelineFrameEffectBlend> _timelineBlends = [];
        private EffectTransferTable3D? _lutTable;
        private EditorProjectClip _clip;
        private bool _hasClipGrade;
        private double _strength;
        private double _timelineSeconds;
        private bool _bypassTimelineEffects;
        private int _queued;
        private int _copyFrameRequested = 1;
        private bool _hasDecodedFrame;
        private bool _rebuildGraph = true;
        private bool _disposed;
#if FX_PREVIEW_HARNESS
        private int _frameCopyCount;
#endif
        public string Signature { get; private set; }

        public bool UsesPlayer(MediaPlayer player) => ReferenceEquals(_player, player);


        public ClipEffectPreviewResources(MediaPlayer player, Grid host, EditorProjectClip clip,
            double strength, IReadOnlyList<EditorProjectEffectLayer> timelineEffects, double timelineSeconds,
            bool bypassTimelineEffects, string signature, DispatcherQueue dispatcher, Action<string, Exception> failed)
        {
            _player = player;
            _clip = clip;
            _hasClipGrade = HasGpuPreviewEffects(clip);
            _strength = strength;
            _timelineEffects = timelineEffects;
            _timelineSeconds = timelineSeconds;
            _bypassTimelineEffects = bypassTimelineEffects;
            Signature = signature;
            _dispatcher = dispatcher;
            _failed = failed;
            _image = new Image { Stretch = Stretch.Uniform, IsHitTestVisible = false };
            host.Children.Add(_image);
            ApplySourceContentTransform(host, _image);
            _player.VideoFrameAvailable += OnVideoFrameAvailable;
            _player.PlaybackSession.SeekCompleted += OnSeekCompleted;
            _player.MediaOpened += OnMediaOpened;
            try
            {
                QueueDraw(copyVideoFrame: true);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Update(EditorProjectClip clip, double strength, IReadOnlyList<EditorProjectEffectLayer> timelineEffects,
            double timelineSeconds, bool bypassTimelineEffects, string signature)
        {
            _clip = clip;
            _hasClipGrade = HasGpuPreviewEffects(clip);
            _rebuildGraph |= Signature != signature;
            Signature = signature;
            _timelineEffects = timelineEffects;
            // Timeline snapshots replace their layer instances while dragging.
            // Always accept the current data, even if time has not advanced.
            var timelineChanged = timelineEffects.Count > 0 || _bypassTimelineEffects != bypassTimelineEffects;
            _timelineSeconds = timelineSeconds;
            _bypassTimelineEffects = bypassTimelineEffects;
            if (!_rebuildGraph && !timelineChanged && Math.Abs(_strength - strength) < 0.0001 && _effect is not null)
                return;
            _strength = strength;
            QueueDraw();
        }

        private void OnVideoFrameAvailable(MediaPlayer sender, object args) => QueueDraw(copyVideoFrame: true);
        private void OnSeekCompleted(MediaPlaybackSession sender, object args) => QueueDraw(copyVideoFrame: true);
        private void OnMediaOpened(MediaPlayer sender, object args) => QueueDraw(copyVideoFrame: true);

        private void QueueDraw(bool copyVideoFrame = false)
        {
            if (copyVideoFrame) Interlocked.Exchange(ref _copyFrameRequested, 1);
            if (_disposed || Interlocked.Exchange(ref _queued, 1) != 0)
                return;
            if (!_dispatcher.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref _queued, 0);
                if (_disposed)
                    return;
                try { DrawFrame(); }
                catch (Exception exception) { _failed(Signature, exception); }
            }))
                Interlocked.Exchange(ref _queued, 0);
        }

        private void DrawFrame()
        {
            var videoWidth = _player.PlaybackSession.NaturalVideoWidth;
            var videoHeight = _player.PlaybackSession.NaturalVideoHeight;
            if (videoWidth == 0 || videoHeight == 0 || _player.PlaybackSession.PlaybackState is
                MediaPlaybackState.None or MediaPlaybackState.Opening or MediaPlaybackState.Buffering)
                return; // MediaOpened/new-frame notification will retry.
            if (!_player.IsVideoFrameServerEnabled)
                throw new InvalidOperationException("The preview decoder is using its native output fallback.");
            var scale = Math.Min(1, Math.Min(1280.0 / videoWidth, 720.0 / videoHeight));
            var width = Math.Max(1, (int)Math.Round(videoWidth * scale));
            var height = Math.Max(1, (int)Math.Round(videoHeight * scale));
            if (_frame is null || _frame.SizeInPixels.Width != width || _frame.SizeInPixels.Height != height)
            {
                ClearEffectGraph();
                _frame?.Dispose();
                _frame = new CanvasRenderTarget(_device, width, height, 96,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
                _destination = new CanvasImageSource(_device, width, height, 96);
                _hasDecodedFrame = false;
                _rebuildGraph = true;
            }
            if (Interlocked.Exchange(ref _copyFrameRequested, 0) != 0 || !_hasDecodedFrame)
            {
                try
                {
                    _player.CopyFrameToVideoSurface(_frame);
#if FX_PREVIEW_HARNESS
                    _frameCopyCount++;
#endif
                    _hasDecodedFrame = true;
                }
                catch (COMException exception) when (exception.HResult is unchecked((int)0xC00D36B2) or unchecked((int)0xC00D6D72))
                {
                    Interlocked.Exchange(ref _copyFrameRequested, 1);
                    // Keep the last copied frame and image visible during an
                    // opening/seek gap; a mask edit can still redraw that frame.
                    if (!_hasDecodedFrame) return;
                }
            }
            if (_effect is null || _rebuildGraph)
            {
                ICanvasImage? graded = null;
                ArithmeticCompositeEffect? clipBlend = null;
                EffectTransferTable3D? lut = null;
                var timelineBlends = new List<TimelineFrameEffectBlend>();
                try
                {
                    ICanvasImage source = _frame;
                    if (_hasClipGrade)
                    {
                        lut = CreatePreviewLutTable(_device, _clip.LutPath);
                        graded = BuildFrameEffectGraph(_clip, _frame, 1, lut);
                        clipBlend = new ArithmeticCompositeEffect
                        {
                            Source1 = _frame, Source2 = graded, MultiplyAmount = 0, Offset = 0, ClampOutput = true
                        };
                        source = clipBlend;
                    }
                    var effect = BuildTimelineFrameEffects(source, _timelineEffects, width, height, timelineBlends);
                    ClearEffectGraph();
                    _graded = graded; _clipBlend = clipBlend; _lutTable = lut;
                    _timelineBlends.AddRange(timelineBlends);
                    _effect = effect;
                    _rebuildGraph = false;
                }
                catch
                {
                    for (var index = timelineBlends.Count - 1; index >= 0; index--) timelineBlends[index].Dispose();
                    clipBlend?.Dispose(); (graded as IDisposable)?.Dispose(); lut?.Dispose();
                    throw;
                }
            }
            if (_clipBlend is not null)
            {
                _clipBlend.Source1Amount = (float)(1 - Math.Clamp(_strength, 0, 1));
                _clipBlend.Source2Amount = (float)Math.Clamp(_strength, 0, 1);
            }
            for (var index = 0; index < _timelineBlends.Count; index++)
            {
                var effect = _timelineBlends[index];
                effect.UpdateLayer(_timelineEffects[index], _timelineSeconds);
                var amount = !_bypassTimelineEffects && effect.Layer.IsActiveAt(_timelineSeconds)
                    ? Math.Clamp(effect.Layer.Amount, 0, 1) : 0;
                effect.SetAmount(amount);
            }
            using (var drawing = _destination!.CreateDrawingSession(Microsoft.UI.Colors.Transparent))
            {
                if (_device.IsBufferPrecisionSupported(CanvasBufferPrecision.Precision16Float))
                    drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                drawing.DrawImage(_effect);
            }
            if (!ReferenceEquals(_image.Source, _destination)) _image.Source = _destination;

        }

        private void ClearEffectGraph()
        {
            _effect = null;
            for (var index = _timelineBlends.Count - 1; index >= 0; index--)
                _timelineBlends[index].Dispose();
            _timelineBlends.Clear();
            _clipBlend?.Dispose();
            _clipBlend = null;
            (_graded as IDisposable)?.Dispose();
            _graded = null;
            _lutTable?.Dispose(); _lutTable = null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _player.VideoFrameAvailable -= OnVideoFrameAvailable;
            _player.PlaybackSession.SeekCompleted -= OnSeekCompleted;
            _player.MediaOpened -= OnMediaOpened;
            // Frame-server mode belongs to the decoder's lifetime, rather than
            // this effect graph. Rebuilding FX must not change its color path.
            _image.Source = null;
            ClearEffectGraph();
            _frame?.Dispose();
            _destination = null;
        }
    }

    private static void RestoreNativePreviewAfterFrameFailure(MediaPlayer player)
    {
        try { player.IsVideoFrameServerEnabled = false; } catch { }
    }

    private static bool KeepNativePreviewFallback(MediaPlayer player, string signature, ref string? currentSignature)
    {
        if (player.IsVideoFrameServerEnabled) return false;
        // Switching this decoder back after it has opened changes its RGB range.
        // A new look keeps native output; source reload enables frame server
        // before opening and can safely retry the GPU renderer.
        currentSignature = "fallback|" + signature;
        return true;
    }
}
