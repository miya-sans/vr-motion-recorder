using System;
using VRMR.App.Services;

namespace VRMR.App.Viewport
{
    /// <summary>
    ///     Shows what SteamVR and VRChat send right now: the devices, the play area, VRChat's head, wrists and camera, and
    ///     VRChat's own IK avatar while recording in a HUMR world. Nothing is recorded by showing it.
    /// </summary>
    public sealed class LivePresenter
    {
        readonly LiveService _live;
        readonly LiveHumrMap _map = new();
        bool _framedDevices;
        AvatarProfile _profile;
        string _profileKey;
        float _userEye;
        double _vrcRead = -100;

        public LivePresenter(LiveService live) { _live = live; }

        /// <summary>Fills the view with the live state only.</summary>
        public void Show(IViewport view, bool devicesOnly = false)
        {
            var rec = _live.Recorder;
            var scene = view.Scene;
            scene.Markers.Clear();
            var ov = rec.OpenVR;
            bool connected = ov != null && ov.Connected;
            scene.PlayArea = connected ? ov.PlayArea : null;
            int shown = 0;
            if (connected)
                foreach (var d in ov.Devices())
                {
                    if (!d.HasPose) continue; // never tracked yet: listed in the Devices panel only
                    scene.Markers.Add(new ViewMarker
                    {
                        Pose = new Pose(d.Pos, d.Rot), Faded = !d.Valid, Size = 1,
                        Tint = d.Class == 1
                            ? MarkerTint.Head
                            : d.Class == 2
                                ? d.Role == 1 ? MarkerTint.Left : d.Role == 2 ? MarkerTint.Right : MarkerTint.Accent
                                : d.Class == 4
                                    ? MarkerTint.Station
                                    : MarkerTint.Tracker,
                        Kind = d.Class == 1 ? MarkerKind.Headset :
                            d.Class == 2 ? MarkerKind.Controller :
                            d.Class == 4 ? MarkerKind.BaseStation : MarkerKind.Tracker,
                        Label = DeviceText.Name(d) + (d.Battery >= 0 ? $"  {d.Battery * 100:0}%" : "") +
                                (d.Valid ? "" : "\n" + d.TrackingText),
                        FovL = d.FovL, FovR = d.FovR, FovT = d.FovT, FovB = d.FovB
                    });
                    shown++;
                }

            if (devicesOnly && scene.Profile != null) scene.SetProfile(null);
            // first time devices show up without an avatar on screen: frame the whole tracking space
            if (shown > 0 && !_framedDevices && scene.Profile == null) _framedDevices = view.FrameDevices();
            if (shown == 0) _framedDevices = false;

            string info = devicesOnly ? "" : ShowVrChat(scene);
            if (devicesOnly && !connected)
            {
                scene.Overlay = scene.OverlayRight = "";
                view.SceneChanged();
                return;
            }

            string head = rec.IsRecording ? "● REC  " + TimeText.Format(rec.RecordingTime) : "LIVE";
            string steamVr = ov == null ? "SteamVR: off (Settings → Sources)" :
                !connected ? "SteamVR: " + (ov.Status ?? "").ToLowerInvariant() :
                "SteamVR: " + DeviceText.Summary(ov.Devices());
            if (!_live.CaptureOn) steamVr = "Live sources are off (started with --no-capture)";
            scene.Overlay = head + (info.Length > 0 ? "\n" + info : "") + "\n" + steamVr;
            scene.OverlayRight = devicesOnly ? "" : _live.WornAvatar;
            view.SceneChanged();
        }

        string ShowVrChat(ViewScene scene)
        {
            var rec = _live.Recorder;
            var match = _live.CurrentMatch();
            string key = rec.AvatarId + "|" + match?.Entry?.Id;
            if (key != _profileKey)
            {
                _profileKey = key;
                _profile = match?.Entry?.Profile();
            }

            double now = Clock.Now;
            if (rec.VrcHeadTime > 0 && now - rec.VrcHeadTime < 1)
            {
                scene.Markers.Add(new ViewMarker
                {
                    Pose = rec.VrcHead, Tint = MarkerTint.VrcPose, Kind = MarkerKind.VrcPose, Size = 1,
                    Label = "VRChat head"
                });
                scene.Markers.Add(new ViewMarker
                    { Pose = rec.VrcLeftWrist, Tint = MarkerTint.VrcPose, Kind = MarkerKind.VrcPose, Size = 1 });
                scene.Markers.Add(new ViewMarker
                    { Pose = rec.VrcRightWrist, Tint = MarkerTint.VrcPose, Kind = MarkerKind.VrcPose, Size = 1 });
            }

            if (rec.VrcCameraTime > 0 && now - rec.VrcCameraTime < 2)
                scene.Markers.Add(new ViewMarker
                {
                    Pose = rec.VrcCamera, Tint = MarkerTint.Camera, Kind = MarkerKind.Camera, Size = 1, Label = "camera"
                });

            string info = "";
            var cur = rec.Current;
            var prof = _profile ?? cur?.Profile;
            if (cur != null && cur.Humr.Count > 0 && prof != null)
            {
                if (scene.Profile != prof) scene.SetProfile(prof);
                Vec3 hips;
                Quat[] rot;
                lock (cur.Sync)
                {
                    int n = cur.Humr.Count - 1;
                    hips = cur.Humr.Hips[n];
                    rot = (Quat[])cur.Humr.Rot[n].Clone();
                }

                for (int b = 0; b < HumanBones.Count; b++)
                    if (rot[b].Norm < 0.5f)
                        rot[b] = prof.HumanRestRot(b);
                var pose = new BakedClip
                {
                    Profile = prof, Frames = 1, Fps = 1, Hips = new[] { hips }, Rot = new[] { rot },
                    BoneExists = prof.HumanExists
                };
                pose.EvaluateNodes(0, scene.NodePos, scene.NodeRot);
                // VRChat's world -> the SteamVR playspace the devices are drawn in (thumbstick moves, spawn point, tracking scale)
                if (now - _vrcRead > 5)
                {
                    _vrcRead = now;
                    var vs = VrcSettings.Read();
                    if (vs.PlayerHeight > 0.5f) _userEye = vs.PlayerHeight * CalibrationSettings.EyeToHeightRatio;
                }

                _map.Update(cur, prof, rec.AvatarEyeHeight > 0.2f ? rec.AvatarEyeHeight : prof.EyeHeight, _userEye);
                if (_map.Valid)
                {
                    for (int k = 0; k < scene.NodePos.Length; k++)
                    {
                        scene.NodePos[k] = _map.ToRaw(scene.NodePos[k]);
                        scene.NodeRot[k] = _map.ToRaw(scene.NodeRot[k]);
                    }

                    info = FormattableString.Invariant($"VRChat's IK over the devices (scale {_map.Scale:0.000})");
                }
                else info = "VRChat's IK (in VRChat's world)";

                scene.MeshScale = _map.Valid ? 1f / _map.Scale : 1f;
                scene.FaceWeights = _live.FaceValues();
            }
            else if (scene.Profile != null) scene.SetProfile(null);

            return info;
        }
    }
}
