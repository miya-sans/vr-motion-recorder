using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using VRMR.Render;

namespace VRMR.RenderDemo
{
    /// <summary>
    ///     A window with nothing but the 3D view in it, playing a take. Keys: Space play / pause, Left / Right one frame,
    ///     Home first frame, M mesh, K skeleton, D devices, H headset, P play area, B base stations, L live devices,
    ///     F frame all, G placement gizmo, A multisampling on / off.
    /// </summary>
    sealed class DemoWindow : Window
    {
        public readonly ViewportControl View = new();
        readonly PlacementTool _gizmo;
        readonly LiveDevices _live = new();
        readonly TakeSource _source;
        bool _playing, _liveDevices;
        double _time, _liveTime;

        public DemoWindow(TakeSource source, DemoOptions options)
        {
            _source = source;
            Title = "VR Motion Recorder: render demo";
            Width = 960;
            Height = 600;
            ShowActivated = !options.Quiet;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(40, 40);
            Content = View;
            View.Msaa = options.Msaa;
            View.FrameTick += OnFrameTick;
            _gizmo = new PlacementTool(source, ShowFrame, () => View.Tool = null);
            Opened += (_, _) =>
            {
                if (options.ViewPixels is var (w, h))
                {
                    // the view is the window's whole client area: size it so the view has exactly these device pixels
                    Width = w / RenderScaling;
                    Height = h / RenderScaling;
                }

                ShowFrame();
                View.FrameAll();
                View.Focus();
            };
        }

        /// <summary>The take's frame under the playhead.</summary>
        public int FrameIndex => (int)(_time * _source.Fps) % Math.Max(1, _source.Frames);

        public bool Playing
        {
            get => _playing;
            set
            {
                _playing = value;
                UpdateAnimating();
            }
        }

        /// <summary>Show simulated SteamVR devices that keep moving, instead of the take's own.</summary>
        public bool LiveDevices
        {
            get => _liveDevices;
            set
            {
                _liveDevices = value;
                UpdateAnimating();
                ShowFrame();
            }
        }

        void UpdateAnimating()
        {
            View.Animating = _playing || _liveDevices;
        }

        /// <summary>Moves the playhead to a frame and shows it.</summary>
        public void Seek(int frame)
        {
            _time = Math.Max(0, frame) / (double)_source.Fps;
            ShowFrame();
        }

        void OnFrameTick(TimeSpan elapsed)
        {
            if (_playing) _time += elapsed.TotalSeconds;
            if (_liveDevices) _liveTime += elapsed.TotalSeconds;
            ShowFrame();
        }

        /// <summary>Fills a frame from the take at the playhead and hands it to the view. Nothing here allocates.</summary>
        public void ShowFrame()
        {
            var f = View.RentFrame();
            int frame = FrameIndex;
            _source.Fill(f, frame, !_liveDevices);
            if (_liveDevices) _live.Fill(f, _liveTime);
            var stats = View.Stats;
            f.Overlay.Append(_liveDevices ? "LIVE (simulated)   " : "").Append(_source.Source).Append("   frame ")
                .Append(frame).Append('/').Append(_source.Frames - 1).Append('\n');
            f.Overlay.Append(
                $"drawn {stats.FramesDrawn}   {stats.LastFrameCpuMs:0.00} ms   {stats.LastFrameAllocatedBytes} B   {stats.Triangles} triangles");
            f.OverlayRight.Append(_source.Name);
            View.Submit(f);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled) return;
            e.Handled = true;
            switch (e.Key)
            {
                case Key.Space: Playing = !Playing; break;
                case Key.Left: Seek(FrameIndex - 1); break;
                case Key.Right: Seek(FrameIndex + 1); break;
                case Key.Home: Seek(0); break;
                case Key.M: View.ShowMesh = !View.ShowMesh; break;
                case Key.K: View.ShowSkeleton = !View.ShowSkeleton; break;
                case Key.D: View.ShowDevices = !View.ShowDevices; break;
                case Key.H: View.ShowHeadset = !View.ShowHeadset; break;
                case Key.P: View.ShowPlayArea = !View.ShowPlayArea; break;
                case Key.B: View.ShowBaseStations = !View.ShowBaseStations; break;
                case Key.L: LiveDevices = !LiveDevices; break;
                case Key.F: View.FrameAll(); break;
                case Key.G: View.Tool = View.Tool == null ? _gizmo : null; break;
                case Key.A: View.Msaa = View.Msaa == 0 ? 4 : 0; break;
                default: e.Handled = false; break;
            }
        }
    }
}
