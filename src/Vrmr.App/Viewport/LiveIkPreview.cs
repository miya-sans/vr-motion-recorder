using System;
using System.Collections.Generic;
using System.Threading;
using VRMR.App.Services;

namespace VRMR.App.Viewport
{
    sealed class LiveIkPreview
    {
        readonly CalibrationLibrary _calibrations;
        readonly bool[] _driven = new bool[HumanBones.Count];
        readonly Lock _gate = new();
        readonly Vec3[] _positions = new Vec3[HumanBones.Count];
        readonly Quat[] _rotations = new Quat[HumanBones.Count];
        readonly Action<LiveIkPreview> _solve = static preview => preview.Solve();
        readonly SettingsStore _settings;
        readonly Dictionary<string, PoseStream> _streams = [];
        bool _busy, _hasResult;
        BakedClip _clip, _result;
        List<OpenVRSource.Device> _devices;
        AvatarProfile _profile;
        float _resultScale;
        int _revision, _configuredRevision = -1, _resultRevision;
        TrackingRig _rig;
        IBodySolver _solver;
        OpenVRSource _source;
        double _time, _lastTime;
        Pose _toRaw;

        public LiveIkPreview(CalibrationLibrary calibrations, SettingsStore settings)
        {
            _calibrations = calibrations;
            _settings = settings;
            calibrations.Changed += Invalidate;
            settings.Changed += what =>
            {
                if (what == nameof(Settings.CalibrationDefaults)) Invalidate();
            };
        }

        void Invalidate()
        {
            Interlocked.Increment(ref _revision);
        }

        public bool Show(ViewScene scene, AvatarProfile profile, OpenVRSource source,
            List<OpenVRSource.Device> devices, double time)
        {
            lock (_gate)
            {
                bool ready = _hasResult && _result.Profile == profile && _source == source &&
                             _resultRevision == Volatile.Read(ref _revision);
                if (ready)
                {
                    _result.EvaluateNodes(0, scene.NodePos, scene.NodeRot);
                    for (int i = 0; i < scene.NodePos.Length; i++)
                    {
                        scene.NodePos[i] = _toRaw.TransformPoint(scene.NodePos[i]) * _resultScale;
                        scene.NodeRot[i] = _toRaw.rot * scene.NodeRot[i];
                    }

                    scene.MeshScale = _result.Scale * _resultScale;
                }

                bool changed = _source != source || _profile != profile ||
                               _resultRevision != Volatile.Read(ref _revision);
                if (!_busy && (ready || changed || time - _time >= 0.25))
                {
                    _busy = true;
                    if (_source != source || _profile != profile) Invalidate();
                    _source = source;
                    _profile = profile;
                    _devices = devices;
                    _time = time;
                    ThreadPool.UnsafeQueueUserWorkItem(_solve, this, preferLocal: false);
                }

                return ready;
            }
        }

        void Solve()
        {
            bool solved = false;
            int revision = Volatile.Read(ref _revision);
            try
            {
                if (_configuredRevision != revision || DevicesChanged()) Configure(revision);
                if (_solver == null) return;
                foreach (var device in _devices)
                    if (_streams.TryGetValue(device.Serial, out var stream))
                    {
                        stream.T.Clear();
                        stream.P.Clear();
                        stream.R.Clear();
                        if (device.Valid) stream.Add(0, device.Pos, device.Rot);
                    }

                _rig.Feed(_solver, 0);
                _solver.Solve((float)Math.Clamp(_time - _lastTime, 1.0 / 120, 0.1));
                _solver.GetPose(_positions, _rotations);
                var rot = _clip.Rot[0];
                for (int b = 0; b < HumanBones.Count; b++)
                {
                    var q = _rotations[b];
                    bool driven = _clip.BoneExists[b] && q.IsFinite && q.Norm > 0.5f &&
                                  !HumanBones.IsFinger(b) && b != HumanBones.LeftEye &&
                                  b != HumanBones.RightEye && b != HumanBones.Jaw;
                    _driven[b] = driven;
                    rot[b] = driven ? q.Normalized : Quat.Identity;
                }

                PoseBuilder.FillUndriven(_profile, rot, _driven);
                _clip.Hips[0] = _positions[HumanBones.Hips];
                solved = true;
            }
            catch (Exception ex)
            {
                _solver = null;
                AppLog.Warn("Live IK: " + ex.Message);
            }
            finally
            {
                _lastTime = _time;
                lock (_gate)
                {
                    if (solved)
                    {
                        (_clip, _result) = (_result, _clip);
                        _toRaw = _rig.RoomAt(0, 0).Inverse;
                        _resultScale = 1f / _rig.TrackScale;
                    }

                    _hasResult = solved;
                    _resultRevision = revision;
                    _busy = false;
                }
            }
        }

        bool DevicesChanged()
        {
            int count = 0;
            foreach (var device in _devices)
            {
                if (!IsInput(device)) continue;
                count++;
                if (!_streams.TryGetValue(device.Serial, out var stream) ||
                    stream.ControllerRole != device.Role || stream.Info.GetStr("trackerRole", "") != device.TrackerRole)
                    return true;
            }

            return count != _streams.Count;
        }

        static bool IsInput(OpenVRSource.Device device)
        {
            return device.HasPose && device.Class is 1 or 2 or 3;
        }

        void Configure(int revision)
        {
            _configuredRevision = revision;
            _solver = null;
            _streams.Clear();
            var rec = new Recording();
            foreach (var device in _devices)
            {
                if (!IsInput(device)) continue;
                var stream = new PoseStream
                {
                    Kind = StreamKind.OpenVRDevice,
                    Name = device.Serial,
                    Info =
                    {
                        ["class"] = device.Class, ["role"] = device.Role,
                        ["model"] = device.Model,
                        ["ctype"] = string.IsNullOrEmpty(device.TrackerRole) ? device.ControllerType : device.TrackerRole,
                        ["trackerRole"] = device.TrackerRole
                    }
                };
                // The rig discovers devices from streams with at least three samples.
                for (int i = 0; i < 3; i++) stream.Add(i / 60.0, device.Pos, device.Rot);
                _streams.Add(device.Serial, stream);
                rec.Streams.Add(stream);
            }

            string session = CalibrationLibrary.CurrentSteamVRSession();
            foreach (var snapshot in _calibrations.List())
            {
                if (snapshot.Hmd == null || snapshot.SteamVRSession != session) continue;
                var cal = _settings.Value.CalibrationDefaults.Clone();
                float scale = cal.AvatarScale > 0.01f ? cal.AvatarScale : 1f;
                var solver = BodySolvers.Factory?.Invoke();
                if (solver == null) return;
                _rig = new TrackingRig(rec, _profile, scale, cal, new CalibrationSource { Snapshot = snapshot });
                solver.Init(_profile, scale, _rig.RealEyeHeight);
                solver.Configure(cal);
                _rig.Calibrate(solver);
                _clip = CreateClip(scale);
                lock (_gate)
                {
                    _hasResult = false;
                    _result = CreateClip(scale);
                }

                _solver = solver;
                return;
            }
        }

        BakedClip CreateClip(float scale)
        {
            return new BakedClip
            {
                Profile = _profile,
                Frames = 1,
                Scale = _profile.Scale * scale,
                BoneExists = _profile.HumanExists,
                Hips = new Vec3[1],
                Rot = [new Quat[HumanBones.Count]]
            };
        }
    }
}
