using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace VRMR.UI
{
    /// <summary>Top bar status chips: one line each (dot, name, state); the full story is in each chip's tooltip.</summary>
    public sealed class StatusChips : DarkControl
    {
        sealed class Chip { public string Title, State, Tip; public Color Color; public Rectangle Rect; }
        readonly List<Chip> _chips = new List<Chip>();
        string _sig = "";
        int _tipFor = -2;
        public StatusChips() { BackColor = Theme.Panel; }

        public void Update(Recorder r, AvatarMatch m, Settings s)
        {
            var list = new List<Chip>();
            void Add(string title, string state, Color c, string tip) => list.Add(new Chip { Title = title, State = state, Color = c, Tip = tip });
            double now = Clock.Now;
            bool osc = r.LastOscTime > 0 && now - r.LastOscTime < 5;
            bool vrcFound = r.OscQuery?.VrcHttp != null;
            string route = r.OscRoute;
            Add("VRChat", osc ? "receiving" : vrcFound ? "no OSC" : "waiting", osc ? Theme.Ok : vrcFound ? Theme.Warn : Theme.TextFaint,
                osc ? "VRChat OSC: receiving (" + route + ")."
                : vrcFound ? (r.LegacyRx != null ? "VRChat found; OSC is idle (also listening on 9001)." : "VRChat found but it sends no OSC: turn OSC on in VRChat's Action Menu (Options > OSC > Enabled).")
                : "Waiting for VRChat (found through OSCQuery when it runs with OSC on).");
            bool face = r.LastFaceTime > 0 && now - r.LastFaceTime < 5;
            Add("Face", face ? "active" : "no data", face ? Theme.Ok : Theme.TextFaint,
                face ? "Face tracking parameters are arriving." : "No face tracking parameters (VRCFaceTracking) in the last 5 s.");
            var ov = r.OpenVR;
            var devs = ov != null && ov.Connected ? ov.Devices() : null;
            int n = devs?.Count ?? 0, tracked = devs?.Count(d => d.Valid) ?? 0;
            string svr = ov == null ? "off" : !ov.Connected ? (ov.Status ?? "").ToLowerInvariant() : n == 0 ? "no devices" : $"{tracked}/{n} tracked";
            Add("SteamVR", svr, ov == null ? Theme.Warn : !ov.Connected ? Theme.TextFaint : n > 0 && tracked == n ? Theme.Ok : Theme.Warn,
                ov == null ? "SteamVR devices are off: turn them on in Settings > Sources." : MainForm.SteamVRSummary(ov));
            bool humr = r.Log != null && r.Log.HumrActive;
            Add("Body", humr ? "HUMR live" : "IK solve", humr ? Theme.Ok : Theme.Accent,
                humr ? "VRChat's own IK is streaming from a HUMR recorder in this world (reference)." : "The body is solved with IK from the headset, controllers and trackers.");
            string av = string.IsNullOrEmpty(r.AvatarName) ? (string.IsNullOrEmpty(r.AvatarId) ? "unknown" : r.AvatarId.Substring(0, Math.Min(13, r.AvatarId.Length)) + "…") : r.AvatarName;
            string state = m?.Entry != null
                ? (string.Equals(av, m.Entry.Name, StringComparison.OrdinalIgnoreCase) ? m.Entry.Name : Short(av, 16) + " → " + m.Entry.Name) + (m.Sure ? "" : " ?")
                : Short(av, 16) + " → none";
            Add("Avatar", state, m?.Entry != null && m.Sure ? Theme.Ok : Theme.Warn,
                "Wearing: " + av + "\n" + (m?.Entry != null
                    ? $"Base model: {m.Entry.Label} ({m.How})" + (m.Sure ? "" : "\nThis is a guess: pick the base model on the Live tab.")
                    : "No base model: add avatars on the Settings tab."));
            string sig = string.Join("|", list.Select(c => c.Title + c.State + c.Color.ToArgb() + c.Tip));
            if (sig == _sig) return;
            _sig = sig;
            _chips.Clear(); _chips.AddRange(list);
            _tipFor = -2;
            Invalidate();
        }

        /// <summary>Chip names and states, for tests.</summary>
        public string[] Texts => _chips.Select(c => c.Title + ": " + c.State).ToArray();

        static string Short(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = _chips.FindIndex(c => c.Rect.Contains(e.Location));
            if (i != _tipFor) { _tipFor = i; Theme.Tip(this, i >= 0 ? _chips[i].Tip : null); }
            base.OnMouseMove(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Theme.Panel);
            int h = Math.Min(Height, Theme.S(30)), y = (Height - h) / 2, gap = Theme.S(6), pad = Theme.S(11), dot = Theme.S(7);
            // whole chips when they all fit, else names only (the state is in the tooltip)
            int Need(Chip c, bool full) => pad + dot + Theme.S(7) + TextRenderer.MeasureText(c.Title, Theme.UIBold).Width + (full ? Theme.S(4) + TextRenderer.MeasureText(c.State, Theme.UI).Width : 0) + pad;
            bool full = _chips.Sum(c => Need(c, true) + gap) <= Width;
            int x = 0;
            foreach (var c in _chips)
            {
                int w = Need(c, full);
                if (x + w > Width) { c.Rect = Rectangle.Empty; continue; }
                var r = new RectangleF(x + 0.5f, y + 0.5f, w - 1, h - 1);
                c.Rect = Rectangle.Round(r);
                Theme.FillRound(g, Theme.Panel2, r, h / 2f);
                using (var b = new SolidBrush(c.Color)) g.FillEllipse(b, x + pad, y + (h - dot) / 2f, dot, dot);
                int tx = x + pad + dot + Theme.S(7);
                int tw = TextRenderer.MeasureText(c.Title, Theme.UIBold).Width;
                TextRenderer.DrawText(g, c.Title, Theme.UIBold, new Rectangle(tx, y, tw + Theme.S(2), h), Theme.Text, TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                if (full) TextRenderer.DrawText(g, c.State, Theme.UI, new Rectangle(tx + tw, y, x + w - pad - (tx + tw) + Theme.S(6), h), Theme.TextDim, TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                x += w + gap;
            }
        }
    }

    /// <summary>Brekel-style SteamVR device list: type/role, model, serial, battery and tracking state per device.</summary>
    public sealed class DeviceList : DarkControl
    {
        List<OpenVRSource.Device> _devs = new List<OpenVRSource.Device>();
        string _status = "SteamVR is off";
        Vec3[] _playArea;
        int RowH => Theme.S(40);

        public DeviceList() { Height = Theme.S(28); }

        public void UpdateFrom(OpenVRSource ov) =>
            Set(ov != null && ov.Connected ? ov.Devices() : new List<OpenVRSource.Device>(),
                ov == null ? "SteamVR is off (Settings > Sources)." : !ov.Connected ? ov.Status : null,
                ov != null && ov.Connected ? ov.PlayArea : null);

        public void Set(List<OpenVRSource.Device> devs, string status, Vec3[] playArea)
        {
            _status = status;
            _devs = devs.OrderBy(d => d.Class == 4 ? 9 : d.Class).ThenBy(d => d.Role).ThenBy(d => d.Serial).ToList();
            _playArea = playArea;
            if (_status == null && _devs.Count == 0) _status = "Connected. No devices are on.";
            int h = (_status != null ? Theme.S(24) : 0) + _devs.Count * RowH + (_status == null ? Theme.S(22) : 0);
            if (Height != h) { Height = h; Layouts.Relayout(this); }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Theme.Panel);
            int y = 0, w = Width;
            if (_status != null)
            {
                TextRenderer.DrawText(g, _status, Theme.UI, new Rectangle(0, y, w, Theme.S(22)), Theme.TextFaint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                y += Theme.S(24);
            }
            foreach (var d in _devs)
            {
                var r = new RectangleF(0, y + 1, w - 1, RowH - 4);
                Theme.FillRound(g, Theme.Panel2, r, Theme.Sf(5));
                var col = MainForm.DeviceColor(d);
                float cy = y + RowH / 2f - 2, rad = Theme.Sf(5);
                if (d.Valid) using (var b = new SolidBrush(col)) g.FillEllipse(b, Theme.S(9) - rad, cy - rad, rad * 2, rad * 2);
                else using (var p = new Pen(col, Theme.Sf(1.5f))) g.DrawEllipse(p, Theme.S(9) - rad, cy - rad, rad * 2, rad * 2);
                int x0 = Theme.S(20);
                // right column: battery, tracking state
                int rw = Theme.S(96);
                string line1 = MainForm.DeviceName(d);
                string model = string.IsNullOrEmpty(d.Model) ? d.Manufacturer : d.Model;
                string line2 = model + (string.IsNullOrEmpty(d.Serial) ? "" : "  ·  " + d.Serial);
                if (d.IsBaseStation && d.HasPose) line1 += $"   {d.Pos.y:0.00} m high";
                TextRenderer.DrawText(g, line1, Theme.UIBold, new Rectangle(x0, y + Theme.S(3), w - x0 - rw, Theme.S(18)), Theme.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, line2, Theme.Small, new Rectangle(x0, y + Theme.S(20), w - x0 - rw, Theme.S(16)), Theme.TextFaint, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                int rx = w - rw - Theme.S(6);
                if (d.Battery >= 0)
                {
                    var bc = d.Charging ? Theme.Accent : d.Battery < 0.2f ? Theme.Rec : d.Battery < 0.4f ? Theme.Warn : Theme.Ok;
                    var bar = new RectangleF(rx, y + Theme.S(8), Theme.S(34), Theme.S(9));
                    using (var p = new Pen(Theme.Border, 1f)) g.DrawRectangle(p, bar.X, bar.Y, bar.Width, bar.Height);
                    using (var b = new SolidBrush(bc)) g.FillRectangle(b, bar.X + 1.5f, bar.Y + 1.5f, (bar.Width - 2.5f) * d.Battery, bar.Height - 2.5f);
                    TextRenderer.DrawText(g, $"{d.Battery * 100:0}%{(d.Charging ? " +" : "")}", Theme.Small, new Point(rx + Theme.S(38), y + Theme.S(4)), Theme.TextDim);
                }
                var tc = d.TrackingLevel == 0 ? Theme.Ok : d.TrackingLevel == 1 ? Theme.Warn : Theme.Rec;
                TextRenderer.DrawText(g, d.TrackingText, Theme.Small, new Rectangle(rx - Theme.S(30), y + Theme.S(20), rw + Theme.S(30), Theme.S(16)), tc, TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
                y += RowH;
            }
            string pa = "Play area: not set up";
            if (_playArea != null && _playArea.Length == 4)
            {
                float a = (_playArea[1] - _playArea[0]).Length, b = (_playArea[2] - _playArea[1]).Length;
                pa = $"Play area: {Math.Max(a, b):0.0} × {Math.Min(a, b):0.0} m";
            }
            if (_status == null) TextRenderer.DrawText(g, pa, Theme.Small, new Rectangle(0, y + Theme.S(2), w, Theme.S(18)), Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>Bar chart of live face shape weights (as tall as its rows; one line when there is no data).</summary>
    public sealed class FaceBars : DarkControl
    {
        readonly Func<Dictionary<string, float>> _get;
        readonly System.Windows.Forms.Timer _t = new System.Windows.Forms.Timer { Interval = 50 };
        Dictionary<string, float> _vals = new Dictionary<string, float>();
        public int MaxRows = 16;
        int RowH => Theme.S(17);
        public FaceBars(Func<Dictionary<string, float>> get)
        {
            _get = get; Height = Theme.S(22);
            _t.Tick += (o, e) =>
            {
                if (!Visible) return;
                try { _vals = _get() ?? new Dictionary<string, float>(); } catch { }
                int rows = Math.Min(MaxRows, _vals.Count(k => k.Value > 0.02f));
                int h = rows == 0 ? Theme.S(22) : rows * RowH;
                // grow at once, shrink only when the face goes quiet (the page doesn't jump around)
                if (h > Height || (rows == 0 && Height != h)) { Height = h; Layouts.Relayout(this); }
                Invalidate();
            };
            _t.Start();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Panel);
            var items = _vals.Where(k => k.Value > 0.02f).OrderByDescending(k => k.Value).Take(MaxRows).ToList();
            if (items.Count == 0) { TextRenderer.DrawText(g, "No face tracking data", Theme.UI, ClientRectangle, Theme.TextFaint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter); return; }
            int y = 0, h = RowH, lw = Theme.S(140);
            foreach (var kv in items)
            {
                TextRenderer.DrawText(g, kv.Key, Theme.Small, new Rectangle(0, y, lw, h), Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                int bw = Width - lw - Theme.S(6);
                using (var b = new SolidBrush(Theme.Panel2)) g.FillRectangle(b, lw + Theme.S(6), y + Theme.S(5), bw, h - Theme.S(10));
                using (var b = new SolidBrush(Theme.Warn)) g.FillRectangle(b, lw + Theme.S(6), y + Theme.S(5), bw * MathX.Clamp01(kv.Value), h - Theme.S(10));
                y += h;
            }
        }
    }
}
