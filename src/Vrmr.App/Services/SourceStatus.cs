using System;
using System.Collections.Generic;
using System.Linq;

namespace VRMR.App.Services
{
    /// <summary>How a source is doing: grey off, amber waiting or needing attention, green receiving.</summary>
    public enum SourceLevel { Off, Waiting, Ok, Warning }

    /// <summary>
    ///     One source as the top bar shows it: a name, a word or two, and the details with the fix when something is
    ///     missing.
    /// </summary>
    public sealed class SourceState : IEquatable<SourceState>
    {
        public string Id { get; init; } = "";
        public string Label { get; init; } = "";
        public string StateText { get; init; } = "";
        public SourceLevel Level { get; init; }

        /// <summary>What is known, one fact per line.</summary>
        public string Detail { get; init; } = "";

        /// <summary>What to do when something is missing, or "".</summary>
        public string Fix { get; init; } = "";

        public bool Equals(SourceState o)
        {
            return o != null && Id == o.Id && Label == o.Label && StateText == o.StateText && Level == o.Level &&
                   Detail == o.Detail && Fix == o.Fix;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as SourceState);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Id, StateText, Level, Detail);
        }
    }

    /// <summary>Reads the state of the live sources (SteamVR, VRChat, face tracking, the avatar worn) for the top bar.</summary>
    public static class SourceStatus
    {
        public const string SteamVr = "steamvr", VrChat = "vrchat", Face = "face", Avatar = "avatar";

        /// <summary>The four states right now.</summary>
        public static List<SourceState> Read(LiveService live)
        {
            var r = live.Recorder;
            var list = new List<SourceState>();
            if (!live.CaptureOn)
            {
                const string off =
                    "Live sources are off in this session (the app was started with --no-capture). Takes can be opened, edited and exported.";
                list.Add(new SourceState { Id = SteamVr, Label = "SteamVR", StateText = "off", Detail = off });
                list.Add(new SourceState { Id = VrChat, Label = "VRChat", StateText = "off", Detail = off });
                list.Add(new SourceState { Id = Face, Label = "Face", StateText = "off", Detail = off });
                return list;
            }

            double now = Clock.Now;

            // SteamVR
            var ov = r.OpenVR;
            if (ov == null)
                list.Add(new SourceState
                {
                    Id = SteamVr, Label = "SteamVR", StateText = "off", Level = SourceLevel.Off,
                    Detail = "SteamVR devices are switched off.", Fix = "Turn them on under Settings → Sources."
                });
            else if (!ov.Connected)
                list.Add(new SourceState
                {
                    Id = SteamVr, Label = "SteamVR", StateText = "waiting", Level = SourceLevel.Waiting,
                    Detail = "Not connected: " + (ov.Status ?? "").TrimEnd('.') + ".",
                    Fix = "Start SteamVR. The app connects by itself and never starts SteamVR on its own."
                });
            else
            {
                var devs = ov.Devices();
                int tracked = devs.Count(d => d.Valid);
                string area = DeviceText.PlayArea(ov.PlayArea);
                list.Add(new SourceState
                {
                    Id = SteamVr, Label = "SteamVR",
                    StateText = devs.Count == 0 ? "no devices" : $"{tracked}/{devs.Count} tracked",
                    Level = devs.Count > 0 && tracked == devs.Count ? SourceLevel.Ok : SourceLevel.Warning,
                    Detail = DeviceText.Summary(devs) + "\nFinger tracking: " + ov.InputStatus +
                             (area != null ? "\nPlay area: " + area : "\nPlay area: not set up"),
                    Fix = devs.Count == 0
                        ? "Switch on your headset, controllers and trackers."
                        : tracked < devs.Count
                            ? "Some devices are not tracked: check that the base stations see them. The Devices panel shows which."
                            : ""
                });
            }

            // VRChat
            bool osc = r.LastOscTime > 0 && now - r.LastOscTime < 5;
            var log = r.Log;
            // VRChat is known to run when its OSCQuery service answers, or when its log is being written right now
            bool found = r.OscQuery?.VrcHttp != null || (log != null && log.HumrActive);
            string world = log != null && !string.IsNullOrEmpty(log.WorldName) ? "\nWorld: " + log.WorldName : "";
            string body = log != null && log.HumrActive
                ? "\nVRChat's own IK is arriving from a HUMR recorder in this world (kept as a reference body)."
                : "";
            if (osc)
                list.Add(new SourceState
                {
                    Id = VrChat, Label = "VRChat", StateText = "receiving", Level = SourceLevel.Ok,
                    Detail = $"Receiving OSC ({r.OscRoute ?? "OSC"})." + world + body
                });
            else if (found)
                list.Add(new SourceState
                {
                    Id = VrChat, Label = "VRChat", StateText = "no OSC", Level = SourceLevel.Warning,
                    Detail = "VRChat is running but sends no OSC." + world + body,
                    Fix = "Turn OSC on in VRChat: Action Menu → Options → OSC → Enabled."
                });
            else
                list.Add(new SourceState
                {
                    Id = VrChat, Label = "VRChat", StateText = "waiting", Level = SourceLevel.Waiting,
                    Detail = "VRChat was not found yet.",
                    Fix =
                        "Start VRChat with OSC on (Action Menu → Options → OSC → Enabled). The app finds it by itself."
                });

            // face tracking
            bool face = r.LastFaceTime > 0 && now - r.LastFaceTime < 5;
            list.Add(face
                ? new SourceState
                {
                    Id = Face, Label = "Face", StateText = "active", Level = SourceLevel.Ok,
                    Detail = "Face tracking parameters are arriving."
                }
                : new SourceState
                {
                    Id = Face, Label = "Face", StateText = "no data", Level = SourceLevel.Off,
                    Detail = "No face tracking parameters in the last 5 seconds.",
                    Fix =
                        "Face tracking is optional. With VRCFaceTracking running and an avatar that supports it, the parameters arrive over OSC by themselves."
                });

            // the avatar worn and its base model
            var m = live.CurrentMatch();
            string worn = live.WornAvatar;
            if (worn.Length >
                0) // nothing worn yet: no pill (the library always has a base model to offer, the mannequin)
            {
                string shown = string.IsNullOrEmpty(r.AvatarName)
                    ? worn.Length > 13 ? worn.Substring(0, 13) + "…" : worn
                    : Short(worn, 18);
                bool sure = m?.Entry != null && m.Sure;
                // nothing of the user's matches: the built-in mannequin stands in (the library always has it)
                bool standIn = m?.Entry != null && m.Entry.BuiltIn && !m.Sure;
                list.Add(new SourceState
                {
                    Id = Avatar, Label = "Avatar",
                    StateText = m?.Entry == null ? shown + " → none" :
                        string.Equals(worn, m.Entry.Name, StringComparison.OrdinalIgnoreCase) ? m.Entry.Name :
                        shown + " → " + m.Entry.Name + (sure ? "" : " ?"),
                    Level = sure ? SourceLevel.Ok : SourceLevel.Warning,
                    Detail = "Wearing: " + (worn.Length > 0 ? worn : "not reported yet") + (standIn
                        ? "\nNone of your base models matches it: takes are solved onto the built-in mannequin."
                        : m?.Entry != null
                            ? $"\nBase model: {m.Entry.Label} ({m.How})"
                            : "\nNo base model matches this avatar."),
                    Fix = m?.Entry == null || standIn
                        ? "Add your avatar's base model under Settings → Avatars, or pick one of yours in the Devices panel (Live view)."
                        : sure
                            ? ""
                            : "The base model is a guess: pick the right one in the Devices panel (Live view)."
                });
            }

            return list;
        }

        static string Short(string s, int n)
        {
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }
    }
}
