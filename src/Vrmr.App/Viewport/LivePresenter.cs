using System;
using VRMR.App.Services;

namespace VRMR.App.Viewport
{
    /// <summary>
    ///     Shows the live avatar, tracked devices, play area and VRChat poses.
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

        internal LiveIkPreview Ik { get; init; }

        /// <summary>Fills the view with the live state only.</summary>
        public void Show(IViewport view)
        {
            var rec = _live.Recorder;
            var scene = view.Scene;
            var match = _live.CurrentMatch();
            string key = rec.AvatarId + "|" + match?.Entry?.Id;
            if (key != _profileKey)
            {
                _profileKey = key;
                _profile = match?.Entry?.Profile();
            }

            scene.Markers.Clear();
            var ov = rec.OpenVR;
            bool connected = ov != null && ov.Connected;
            var devices = connected ? ov.Devices() : null;
            OpenVRSource.Device headset = null;
            scene.PlayArea = connected ? ov.PlayArea : null;
            int shown = 0;
            if (connected)
                foreach (var d in devices)
                {
                    if (!d.HasPose) continue; // never tracked yet: listed in the Devices panel only
                    if (d.Class == 1 && d.Valid) headset = d;
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

            // first time devices show up without an avatar on screen: frame the whole tracking space
            if (shown > 0 && !_framedDevices && scene.Profile == null) _framedDevices = view.FrameDevices();
            if (shown == 0) _framedDevices = false;

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
            if (headset != null && prof != null)
            {
                bool frame = scene.Profile != prof;
                if (frame) scene.SetProfile(prof);
                if (Ik?.Show(scene, prof, ov, devices, now) == true) info = "IK solve";
                else ShowRest(scene, prof, headset);
                scene.FaceWeights = _live.FaceValues();
                if (frame) view.FrameAll();
            }
            else if (cur != null && cur.Humr.Count > 0 && prof != null)
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

            string head = rec.IsRecording ? "● REC  " + TimeText.Format(rec.RecordingTime) : "LIVE";
            string steamVr = ov == null ? "SteamVR: off (Settings → Sources)" :
                !connected ? "SteamVR: " + (ov.Status ?? "").ToLowerInvariant() :
                "SteamVR: " + DeviceText.Summary(devices);
            if (!_live.CaptureOn) steamVr = "Live sources are off (started with --no-capture)";
            scene.Overlay = head + (info.Length > 0 ? "\n" + info : "") + "\n" + steamVr;
            scene.OverlayRight = _live.WornAvatar;
            view.SceneChanged();
        }

        static void ShowRest(ViewScene scene, AvatarProfile profile, OpenVRSource.Device headset)
        {
            var yaw = Quat.YawRotation(headset.Rot.Yaw);
            var origin = headset.Pos - yaw * profile.ViewPoint;
            for (int i = 0; i < scene.NodePos.Length; i++)
            {
                scene.NodePos[i] = origin + yaw * profile.RestWorldPos[i];
                scene.NodeRot[i] = yaw * profile.RestWorldRot[i];
            }

            scene.MeshScale = profile.Scale;
        }
    }
}
