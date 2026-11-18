using System;
using System.Collections.Generic;
using System.Linq;
using VRMR.App.Services;

namespace VRMR.App.Viewport
{
    /// <summary>
    ///     Shows the open take in the 3D view at the playhead: the solved pose, the face, the take's devices and camera, and
    ///     its room. A placement that is not solved in yet (a gizmo drag) is previewed by moving the last solve.
    /// </summary>
    public sealed class TakePresenter
    {
        readonly PlaybackClock _clock;
        readonly Dictionary<string, float> _face = new();
        readonly RoomService _rooms;
        readonly TakeSession _session;
        SteamVRRoom _room;
        Recording _roomOf;

        public TakePresenter(TakeSession session, PlaybackClock clock, RoomService rooms)
        {
            _session = session;
            _clock = clock;
            _rooms = rooms;
        }

        /// <summary>The frame last shown (for the overlay and the transport).</summary>
        public int Frame { get; private set; }

        /// <summary>What the view adds to the last solve: the take's placement now relative to the one it was solved with.</summary>
        public Pose PlacementDelta()
        {
            var r = _session.Take;
            var c = _session.Clip;
            if (r == null || c == null) return Pose.Identity;
            return r.Edits.Placement * c.Placement.Inverse;
        }

        static bool IsIdentity(Pose p)
        {
            return p.pos.SqrLength < 1e-12f && Math.Abs(Math.Abs(p.rot.w) - 1) < 1e-7f;
        }

        /// <summary>Fills the view with the take at the playhead. Returns false when there is nothing solved to show.</summary>
        public bool Show(IViewport view)
        {
            var c = _session.Clip;
            var r = _session.Take;
            if (c == null || r == null || c.Profile == null) return false;
            var scene = view.Scene;
            if (scene.Profile != c.Profile) scene.SetProfile(c.Profile);
            scene.PlayArea = null; // the live play area doesn't apply to a replay; the take's own follows below
            double te = r.Edits.SourceToEdited(_clock.Playhead, r.Duration);
            int f = Frame = c.FrameAt(te);
            c.EvaluateNodes(f, scene.NodePos, scene.NodeRot);
            scene.MeshScale = c.ScaleAt(f);
            _face.Clear();
            foreach (var kv in c.Face) _face[kv.Key] = kv.Value[f];
            scene.FaceWeights = _face;
            scene.Markers.Clear();
            if (c.Camera != null && c.CameraValid[f])
                scene.Markers.Add(new ViewMarker
                {
                    Pose = c.Camera[f], Tint = MarkerTint.Camera, Kind = MarkerKind.Camera, Size = 1, Label = "camera"
                });
            // devices as small models at the tracking scale (like VRChat, where the controllers shrink with the avatar)
            float size = c.Calibration != null && c.Calibration.TrackScale > 0.05f ? c.Calibration.TrackScale : 1f;
            foreach (var kv in c.Trackers)
            {
                if (!c.TrackerValid[kv.Key][f]) continue;
                bool hand = kv.Key == "LeftHand" || kv.Key == "RightHand", head = kv.Key == "Head";
                scene.Markers.Add(new ViewMarker
                {
                    Pose = kv.Value[f], Size = size, NoDropLine = true,
                    Tint = head ? MarkerTint.Head :
                        kv.Key == "LeftHand" ? MarkerTint.Left :
                        kv.Key == "RightHand" ? MarkerTint.Right : MarkerTint.Tracker,
                    Kind = head ? MarkerKind.Headset : hand ? MarkerKind.Controller : MarkerKind.Tracker
                });
            }

            string roomNote = ShowRoom(view, c, r, f);
            scene.Overlay = $"{BodySourceText(c.BodySource)}   frame {f}/{Math.Max(0, c.Frames - 1)}" +
                            (roomNote.Length > 0 ? "\n" + roomNote : "");
            scene.OverlayRight = r.Name ?? "";
            var d = PlacementDelta();
            if (!IsIdentity(d)) scene.Transform(d);
            view.SceneChanged();
            return true;
        }

        /// <summary>The body source in the user's words.</summary>
        public static string BodySourceText(string source)
        {
            return source == "humr" ? "VRChat IK" : source == "ik" ? "IK solve" : "No body";
        }

        /// <summary>
        ///     The take's play area and base stations, mapped like its devices at frame f. Takes that recorded their own room
        ///     use it; older ones are shown with the SteamVR room as last seen, but only when the take's headset stayed mostly
        ///     inside that play area. Returns a note for the overlay ("" for the take's own room).
        /// </summary>
        string ShowRoom(IViewport view, BakedClip c, Recording r, int f)
        {
            if (_roomOf != r)
            {
                _roomOf = r;
                lock (r.Sync) _room = SteamVRRoom.From(r);
            }

            var room = _room;
            bool approx = false;
            if (room == null)
            {
                room = _rooms.Known;
                approx = room != null;
            }

            if (room == null || c.Room == null || f < 0 || f >= c.Room.Length || c.RoomScale[f] <= 0) return "";
            bool wanted = view.Options.PlayArea || view.Options.BaseStations;
            if (approx && room.PlayArea != null)
            {
                float fit = _rooms.Fit(r, room);
                if (fit < 0.6f)
                    return wanted
                        ? $"Room not shown: SteamVR's room now doesn't fit this take (headset inside the play area {fit * 100:0}% of the time)"
                        : "";
            }

            var m = c.Room[f];
            float k = c.RoomScale[f];
            var scene = view.Scene;
            if (room.PlayArea != null) scene.PlayArea = room.PlayArea.Select(p => m.TransformPoint(p * k)).ToArray();
            foreach (var s in room.BaseStations)
                scene.Markers.Add(new ViewMarker
                {
                    Pose = new Pose(m.TransformPoint(s.Pose.pos * k), (m.rot * s.Pose.rot).Normalized),
                    Tint = MarkerTint.Station, Kind = MarkerKind.BaseStation,
                    Size = k, FovL = s.FovL, FovR = s.FovR, FovT = s.FovT, FovB = s.FovB, NoDropLine = true
                });
            return approx && wanted ? "Room: SteamVR's room as it is now (this take didn't record its own)" : "";
        }

        /// <summary>The avatar as the view shows it at the playhead (placement included). False when no body is solved.</summary>
        public bool ShownPose(out Vec3[] positions, out Quat[] rotations, out int frame)
        {
            positions = null;
            rotations = null;
            frame = 0;
            var c = _session.Clip;
            var r = _session.Take;
            if (c == null || r == null || c.Profile == null || c.BodySource == "none") return false;
            frame = c.FrameAt(r.Edits.SourceToEdited(_clock.Playhead, r.Duration));
            int n = c.Profile.Nodes.Count;
            positions = new Vec3[n];
            rotations = new Quat[n];
            c.EvaluateNodes(frame, positions, rotations);
            var d = PlacementDelta();
            for (int i = 0; i < n; i++)
            {
                positions[i] = d.TransformPoint(positions[i]);
                rotations[i] = (d.rot * rotations[i]).Normalized;
            }

            return true;
        }
    }
}
