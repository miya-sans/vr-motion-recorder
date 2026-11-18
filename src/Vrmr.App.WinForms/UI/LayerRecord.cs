using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Windows.Forms;

namespace VRMR.UI
{
    /// <summary>
    ///     "Record layer": re-record chosen inputs (arms, legs, head, face tracking, finger tracking...) against the scene.
    ///     Arm it at the playhead, then start recording from VR with the record hotkey: the scene plays from the pre-roll with
    ///     count-in beeps, a high beep where the new layer starts, and the take lands on a new input track when you stop.
    ///     Timing runs on the shared Clock (see PunchIn).
    /// </summary>
    public sealed class LayerRecordModule : ISceneModule
    {
        readonly Timer _timer = new() { Interval = 15 };
        public string ArmedActor = "";
        public double ArmedAt;
        public string LastResult = "";
        public PunchInSettings Opt = PunchInSettings.Load();
        int _attempt;
        DarkButton _btn;
        int _countOut;
        SceneEditor _ed;
        PunchIn.RecZeroEstimator _est;
        string _lastTrackId;
        LayerRecordPopup _popup;
        Recording _rec;
        double _savedSpeed = 1;

        /// <summary>Armed: the next recording is a punch-in at <see cref="ArmedAt" /> for <see cref="ArmedActor" />.</summary>
        public bool Armed { get; private set; }

        /// <summary>The attempt being recorded (null when none).</summary>
        public PunchInTake Current { get; private set; }

        public static LayerRecordModule Instance { get; private set; }

        public void Attach(SceneEditor ed)
        {
            _ed = ed;
            Instance = this;
            _btn = ed.AddToolbarButton("● Record layer", TogglePopup,
                "Re-record body parts (by device), face or finger tracking against the scene: count-in beeps, start / stop with the record hotkey");
            _btn.Width = Theme.S(124);
            // a record button belongs with the transport: right after Loop (feature buttons are appended at the end of the bar)
            var bar = _btn.Parent;
            var loop = bar?.Controls.Cast<Control>().FirstOrDefault(x => x is DarkButton b && b.Text == "Loop");
            if (loop != null) bar.Controls.SetChildIndex(_btn, bar.Controls.IndexOf(loop) + 1);
            ed.RecordingStarted += OnStarted;
            ed.RecordingStopped += OnStopped;
            ed.SceneChanged += () =>
            {
                if (Armed && _ed.Scene?.ActorById(ArmedActor) == null && _ed.Scene?.Actors.Count > 0)
                    ArmedActor = _ed.Scene.Actors[0].Id;
                UpdateButton();
            };
            ed.View.PaintOverlay += PaintOverlay;
            ed.View.Animating += () => Current != null; // the count-in and the running time are drawn from the clock
            _timer.Tick += (o, e) => Tick();
            UpdateButton();
        }

        // ---------------------------------------------------------------- arming

        public void Arm()
        {
            if (_ed.Scene == null) return;
            Armed = true;
            ArmedAt = _ed.Playhead;
            ArmedActor = (_ed.SelectedActor ?? _ed.Scene.Actors.FirstOrDefault())?.Id ?? "";
            LastResult = "";
            Opt.Save();
            UpdateButton();
            _ed.View.Invalidate();
            LayerBeeps.Play(LayerBeeps.Armed());
        }

        public void Disarm()
        {
            Armed = false;
            UpdateButton();
            _ed.View.Invalidate();
        }

        /// <summary>Arms (if needed) and starts recording now (the desktop way).</summary>
        public void RecordNow()
        {
            if (!Armed) Arm();
            if (!_ed.IsRecording()) _ed.StartRecording?.Invoke();
        }

        void UpdateButton()
        {
            if (_btn == null) return;
            if (Current != null)
            {
                _btn.Text = "● REC " + PunchIn.NameOf(Current.Parts);
                _btn.Style = DarkButton.Kind.Danger;
            }
            else if (Armed)
            {
                _btn.Text = "● Armed: " + PunchIn.NameOf(Opt.Parts);
                _btn.Style = DarkButton.Kind.Danger;
            }
            else
            {
                _btn.Text = "● Record layer";
                _btn.Style = DarkButton.Kind.Normal;
            }

            _btn.Width = Math.Max(Theme.S(124), TextRenderer.MeasureText(_btn.Text, Theme.UI).Width + Theme.S(22));
            _btn.Invalidate();
            _popup?.RefreshState();
        }

        void TogglePopup()
        {
            if (_popup != null && _popup.Visible)
            {
                _popup.Hide();
                return;
            }

            if (_popup == null || _popup.IsDisposed) _popup = new LayerRecordPopup(this, _ed);
            _popup.RefreshState();
            _popup.FitHeight();
            var pt = _btn.PointToScreen(new Point(0, _btn.Height + Theme.S(2)));
            var wa = Screen.FromControl(_btn).WorkingArea;
            _popup.Location = new Point(Math.Max(wa.Left, Math.Min(pt.X, wa.Right - _popup.Width)),
                Math.Max(wa.Top, pt.Y - _popup.Height - _btn.Height - Theme.S(4)));
            if (pt.Y + _popup.Height < wa.Bottom) _popup.Location = new Point(_popup.Location.X, pt.Y);
            _popup.Show(_btn.FindForm());
        }

        // ---------------------------------------------------------------- recording


        void OnStarted(Recording rec)
        {
            if (!Armed || _ed.Scene == null) return;
            double now = Clock.Now;
            _attempt++;
            var t = new PunchInTake
            {
                Parts = Opt.Parts, ActorId = ArmedActor, PunchIn = ArmedAt, PreRoll = Math.Max(0, Opt.PreRoll),
                Attempt = _attempt,
                Beeps = Opt.CountIn ? PunchIn.Beeps(Math.Max(0, Opt.PreRoll)) : new double[0]
            };
            if (Opt.StopMode == 1) t.StopAt = _ed.Scene.RangeEnd;
            else if (Opt.StopMode == 2) t.StopAt = t.PunchIn + Math.Max(0.5, Opt.Length);
            // the take's t = 0 on the Clock: the recorder's own value when the host gives it, else from the first samples
            double recT = _ed.RecordingTime?.Invoke() ?? 0;
            if (recT > 0 && recT < 2)
            {
                t.RecZeroClock = now - recT;
                t.RecZeroHow = "recorder";
                _est = null;
            }
            else
            {
                t.RecZeroClock = now;
                t.RecZeroHow = "handler";
                _est = new PunchIn.RecZeroEstimator();
                _est.Observe(rec, now);
            }

            // play the scene from the pre-roll, driven by the Clock (not by the UI timer's steps)
            t.PlayClock0 = Clock.Now;
            Current = t;
            _rec = rec;
            _countOut = 0;
            _savedSpeed = _ed.SpeedMul;
            _ed.SpeedMul = 0;
            _ed.Playing = true;
            _ed.Playhead = t.SceneStart;
            if (t.Beeps.Length > 0) LayerBeeps.Play(LayerBeeps.CountIn(t.Beeps));
            _timer.Start();
            UpdateButton();
            AppLog.Info($"Record layer: {PunchIn.NameOf(t.Parts)} at {t.PunchIn:0.000} s, pre-roll {t.PreRoll:0.0} s" +
                        (t.StopAt >= 0 ? $", stops at {t.StopAt:0.00} s" : ""));
        }

        void Tick()
        {
            var t = Current;
            if (t == null || !_ed.IsRecording())
            {
                _timer.Stop();
                return;
            }

            double now = Clock.Now;
            if (_est != null && now - t.PlayClock0 < 1.5 && _rec != null)
            {
                _est.Observe(_rec, now);
                if (_est.Seen > 0 && _est.Best < t.RecZeroClock + 0.5)
                {
                    t.RecZeroClock = Math.Min(t.RecZeroClock, _est.Best);
                    t.RecZeroHow = "samples";
                }
            }

            double st = t.SceneTime(now);
            _ed.Playing = true;
            _ed.Playhead = Math.Min(st, _ed.Scene?.RangeEnd ?? st); // past the end: hold the last frame (no loop)
            if (t.StopAt >= 0)
            {
                if (_countOut == 0 && st >= t.StopAt - 2 && t.StopAt - 2 > t.PunchIn)
                {
                    _countOut = 1;
                    LayerBeeps.Play(LayerBeeps.Soft());
                }
                else if (_countOut <= 1 && st >= t.StopAt - 1 && t.StopAt - 1 > t.PunchIn)
                {
                    _countOut = 2;
                    LayerBeeps.Play(LayerBeeps.Soft());
                }

                if (st >= t.StopAt)
                {
                    _timer.Stop();
                    _ed.StopRecording?.Invoke();
                }
            }
        }

        void OnStopped(Recording rec)
        {
            var t = Current;
            if (t == null) return;
            _timer.Stop();
            Current = null;
            _rec = null;
            _ed.Playing = false;
            _ed.SpeedMul = _savedSpeed;
            try
            {
                if (_ed.Scene == null) throw new InvalidOperationException("no scene open");
                if (string.IsNullOrEmpty(rec?.FilePath) || !File.Exists(rec.FilePath))
                    throw new InvalidOperationException("the take wasn't saved");
                double dur;
                lock (rec.Sync) dur = rec.Duration;
                if (t.SceneOf(dur) <= t.PunchIn + 0.05)
                    throw new InvalidOperationException(
                        $"stopped before the punch-in point (after {dur:0.0} s); the take is kept in the recordings list");
                // the performer's latency: trigger taps on the count-in, else the remembered value
                t.Latency = Opt.Latency;
                t.LatencyHow = "remembered";
                if (Opt.TapSync && t.Beeps.Length >= 2 &&
                    PunchIn.MeasureLatency(PunchIn.TriggerPresses(rec), t.BeepTakeTimes, out double lat,
                        out int taps) && lat > -0.2 && lat < 0.6)
                {
                    t.Latency = lat;
                    t.Taps = taps;
                    t.LatencyHow = $"measured from {taps} trigger taps";
                    Opt.Latency = lat;
                    Opt.Save();
                }

                Strip made = null;
                NlaTrack track = null;
                string name, created;
                lock (rec.Sync)
                {
                    name = rec.Name;
                    created = rec.CreatedUtc.ToString("o");
                }

                _ed.Edit("Record layer", () =>
                {
                    if (Opt.MutePrevious && _lastTrackId != null)
                    {
                        var prev = _ed.Scene.FindTrack(_lastTrackId).Track;
                        if (prev != null) prev.Mute = true;
                    }

                    made = PunchIn.Land(_ed.Scene, t, Opt, rec.FilePath, name, created, dur, out track);
                });
                _lastTrackId = track?.Id;
                LastResult =
                    $"{PunchIn.NameOf(t.Parts)} landed at {made.Start:0.00} s on \"{track.Name}\". {t.Describe()}";
                AppLog.Info("Record layer: " + LastResult);
                _ed.Select(made);
                _ed.Playhead = t.PunchIn;
                LayerBeeps.Play(LayerBeeps.Landed());
            }
            catch (Exception ex)
            {
                LastResult = "Not added: " + ex.Message;
                AppLog.Warn("Record layer: " + LastResult);
                LayerBeeps.Play(LayerBeeps.Error());
            }

            if (!Opt.KeepArmed) Armed = false;
            UpdateButton();
            _ed.View.Invalidate();
        }

        // ---------------------------------------------------------------- 3D view banner (visible through a desktop overlay in VR)

        void PaintOverlay(Viewport3D v, Graphics g)
        {
            if (!Armed && Current == null) return;
            string big = null, line;
            var col = Theme.Rec;
            var t = Current;
            if (t != null)
            {
                double st = t.SceneTime(Clock.Now);
                double toGo = t.PunchIn - st;
                if (toGo > 0)
                {
                    big = Math.Ceiling(toGo).ToString("0");
                    line = $"Count-in  ·  {PunchIn.NameOf(t.Parts)} starts at {t.PunchIn:0.00} s";
                    col = Theme.Warn;
                }
                else
                    line = $"● REC {PunchIn.NameOf(t.Parts)}   {st - t.PunchIn:0.0} s" + (t.StopAt >= 0
                        ? $" / {t.StopAt - t.PunchIn:0.0} s"
                        : "   (record hotkey to stop)");
            }
            else
                line =
                    $"● Record layer armed: {PunchIn.NameOf(Opt.Parts)} from {ArmedAt:0.00} s  ·  start with the record hotkey";

            var sz = TextRenderer.MeasureText(line, Theme.UIBold);
            var r = new Rectangle((v.Width - sz.Width) / 2 - Theme.S(12), Theme.S(10), sz.Width + Theme.S(24),
                sz.Height + Theme.S(10));
            Theme.FillRound(g, Color.FromArgb(200, 20, 20, 24), r, Theme.S(6));
            Theme.StrokeRound(g, col, r, Theme.S(6), 1.5f);
            TextRenderer.DrawText(g, line, Theme.UIBold, r, col,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (big != null)
                using (var f = new Font(Theme.UI.FontFamily, Theme.Sf(64), FontStyle.Bold))
                    TextRenderer.DrawText(g, big, f, new Rectangle(0, r.Bottom + Theme.S(10), v.Width, Theme.S(100)),
                        col, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
        }
    }

    /// <summary>The Record layer settings: what to re-record, count-in, stop, latency; arm / record now.</summary>
    sealed class LayerRecordPopup : Form
    {
        readonly InspectorContext _c;
        readonly SceneEditor _ed;
        readonly LayerRecordModule _m;
        readonly StackPanel _page;
        DarkButton _arm;
        LayerFigure _fig;
        InfoLabel _state;

        public LayerRecordPopup(LayerRecordModule m, SceneEditor ed)
        {
            _m = m;
            _ed = ed;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Border;
            Padding = new Padding(1);
            Size = new Size(Theme.S(340), Theme.S(640));
            KeyPreview = true;
            KeyDown += (o, e) =>
            {
                if (e.KeyCode == Keys.Escape) Hide();
            };
            Deactivate += (o, e) => BeginInvoke((Action)(() =>
            {
                if (!ContainsFocus) Hide();
            }));
            _page = new StackPanel { Dock = DockStyle.Fill, BackColor = Theme.Panel };
            Controls.Add(_page);
            _c = new InspectorContext { Editor = ed, Page = _page };
            Build();
        }

        PunchInSettings O => _m.Opt;

        void Build()
        {
            var c = _c;
            c.Section("Record a layer");
            c.Note(
                "Re-record parts of the body (by device), face or finger tracking against the scene. The scene plays from a pre-roll with count-in beeps; the new take lands on its own input track.");
            var presets = PunchIn.Presets.ToList();
            var names = presets.Select(p => p.Name).ToList();
            names.Add("Custom (click the figure)");

            int Idx()
            {
                int i = presets.FindIndex(p => p.Parts == O.Parts);
                return i >= 0 ? i : names.Count - 1;
            }

            Props.Choice(c, "Re-record", names.ToArray(), Idx, i =>
            {
                if (i < presets.Count)
                {
                    O.Parts = presets[i].Parts;
                    Changed();
                }
            }, "What the new take replaces");
            _fig = c.Add(new LayerFigure(LayerFigure.Mode.Inputs) { Value = (int)O.Parts, Height = Theme.S(210) });
            _fig.Toggled += v =>
            {
                O.Parts = (InputPart)v;
                Changed();
            };
            Props.Number(c, "Pre-roll", () => O.PreRoll, v =>
            {
                O.PreRoll = MathX.Clamp(v, 0, 30);
                Changed();
            }, "0.0", " s", "How long the scene plays before the layer starts (a beep each second)", 0, 30);
            Props.Check(c, "Count-in beeps", () => O.CountIn, v =>
            {
                O.CountIn = v;
                Changed();
            }, "Beeps each second of the pre-roll and a high beep where the layer starts (you hear them in VR)");
            Props.Choice(c, "Stop", new[] { "With the record hotkey", "At the end of the scene", "After a set length" },
                () => O.StopMode, i =>
                {
                    O.StopMode = i;
                    Changed();
                });
            var len = Props.Number(c, "Length", () => O.Length, v =>
            {
                O.Length = Math.Max(0.5, v);
                Changed();
            }, "0.0", " s", "How long the layer records (two soft beeps before it stops)", 0.5, 3600);
            c.OnRefresh(() => len.Parent.Visible = O.StopMode == 2);
            Props.Number(c, "Your latency", () => O.Latency * 1000, v =>
                {
                    O.Latency = MathX.Clamp(v / 1000, -0.5, 1);
                    Changed();
                }, "0", " ms",
                "How late you move against what you hear (sound output + reaction); the take is moved earlier by this",
                -500, 1000);
            Props.Check(c, "Measure it: tap a trigger on the count-in beeps", () => O.TapSync, v =>
                {
                    O.TapSync = v;
                    Changed();
                },
                "Tap either trigger on two or more count-in beeps; the average delay is used for this take and remembered");
            Props.Choice(c, "Placement", new[] { "As recorded", "Headset matched to the scene" }, () => (int)O.Align,
                i =>
                {
                    O.Align = (InputAlign)i;
                    Changed();
                },
                "As recorded: same spot in the playspace. Headset matched: moved so your headset meets the base take's where the layer starts");
            Props.Check(c, "A new attempt mutes the previous one", () => O.MutePrevious, v =>
            {
                O.MutePrevious = v;
                Changed();
            });
            Props.Check(c, "Stay armed after a take", () => O.KeepArmed, v =>
            {
                O.KeepArmed = v;
                Changed();
            });
            _state = c.Note("", false);
            _arm = new DarkButton("Arm", DarkButton.Kind.Danger);
            _arm.Click += (o, e) =>
            {
                if (_m.Armed) _m.Disarm();
                else _m.Arm();
                RefreshState();
            };
            var now = new DarkButton("Record now");
            now.Click += (o, e) =>
            {
                _m.RecordNow();
                RefreshState();
            };
            new ToolTip().SetToolTip(now, "Arms at the playhead and starts recording right away");
            c.Page.Row(_arm, now);
            RefreshState();
        }

        void Changed()
        {
            O.Save();
            _fig.Value = (int)O.Parts;
            _fig.Invalidate();
            RefreshState();
            FitHeight();
        }

        /// <summary>Tall enough for everything on it (within the screen).</summary>
        public void FitHeight()
        {
            _page.PerformLayout();
            int bottom = _page.Controls.Cast<Control>().Where(x => x.Visible)
                .Select(x => x.Bottom - _page.AutoScrollPosition.Y).DefaultIfEmpty(0).Max();
            int want = bottom + Theme.S(12) + Padding.Vertical;
            var wa = Screen.FromPoint(Location).WorkingArea;
            Height = Math.Min(want, Math.Max(Theme.S(300), wa.Height - Theme.S(40)));
        }

        public void RefreshState()
        {
            if (_state == null) return;
            string key = "the record hotkey";
            if (_m.Current != null) _state.Text = "Recording… stop with " + key + ".";
            else if (_m.Armed)
                _state.Text =
                    $"Armed at {_m.ArmedAt:0.00} s. In VR, press {key}: {(O.CountIn ? $"{Math.Floor(O.PreRoll + 1e-6):0} count-in beeps, then a high beep where the layer starts" : "the layer starts after the pre-roll")}; press it again to stop.";
            else
                _state.Text = $"The layer starts at the playhead ({_ed.Playhead:0.00} s)." +
                              (_m.LastResult.Length > 0 ? "\nLast: " + _m.LastResult : "");
            _state.Color = _m.Armed || _m.Current != null ? Theme.Warn : Theme.TextDim;
            _arm.Text = _m.Armed ? "Disarm" : "Arm at the playhead";
            _arm.Invalidate();
            foreach (var r in _c.Refreshers) r();
            _page.PerformLayout();
        }
    }

    /// <summary>Count-in and status sounds (generated tones, played asynchronously).</summary>
    public static class LayerBeeps
    {
        const int Rate = 44100;
        static SoundPlayer _player;
        static MemoryStream _stream;

        public static void Play(byte[] wav)
        {
            try
            {
                var ms = new MemoryStream(wav);
                var p = new SoundPlayer(ms);
                p.Load();
                p.Play();
                _player = p;
                _stream = ms; // keep them alive while playing
            }
            catch (Exception ex) { AppLog.Warn("Beep: " + ex.Message); }
        }

        /// <summary>
        ///     One WAV with every count-in beep at its offset (s): exact spacing whatever the timers do. The last one is the
        ///     punch-in.
        /// </summary>
        public static byte[] CountIn(double[] beeps)
        {
            double len = (beeps.Length > 0 ? beeps.Max() : 0) + 0.35;
            float[] buf = new float[(int)(len * Rate)];
            for (int i = 0; i < beeps.Length; i++)
            {
                bool last = i == beeps.Length - 1;
                Tone(buf, beeps[i], last ? 1320 : 880, last ? 0.22 : 0.07, last ? 0.55f : 0.45f);
            }

            return Wav(buf);
        }

        public static byte[] Armed()
        {
            float[] b = new float[(int)(0.25 * Rate)];
            Tone(b, 0, 660, 0.06, 0.3f);
            Tone(b, 0.1, 880, 0.06, 0.3f);
            return Wav(b);
        }

        public static byte[] Soft()
        {
            float[] b = new float[(int)(0.12 * Rate)];
            Tone(b, 0, 660, 0.05, 0.25f);
            return Wav(b);
        }

        public static byte[] Landed()
        {
            float[] b = new float[(int)(0.4 * Rate)];
            Tone(b, 0, 660, 0.07, 0.35f);
            Tone(b, 0.1, 880, 0.07, 0.35f);
            Tone(b, 0.2, 1320, 0.1, 0.35f);
            return Wav(b);
        }

        public static byte[] Error()
        {
            float[] b = new float[(int)(0.45 * Rate)];
            Tone(b, 0, 220, 0.35, 0.4f, true);
            return Wav(b);
        }

        static void Tone(float[] buf, double at, double hz, double dur, float amp, bool square = false)
        {
            int a = (int)(at * Rate), n = (int)(dur * Rate), fade = Math.Max(1, (int)(0.005 * Rate));
            for (int i = 0; i < n && a + i < buf.Length; i++)
            {
                if (a + i < 0) continue;
                double ph = 2 * Math.PI * hz * i / Rate;
                float s = square ? Math.Sin(ph) >= 0 ? 0.6f : -0.6f : (float)Math.Sin(ph);
                float env = Math.Min(1f, Math.Min(i / (float)fade, (n - i) / (float)fade));
                buf[a + i] += s * amp * env;
            }
        }

        static byte[] Wav(float[] s)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                int data = s.Length * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + data);
                w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)1);
                w.Write(Rate);
                w.Write(Rate * 2);
                w.Write((short)2);
                w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(data);
                foreach (float x in s) w.Write((short)(MathX.Clamp(x, -1f, 1f) * 32000));
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
