using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace VRMR
{
    /// <summary>
    ///     Reads every SteamVR tracked device as a background OpenVR app (never starts or blocks SteamVR).
    ///     Uses SteamVR's own openvr_api.dll (located through openvrpaths.vrpath) and the IVRSystem_026 function table.
    ///     Poses are converted to Unity space (left-handed) in the standing universe.
    /// </summary>
    public sealed class OpenVRSource : IDisposable
    {
        /// <summary>Which devices a snapshot holds.</summary>
        public enum DeviceFilter
        {
            /// <summary>Every connected device, base stations and currently untracked devices included.</summary>
            Connected,

            /// <summary>Headset, controllers and trackers with a valid pose right now.</summary>
            TrackedBody
        }

        /// <summary>The most devices SteamVR reports: a snapshot buffer of this size always fits.</summary>
        public const int MaxDevices = 64;

        const int AppUtility = 4, AppBackground = 3;
        const int UniverseStanding = 1;

        static readonly string[] ControllerTypes =
        {
            "knuckles", "vive_controller", "oculus_touch", "holographic_controller", "vive_cosmos_controller",
            "hpmotioncontroller", "pico_controller", "generic"
        };

        static readonly uint ActionSetSize = (uint)Marshal.SizeOf(typeof(ActiveActionSet)),
            SkeletalDataSize = (uint)Marshal.SizeOf(typeof(SkeletalActionData)),
            AnalogDataSize = (uint)Marshal.SizeOf(typeof(AnalogData));

        public readonly double[] LiveFingerTime = { -1, -1 };
        public readonly float[][] LiveFingers = { new float[9], new float[9] };

        /// <summary>SteamVR skeletal wrist (bone 1, parent space, raw OpenVR) per hand: the controller -> wrist offset.</summary>
        public readonly Vec3[] LiveWristPos = new Vec3[2];

        public readonly Quat[] LiveWristRot = { Quat.Identity, Quat.Identity };
        public readonly double[] LiveWristTime = { -1, -1 };

        /// <summary>Trigger pull 0..1 per hand (read-only SteamVR Input action; also delivered while VRChat has focus).</summary>
        public readonly float[] Trigger = new float[2];

        public readonly double[] TriggerTime = { -1, -1 };
        readonly BoneTransform[] _boneBuf = new BoneTransform[HandStream.BoneCount];

        readonly Device[] _devices = new Device[MaxDevices];

        readonly short[] _handPacked = new short[HandStream.PackedLength];

        // reused for every hand sample (see OnHand)
        readonly float[] _handSummary = new float[9],
            _handRot = new float[HandStream.BoneCount * 4],
            _handRoot = new float[3],
            _handWrist = new float[3];

        readonly ActiveActionSet[] _sets = new ActiveActionSet[1];
        readonly object _snapLock = new();
        readonly ManualResetEventSlim _stopSignal = new(false);
        readonly Thread _thread;
        public volatile bool CaptureFingers = true;

        public volatile bool Connected;
        public string InputStatus = "off";
        public double LastPoll;

        /// <summary>Called when a device appears or its role changes.</summary>
        public Action<Device> OnDevice;

        public Action OnFrame;

        /// <summary>
        ///     (side 0/1, time, summary curl[5]+splay[4], packed 31-bone skeleton) on the poll thread. The two arrays are
        ///     reused for the next sample: copy what is kept.
        /// </summary>
        public Action<int, double, float[], short[]> OnHand;

        /// <summary>Called on the poll thread for each valid device sample: (device, time).</summary>
        public Action<Device, double> OnPose;

        /// <summary>
        ///     Called on the poll thread when a device's tracking changes: (device, time, ETrackingResult while the pose is
        ///     valid; 0 = invalid pose; -1 = disconnected).
        /// </summary>
        public Action<Device, double, int> OnTracking;

        /// <summary>(time, left, right) after every input poll that had trigger data.</summary>
        public Action<double, float, float> OnTriggers;

        /// <summary>Play area (chaperone) corners on the floor in Unity space, or null when SteamVR has none.</summary>
        public volatile Vec3[] PlayArea;

        public int Rate;
        public long Samples;
        public string Status = "Not started";
        public bool UseUtilityApp = true;
        FnAckQuit _ackQuit;
        ulong _actionSet, _skelL, _skelR, _trigL, _trigR;
        FnAnalog _analog;
        FnBones _bones;
        FnGetBool _getBool;
        FnGetClass _getClass;
        FnGetFloat _getFloat;
        FnGetInterface _getIface;
        FnGetPoses _getPoses;
        FnGetRole _getRole;
        FnGetString _getString;

        FnInit _init;
        bool _inputOk;
        int _inputTick, _badHandlePolls;
        FnPlayAreaRect _playAreaRect;
        FnPollEvent _poll;
        FnShutdown _shutdown;
        FnSkelActionData _skelData;
        volatile bool _stop;
        FnSummary _summary;

        // SteamVR "Manage Trackers" roles live in Steam\config\steamvr.vrsettings: "trackers": { "/devices/htc/vive_trackerLHR-XXXX": "TrackerRole_Waist" }
        Dictionary<string, object> _trackerRoles;
        FnUpdateActions _updateActions;

        string _vrsettingsFile;
        DateTime _vrsettingsStamp;

        public OpenVRSource(int rate)
        {
            Rate = Math.Max(30, Math.Min(250, rate));
            _thread = new Thread(Loop)
                { IsBackground = true, Name = "OpenVR poll", Priority = ThreadPriority.AboveNormal };
        }

        public void Dispose()
        {
            _stop = true;
            _stopSignal.Set();
            if (_thread.IsAlive) _thread.Join(1500);
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibraryExW(string path, IntPtr h, uint flags);

        [DllImport("kernel32", CharSet = CharSet.Ansi)]
        static extern IntPtr GetProcAddress(IntPtr h, string name);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWaitableTimerExW(IntPtr attr, string name, uint flags, uint access);

        [DllImport("kernel32", SetLastError = true)]
        static extern bool SetWaitableTimer(IntPtr h, ref long due, int period, IntPtr cb, IntPtr arg, bool resume);

        [DllImport("kernel32")]
        static extern uint WaitForSingleObject(IntPtr h, uint ms);

        [DllImport("kernel32")]
        static extern bool CloseHandle(IntPtr h);

        public void Start()
        {
            _thread.Start();
        }

        /// <summary>
        ///     Reports devices as one poll of SteamVR would, on a source that was never started: for tests and demos, which
        ///     have no SteamVR. Devices are matched by <see cref="Device.Index" />; one missing from the list counts as
        ///     switched off. The same callbacks run as for real devices, in the same order.
        /// </summary>
        public void Feed(IReadOnlyList<Device> devices, Vec3[] playArea, double time)
        {
            if (_thread.IsAlive) throw new InvalidOperationException("Feed is for a source that was not started.");
            Connected = true;
            Status = "Simulated devices";
            PlayArea = playArea;
            LastPoll = time;
            bool[] seen = new bool[MaxDevices];
            foreach (var given in devices)
            {
                int i = given.Index;
                if (i < 0 || i >= MaxDevices) continue;
                seen[i] = true;
                var d = _devices[i];
                if (d == null)
                {
                    d = given.Clone();
                    d.Valid = false;
                    d.HasPose = false;
                    d.LastCode = int.MinValue;
                    if (string.IsNullOrEmpty(d.Serial)) d.Serial = "device" + i;
                    lock (_snapLock) _devices[i] = d;
                    if (!d.IsBaseStation) OnDevice?.Invoke(d);
                }
                else if (given.Role != d.Role)
                {
                    d.Role = given.Role;
                    if (!d.IsBaseStation) OnDevice?.Invoke(d);
                }

                int result = given.Result == 0 ? 200 : given.Result;
                int code = given.Valid ? result : 0;
                if (code != d.LastCode && !d.IsBaseStation)
                {
                    d.LastCode = code;
                    OnTracking?.Invoke(d, time, code);
                }

                d.Result = result;
                d.Battery = given.Battery;
                if (!given.Valid || d.Class == 0 || d.Class == 5)
                {
                    d.Valid = false;
                    continue;
                }

                lock (_snapLock)
                {
                    d.Pos = given.Pos;
                    d.Rot = given.Rot;
                    d.Valid = true;
                    d.HasPose = true;
                }

                if (d.IsBaseStation) continue;
                Samples++;
                OnPose?.Invoke(d, time);
            }

            for (int i = 0; i < MaxDevices; i++)
            {
                var d = _devices[i];
                if (d == null || seen[i]) continue;
                if (!d.IsBaseStation) OnTracking?.Invoke(d, time, -1);
                lock (_snapLock) _devices[i] = null;
            }

            OnFrame?.Invoke();
        }

        /// <summary>Tracked body devices (HMD, controllers, trackers) with a valid pose right now.</summary>
        public List<Device> Snapshot()
        {
            var list = new List<Device>();
            lock (_snapLock)
                foreach (var d in _devices)
                    if (d != null && d.Valid && !d.IsBaseStation)
                        list.Add(d.Clone());
            return list;
        }

        /// <summary>Every connected device, base stations and currently untracked devices included.</summary>
        public List<Device> Devices()
        {
            var list = new List<Device>();
            lock (_snapLock)
                foreach (var d in _devices)
                    if (d != null && d.Class != 0)
                        list.Add(d.Clone());
            return list;
        }

        /// <summary>
        ///     Fills a caller-owned buffer with the current state of the devices and returns how many were written. Slots
        ///     that are still null get a Device; after that the same objects are refreshed, so a view that calls this on
        ///     every tick allocates nothing. A buffer of <see cref="MaxDevices" /> always fits.
        /// </summary>
        public int CopyDevices(Device[] buffer, DeviceFilter filter = DeviceFilter.Connected)
        {
            int n = 0;
            lock (_snapLock)
                foreach (var d in _devices)
                {
                    if (d == null || n >= buffer.Length) continue;
                    if (filter == DeviceFilter.TrackedBody ? !d.Valid || d.IsBaseStation : d.Class == 0) continue;
                    (buffer[n] ?? (buffer[n] = new Device())).CopyFrom(d);
                    n++;
                }

            return n;
        }

        /// <summary>The installed SteamVR runtime's folder (see <see cref="SteamVRLocator" />), or null.</summary>
        public static string RuntimePath()
        {
            return SteamVRLocator.RuntimeDir(AppPaths.Current.Machine);
        }

        bool LoadApi()
        {
            if (_init != null) return true;
            string rt = RuntimePath();
            if (rt == null)
            {
                Status = "SteamVR not installed";
                return false;
            }

            string dll = Path.Combine(rt, "bin", "win64", "openvr_api.dll");
            if (!File.Exists(dll))
            {
                Status = "openvr_api.dll not found";
                return false;
            }

            IntPtr h = LoadLibraryExW(dll, IntPtr.Zero, 0x8 /*LOAD_WITH_ALTERED_SEARCH_PATH*/);
            if (h == IntPtr.Zero)
            {
                Status = "Failed to load openvr_api.dll";
                return false;
            }

            _init = Marshal.GetDelegateForFunctionPointer<FnInit>(GetProcAddress(h, "VR_InitInternal2"));
            _getIface = Marshal.GetDelegateForFunctionPointer<FnGetInterface>(GetProcAddress(h,
                "VR_GetGenericInterface"));
            _shutdown = Marshal.GetDelegateForFunctionPointer<FnShutdown>(GetProcAddress(h, "VR_ShutdownInternal"));
            return true;
        }

        static bool SteamVRRunning()
        {
            return SteamVRLocator.IsRunning(AppPaths.Current.Machine);
        }

        bool Connect()
        {
            if (!LoadApi()) return false;
            int err = 0;
            // never start SteamVR: a utility app (needed for skeletal input) only connects to an already running vrserver
            bool utility = UseUtilityApp;
            if (!SteamVRRunning())
            {
                SetStatus("Waiting for SteamVR");
                return false;
            }

            _init(ref err, utility ? AppUtility : AppBackground, IntPtr.Zero);
            if (err != 0)
            {
                // 121 = no server for background app, 108 = HMD not found, 110 = no connection
                SetStatus(
                    err == 121 || err == 110 ? "Waiting for SteamVR" :
                    err == 108 ? "SteamVR: headset not found" : "SteamVR init error " + err, true);
                return false;
            }

            err = 0;
            // function tables verified against openvr.h (IVRSystem_026, IVRChaperone_004)
            IntPtr tbl = _getIface("FnTable:IVRSystem_026", ref err);
            if (tbl == IntPtr.Zero || err != 0)
            {
                SetStatus("IVRSystem_026 unavailable (" + err + ")", true);
                _shutdown();
                return false;
            }

            T Fn<T>(IntPtr t, int idx)
            {
                return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(t, idx * IntPtr.Size));
            }

            _getPoses = Fn<FnGetPoses>(tbl, 12);
            _getRole = Fn<FnGetRole>(tbl, 19);
            _getClass = Fn<FnGetClass>(tbl, 20);
            _getBool = Fn<FnGetBool>(tbl, 22);
            _getFloat = Fn<FnGetFloat>(tbl, 23);
            _getString = Fn<FnGetString>(tbl, 28);
            _poll = Fn<FnPollEvent>(tbl, 30);
            _ackQuit = Fn<FnAckQuit>(tbl, 47);
            err = 0;
            IntPtr chap = _getIface("FnTable:IVRChaperone_004", ref err);
            _playAreaRect = chap != IntPtr.Zero && err == 0 ? Fn<FnPlayAreaRect>(chap, 2) : null;
            lock (_snapLock) Array.Clear(_devices, 0, _devices.Length);
            PlayArea = null;
            _vrsettingsFile = null;
            RefreshTrackerRoles();
            Connected = true;
            SetStatus("Connected");
            AppLog.Info("SteamVR connected (" + (utility ? "utility" : "background") + " app)");
            _inputOk = false;
            InitInput();
            return true;
        }

        // ------------------------------------------------------------------ skeletal input

        static IntPtr Utf8(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s + "\0");
            IntPtr p = Marshal.AllocHGlobal(b.Length);
            Marshal.Copy(b, 0, p, b.Length);
            return p;
        }

        /// <summary>
        ///     Where the action manifest goes. SteamVR reports #ActionManifestError_FileNotFound for manifests in the
        ///     per-user app data folders (verified on SteamVR 2.18), so <see cref="AppPaths" /> picks a folder SteamVR can read.
        /// </summary>
        static string ManifestDir()
        {
            return AppPaths.Current.WritableSteamVRManifestDir();
        }

        /// <summary>Skeletons (finger tracking) and, read-only, the triggers (to see VRChat's calibrate confirm).</summary>
        static Dictionary<string, object> SetBindings(string ct)
        {
            var d = new Dictionary<string, object>
            {
                ["skeleton"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["output"] = "/actions/vrmr/in/lefthandskeleton",
                        ["path"] = "/user/hand/left/input/skeleton/left"
                    },
                    new Dictionary<string, object>
                    {
                        ["output"] = "/actions/vrmr/in/righthandskeleton",
                        ["path"] = "/user/hand/right/input/skeleton/right"
                    }
                }
            };
            if (ct != "generic")
                d["sources"] = new List<object>
                {
                    Trig("left"), Trig("right")
                };
            return d;
        }

        static Dictionary<string, object> Trig(string side)
        {
            return new Dictionary<string, object>
            {
                ["path"] = "/user/hand/" + side + "/input/trigger", ["mode"] = "trigger",
                ["inputs"] = new Dictionary<string, object>
                    { ["pull"] = new Dictionary<string, object> { ["output"] = "/actions/vrmr/in/trigger" + side } }
            };
        }

        static string WriteActionManifest()
        {
            string dir = ManifestDir();
            Directory.CreateDirectory(dir);
            var defaults = new List<object>();
            foreach (string ct in ControllerTypes)
            {
                string file = "bindings_" + ct + ".json";
                var binding = new Dictionary<string, object>
                {
                    ["action_manifest_version"] = 0, ["controller_type"] = ct, ["name"] = "VR Motion Recorder " + ct,
                    ["description"] = "Finger tracking capture",
                    ["bindings"] = new Dictionary<string, object>
                    {
                        ["/actions/vrmr"] = SetBindings(ct)
                    }
                };
                File.WriteAllText(Path.Combine(dir, file), Json.Serialize(binding, true));
                defaults.Add(new Dictionary<string, object> { ["controller_type"] = ct, ["binding_url"] = file });
            }

            var manifest = new Dictionary<string, object>
            {
                ["default_bindings"] = defaults,
                ["actions"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["name"] = "/actions/vrmr/in/lefthandskeleton", ["type"] = "skeleton",
                        ["skeleton"] = "/skeleton/hand/left"
                    },
                    new Dictionary<string, object>
                    {
                        ["name"] = "/actions/vrmr/in/righthandskeleton", ["type"] = "skeleton",
                        ["skeleton"] = "/skeleton/hand/right"
                    },
                    new Dictionary<string, object> { ["name"] = "/actions/vrmr/in/triggerleft", ["type"] = "vector1" },
                    new Dictionary<string, object> { ["name"] = "/actions/vrmr/in/triggerright", ["type"] = "vector1" }
                },
                ["action_sets"] = new List<object>
                    { new Dictionary<string, object> { ["name"] = "/actions/vrmr", ["usage"] = "leftright" } },
                ["localization"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["language_tag"] = "en_US", ["/actions/vrmr"] = "VR Motion Recorder",
                        ["/actions/vrmr/in/lefthandskeleton"] = "Left hand skeleton",
                        ["/actions/vrmr/in/righthandskeleton"] = "Right hand skeleton",
                        ["/actions/vrmr/in/triggerleft"] = "Left trigger (read only)",
                        ["/actions/vrmr/in/triggerright"] = "Right trigger (read only)"
                    }
                }
            };
            string path = Path.Combine(dir, "actions.json");
            File.WriteAllText(path, Json.Serialize(manifest, true));
            return path;
        }

        void InitInput()
        {
            try
            {
                int err = 0;
                IntPtr tbl = _getIface("FnTable:IVRInput_011", ref err);
                if (tbl == IntPtr.Zero || err != 0)
                {
                    InputStatus = "IVRInput unavailable (" + err + ")";
                    return;
                }

                T Fn<T>(int idx)
                {
                    return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(tbl, idx * IntPtr.Size));
                }

                var setManifest = Fn<FnSetManifest>(0);
                var getSet = Fn<FnGetHandle>(1);
                var getAction = Fn<FnGetHandle>(2);
                _updateActions = Fn<FnUpdateActions>(4);
                _skelData = Fn<FnSkelActionData>(9);
                _bones = Fn<FnBones>(19);
                _summary = Fn<FnSummary>(20);
                _analog = Fn<FnAnalog>(6);
                IntPtr p = Utf8(WriteActionManifest());
                int e = setManifest(p);
                Marshal.FreeHGlobal(p);
                if (e != 0)
                {
                    InputStatus = "action manifest rejected (" + e + ")";
                    AppLog.Warn("SteamVR input: " + InputStatus);
                    return;
                }

                IntPtr n = Utf8("/actions/vrmr");
                getSet(n, ref _actionSet);
                Marshal.FreeHGlobal(n);
                n = Utf8("/actions/vrmr/in/lefthandskeleton");
                getAction(n, ref _skelL);
                Marshal.FreeHGlobal(n);
                n = Utf8("/actions/vrmr/in/righthandskeleton");
                getAction(n, ref _skelR);
                Marshal.FreeHGlobal(n);
                n = Utf8("/actions/vrmr/in/triggerleft");
                getAction(n, ref _trigL);
                Marshal.FreeHGlobal(n);
                n = Utf8("/actions/vrmr/in/triggerright");
                getAction(n, ref _trigR);
                Marshal.FreeHGlobal(n);
                _inputOk = _actionSet != 0 && _skelL != 0 && _skelR != 0;
                InputStatus = _inputOk ? "waiting for hands" : "action handles missing";
                AppLog.Info("SteamVR skeletal input: " + InputStatus);
            }
            catch (Exception ex)
            {
                InputStatus = "input init failed: " + ex.Message;
                AppLog.Warn(InputStatus);
            }
        }

        void PollInput(double t)
        {
            if (!_inputOk || (++_inputTick & 1) != 0) return; // ~45 Hz is plenty for fingers
            try
            {
                _sets[0] = new ActiveActionSet { set = _actionSet };
                if (_updateActions(_sets, ActionSetSize, 1) != 0) return;
                int active = 0, invalid = 0;
                for (int side = 0; CaptureFingers && side < 2; side++)
                {
                    ulong h = side == 0 ? _skelL : _skelR;
                    var ad = new SkeletalActionData();
                    int se = _skelData(h, ref ad, SkeletalDataSize);
                    if (se == 3) invalid++; // InvalidHandle: SteamVR didn't load our action manifest
                    if (se != 0 || !ad.active) continue;
                    var sd = new SummaryData();
                    if (_summary(h, 1 /*FromDevice*/, ref sd) != 0 &&
                        _summary(h, 0 /*FromAnimation*/, ref sd) != 0) continue;
                    if (_bones(h, 1 /*Parent*/, 0 /*WithController*/, _boneBuf, HandStream.BoneCount) != 0) continue;
                    float[] sum = _handSummary;
                    float[] rot = _handRot;
                    short[] packed = _handPacked;
                    sum[0] = sd.c0;
                    sum[1] = sd.c1;
                    sum[2] = sd.c2;
                    sum[3] = sd.c3;
                    sum[4] = sd.c4;
                    sum[5] = sd.s0;
                    sum[6] = sd.s1;
                    sum[7] = sd.s2;
                    sum[8] = sd.s3;
                    for (int b = 0; b < HandStream.BoneCount; b++)
                    {
                        var bt = _boneBuf[b];
                        rot[b * 4] = bt.qx;
                        rot[b * 4 + 1] = bt.qy;
                        rot[b * 4 + 2] = bt.qz;
                        rot[b * 4 + 3] = bt.qw;
                    }

                    _handRoot[0] = _boneBuf[0].px;
                    _handRoot[1] = _boneBuf[0].py;
                    _handRoot[2] = _boneBuf[0].pz;
                    _handWrist[0] = _boneBuf[1].px;
                    _handWrist[1] = _boneBuf[1].py;
                    _handWrist[2] = _boneBuf[1].pz;
                    HandStream.Pack(rot, _handRoot, _handWrist, packed);
                    Array.Copy(sum, LiveFingers[side], 9);
                    LiveFingerTime[side] = t;
                    LiveWristPos[side] = new Vec3(_boneBuf[1].px, _boneBuf[1].py, _boneBuf[1].pz);
                    LiveWristRot[side] = new Quat(_boneBuf[1].qx, _boneBuf[1].qy, _boneBuf[1].qz, _boneBuf[1].qw)
                        .Normalized;
                    LiveWristTime[side] = t;
                    active++;
                    OnHand?.Invoke(side, t, sum, packed);
                }

                if (_trigL != 0 && _trigR != 0)
                {
                    var al = new AnalogData();
                    var ar = new AnalogData();
                    uint asz = AnalogDataSize;
                    if (_analog(_trigL, ref al, asz, 0) == 0 && _analog(_trigR, ref ar, asz, 0) == 0 &&
                        (al.active || ar.active))
                    {
                        Trigger[0] = al.active ? al.x : 0;
                        Trigger[1] = ar.active ? ar.x : 0;
                        TriggerTime[0] = TriggerTime[1] = t;
                        OnTriggers?.Invoke(t, Trigger[0], Trigger[1]);
                    }
                }

                _badHandlePolls = invalid == 2 ? _badHandlePolls + 1 : 0;
                if (_badHandlePolls == 90) // ~2 s
                    AppLog.Warn(
                        "SteamVR finger tracking: SteamVR did not load the input manifest (see Steam\\logs\\vrserver.txt, 'Error loading action manifest')");
                InputStatus = active == 2 ? "fingers: 2 hand(s)" :
                    active == 1 ? "fingers: 1 hand(s)" :
                    _badHandlePolls >= 90 ? "SteamVR rejected the input manifest" : "waiting for hands";
            }
            catch (Exception ex)
            {
                _inputOk = false;
                InputStatus = "input error: " + ex.Message;
                AppLog.Warn(InputStatus);
            }
        }

        void SetStatus(string s, bool log = false)
        {
            if (s == Status) return;
            Status = s;
            if (log) AppLog.Warn(s);
        }

        void Disconnect(string why)
        {
            if (!Connected) return;
            Connected = false;
            try { _shutdown(); }
            catch { }

            lock (_snapLock) Array.Clear(_devices, 0, _devices.Length);
            PlayArea = null;
            Status = why;
            AppLog.Info("SteamVR disconnected: " + why);
        }

        string Prop(uint idx, int prop)
        {
            var sb = new StringBuilder(256);
            int err = 0;
            try { _getString(idx, prop, sb, 256, ref err); }
            catch { return ""; }

            return err == 0 ? sb.ToString() : "";
        }

        float PropF(uint idx, int prop, float def)
        {
            int err = 0;
            try
            {
                float v = _getFloat(idx, prop, ref err);
                return err == 0 ? v : def;
            }
            catch { return def; }
        }

        bool PropB(uint idx, int prop)
        {
            int err = 0;
            try
            {
                bool v = _getBool(idx, prop, ref err);
                return err == 0 && v;
            }
            catch { return false; }
        }

        /// <summary>Static properties (once per device) and the slowly changing ones (battery, charging; every ~2 s).</summary>
        void ReadProps(Device d, bool full)
        {
            uint i = (uint)d.Index;
            if (full)
            {
                d.Wireless = PropB(i, 1010);
                if (d.IsBaseStation)
                {
                    d.FovL = PropF(i, 4000, 0);
                    d.FovR = PropF(i, 4001, 0);
                    d.FovT = PropF(i, 4002, 0);
                    d.FovB = PropF(i, 4003, 0);
                    d.RangeMin = PropF(i, 4004, 0);
                    d.RangeMax = PropF(i, 4005, 0);
                    d.ModeLabel = Prop(i, 4006);
                }
            }

            if (d.Class == 3) d.TrackerRole = TrackerRoleOf(d.Serial);
            if (!d.IsBaseStation && PropB(i, 1026))
            {
                d.Battery = MathX.Clamp01(PropF(i, 1012, -1));
                d.Charging = PropB(i, 1011);
            }
        }

        void RefreshTrackerRoles()
        {
            try
            {
                // located once per connection: the file's place does not change while SteamVR runs
                string f = _vrsettingsFile ??
                           (_vrsettingsFile = SteamVRLocator.SettingsFile(AppPaths.Current.Machine) ?? "");
                if (f.Length == 0 || !File.Exists(f))
                {
                    _trackerRoles = null;
                    return;
                }

                var stamp = File.GetLastWriteTimeUtc(f);
                if (stamp == _vrsettingsStamp && _trackerRoles != null) return;
                _vrsettingsStamp = stamp;
                _trackerRoles = Json.ParseObject(File.ReadAllText(f))?.GetObj("trackers") ??
                                new Dictionary<string, object>();
            }
            catch { }
        }

        string TrackerRoleOf(string serial)
        {
            if (_trackerRoles == null || string.IsNullOrEmpty(serial)) return "";
            foreach (var kv in _trackerRoles)
                if (kv.Key.EndsWith(serial, StringComparison.OrdinalIgnoreCase) && kv.Value is string v)
                {
                    v = v.Replace("TrackerRole_", "");
                    if (v == "None" || v == "Handed" || v.Length == 0) return "";
                    return Regex.Replace(v, "(?<=[a-z])(?=[A-Z])", " ");
                }

            return "";
        }

        void ReadPlayArea()
        {
            if (_playAreaRect == null) return;
            try
            {
                var q = new HmdQuad();
                if (!_playAreaRect(ref q))
                {
                    PlayArea = null;
                    return;
                }

                var c = new[]
                {
                    new Vec3(q.x0, q.y0, -q.z0), new Vec3(q.x1, q.y1, -q.z1), new Vec3(q.x2, q.y2, -q.z2),
                    new Vec3(q.x3, q.y3, -q.z3)
                };
                float area = 0;
                for (int k = 0; k < 4; k++) area += c[k].x * c[(k + 1) % 4].z - c[(k + 1) % 4].x * c[k].z;
                PlayArea = Math.Abs(area) > 0.01f ? c : null;
            }
            catch { PlayArea = null; }
        }

        void Loop()
        {
            EfficiencyMode.ExemptCurrentThread();
            var poses = new TrackedDevicePose[MaxDevices];
            float[] m = new float[12];
            IntPtr evt = Marshal.AllocHGlobal(1024);
            IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /*HIGH_RESOLUTION*/, 0x1F0003);
            if (timer == IntPtr.Zero) timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, 0x1F0003);
            double next = Clock.Now;
            double lastRoleCheck = 0, lastPlayArea = 0;
            try
            {
                while (!_stop)
                {
                    if (!Connected)
                    {
                        if (!Connect())
                        {
                            _stopSignal.Wait(3000);
                            continue;
                        } // sleeps the whole three seconds unless the source is stopped

                        next = Clock.Now;
                    }

                    // events (quit / device changes)
                    bool quit = false;
                    try
                    {
                        while (_poll(evt, 64))
                        {
                            uint type = (uint)Marshal.ReadInt32(evt, 0);
                            int dev = Marshal.ReadInt32(evt, 4);
                            if (type == 700) quit = true;
                            else if ((type == 100 || type == 101 || type == 108) && dev >= 0 && dev < MaxDevices)
                                lock (_snapLock)
                                    _devices[dev] = null; // re-query properties
                            else if (type >= 800 && type <= 807) lastPlayArea = -100; // chaperone / room setup changed
                        }
                    }
                    catch { }

                    if (quit)
                    {
                        try { _ackQuit(); }
                        catch { }

                        Disconnect("SteamVR exited");
                        continue;
                    }

                    double t = Clock.Now;
                    try { _getPoses(UniverseStanding, 0f, poses, MaxDevices); }
                    catch (Exception ex)
                    {
                        Disconnect("poll failed: " + ex.Message);
                        continue;
                    }

                    LastPoll = t;
                    bool recheck = t - lastRoleCheck > 2.0;
                    if (recheck) lastRoleCheck = t;
                    if (recheck && (PlayArea == null || t - lastPlayArea > 10))
                    {
                        lastPlayArea = t;
                        ReadPlayArea();
                    }

                    if (recheck) RefreshTrackerRoles();
                    for (int i = 0; i < MaxDevices; i++)
                    {
                        var p = poses[i];
                        var d = _devices[i];
                        if (!p.connected)
                        {
                            if (d != null)
                            {
                                if (!d.IsBaseStation) OnTracking?.Invoke(d, t, -1);
                                lock (_snapLock) _devices[i] = null;
                            }

                            continue;
                        }

                        if (d == null)
                        {
                            d = new Device
                            {
                                Index = i, Class = _getClass((uint)i), Role = _getRole((uint)i),
                                Serial = Prop((uint)i, 1002), Model = Prop((uint)i, 1001),
                                ControllerType = Prop((uint)i, 7000),
                                TrackingSystem = Prop((uint)i, 1000), Manufacturer = Prop((uint)i, 1005),
                                RenderModel = Prop((uint)i, 1003)
                            };
                            if (string.IsNullOrEmpty(d.Serial)) d.Serial = "device" + i;
                            ReadProps(d, true);
                            lock (_snapLock) _devices[i] = d;
                            AppLog.Info(
                                $"SteamVR device {i}: {d.Kind} class={d.Class} role={d.Role} {d.Manufacturer} {d.Model} ({d.ControllerType}) {d.Serial}" +
                                (d.IsBaseStation
                                    ? $" fov L{d.FovL:0} R{d.FovR:0} T{d.FovT:0} B{d.FovB:0} range {d.RangeMin:0.##}-{d.RangeMax:0.##} m mode '{d.ModeLabel}'"
                                    : ""));
                            if (!d.IsBaseStation) OnDevice?.Invoke(d);
                        }
                        else if (recheck)
                        {
                            int role = _getRole((uint)i);
                            if (role != d.Role)
                            {
                                d.Role = role;
                                if (!d.IsBaseStation) OnDevice?.Invoke(d);
                            }

                            ReadProps(d, false);
                        }

                        int code = p.valid ? p.result : 0;
                        if (code != d.LastCode && !d.IsBaseStation)
                        {
                            d.LastCode = code;
                            OnTracking?.Invoke(d, t, code);
                        }

                        d.Result = p.result;
                        if (!p.valid || d.Class == 0 || d.Class == 5)
                        {
                            d.Valid = false;
                            continue;
                        }

                        m[0] = p.m0;
                        m[1] = p.m1;
                        m[2] = p.m2;
                        m[3] = p.m3;
                        m[4] = p.m4;
                        m[5] = p.m5;
                        m[6] = p.m6;
                        m[7] = p.m7;
                        m[8] = p.m8;
                        m[9] = p.m9;
                        m[10] = p.m10;
                        m[11] = p.m11;
                        MathX.OpenVRToUnity(m, 0, out var pos, out var rot);
                        lock (_snapLock)
                        {
                            d.Pos = pos;
                            d.Rot = rot;
                            d.Valid = true;
                            d.HasPose = true;
                        }

                        if (d.IsBaseStation) continue; // shown live, not recorded or used by the IK
                        Samples++;
                        OnPose?.Invoke(d, t);
                    }

                    PollInput(t);
                    OnFrame?.Invoke();

                    // pace to the target rate
                    next += 1.0 / Rate;
                    double wait = next - Clock.Now;
                    if (wait < -0.05) next = Clock.Now; // fell behind; resync
                    else if (wait > 0)
                    {
                        if (timer != IntPtr.Zero)
                        {
                            long due = -(long)(wait * 1e7);
                            SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
                            WaitForSingleObject(timer, 100);
                        }
                        else Thread.Sleep((int)Math.Max(1, wait * 1000));
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(evt);
                if (timer != IntPtr.Zero) CloseHandle(timer);
                if (Connected)
                {
                    try { _shutdown(); }
                    catch { }

                    Connected = false;
                }
            }
        }

        public sealed class Device
        {
            /// <summary>Battery 0..1, or -1 when the device doesn't report one.</summary>
            public float Battery = -1;

            public bool Charging, Wireless;
            public int Class, Role;

            /// <summary>Base stations: field of view half-angles (left, right, top, bottom, degrees), range and channel/mode label.</summary>
            public float FovL, FovR, FovT, FovB, RangeMin, RangeMax;

            /// <summary>Has had a valid pose at least once (Pos/Rot hold the last known pose).</summary>
            public bool HasPose;

            public int Index;

            /// <summary>
            ///     Last reported tracking code (OnTracking): the ETrackingResult while valid, 0 = pose invalid, int.MinValue =
            ///     not seen yet.
            /// </summary>
            public int LastCode = int.MinValue;

            public string ModeLabel = "";
            public Vec3 Pos;

            /// <summary>ETrackingResult: 1 uninitialized, 100/101 calibrating, 200 OK, 201 out of range, 300 rotation only.</summary>
            public int Result;

            public Quat Rot = Quat.Identity;

            public string Serial = "",
                Model = "",
                ControllerType = "",
                TrackingSystem = "",
                Manufacturer = "",
                RenderModel = "";

            /// <summary>Role set in SteamVR "Manage Trackers" (e.g. "Waist", "Left Foot"), or "".</summary>
            public string TrackerRole = "";

            /// <summary>Pose is valid right now (tracked).</summary>
            public bool Valid;

            public bool IsBaseStation => Class == 4;

            public string Kind => Class == 1 ? "HMD" :
                Class == 2 ? "Controller" :
                Class == 3 ? "Tracker" :
                Class == 4 ? "Base station" : "Device";

            public string RoleName => Class == 2
                ? Role == 1 ? "left hand" :
                Role == 2 ? "right hand" :
                Role == 5 ? "treadmill" :
                Role == 6 ? "stylus" : ""
                : "";

            public string TrackingText => Result == 200 ? "tracking" :
                Result == 201 ? "out of range" :
                Result == 300 ? "rotation only" :
                Result == 100 ? "calibrating" :
                Result == 101 ? "not seen by base stations" : "not tracking";

            /// <summary>0 = tracked, 1 = degraded (out of range / rotation only), 2 = not tracked.</summary>
            public int TrackingLevel => Valid && Result == 200 ? 0 : Valid ? 1 : 2;

            public Device Clone()
            {
                return (Device)MemberwiseClone();
            }

            /// <summary>Takes over every value of another device (to refresh a snapshot object without allocating).</summary>
            public void CopyFrom(Device d)
            {
                Index = d.Index;
                Serial = d.Serial;
                Model = d.Model;
                ControllerType = d.ControllerType;
                TrackingSystem = d.TrackingSystem;
                Manufacturer = d.Manufacturer;
                RenderModel = d.RenderModel;
                Class = d.Class;
                Role = d.Role;
                Valid = d.Valid;
                HasPose = d.HasPose;
                Pos = d.Pos;
                Rot = d.Rot;
                Result = d.Result;
                LastCode = d.LastCode;
                Battery = d.Battery;
                Charging = d.Charging;
                Wireless = d.Wireless;
                FovL = d.FovL;
                FovR = d.FovR;
                FovT = d.FovT;
                FovB = d.FovB;
                RangeMin = d.RangeMin;
                RangeMax = d.RangeMax;
                ModeLabel = d.ModeLabel;
                TrackerRole = d.TrackerRole;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TrackedDevicePose
        {
            public float m0, m1, m2, m3, m4, m5, m6, m7, m8, m9, m10, m11;
            public float vx, vy, vz, ax, ay, az;
            public int result;
            [MarshalAs(UnmanagedType.I1)] public bool valid;
            [MarshalAs(UnmanagedType.I1)] public bool connected;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate uint FnInit(ref int err, int type, IntPtr startup);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr FnGetInterface([MarshalAs(UnmanagedType.LPStr)] string name, ref int err);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void FnShutdown();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void FnGetPoses(int origin, float predict, [In] [Out] TrackedDevicePose[] poses, uint count);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int FnGetClass(uint idx);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int FnGetRole(uint idx);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate uint FnGetString(uint idx, int prop, StringBuilder buf, uint size, ref int err);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate float FnGetFloat(uint idx, int prop, ref int err);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        delegate bool FnGetBool(uint idx, int prop, ref int err);

        [StructLayout(LayoutKind.Sequential)]
        struct HmdQuad
        {
            public float x0, y0, z0, x1, y1, z1, x2, y2, z2, x3, y3, z3;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        delegate bool FnPlayAreaRect(ref HmdQuad rect);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        delegate bool FnPollEvent(IntPtr evt, uint size);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void FnAckQuit();

        // ---- SteamVR Skeletal Input (finger tracking). Same approach as ThumbParamsOSC: utility app, priority 0,
        // skeleton actions bound to /user/hand/*/input/skeleton/* -> never takes input away from VRChat.
        [StructLayout(LayoutKind.Sequential)]
        struct ActiveActionSet
        {
            public ulong set, restrict, secondary;
            public uint pad;
            public int priority;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SkeletalActionData
        {
            [MarshalAs(UnmanagedType.I1)] public bool active;
            public ulong origin;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SummaryData
        {
            public float c0, c1, c2, c3, c4, s0, s1, s2, s3;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BoneTransform
        {
            public float px, py, pz, pw, qw, qx, qy, qz;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int FnSetManifest(IntPtr path);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int FnGetHandle(IntPtr name, ref ulong handle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int FnUpdateActions([In] [Out] ActiveActionSet[] sets, uint size, uint count);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int FnSkelActionData(ulong action, ref SkeletalActionData data, uint size);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int FnSummary(ulong action, int type, ref SummaryData data);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int FnBones(ulong action, int space, int range, [In] [Out] BoneTransform[] arr, uint count);

        [StructLayout(LayoutKind.Sequential)]
        struct AnalogData
        {
            [MarshalAs(UnmanagedType.I1)] public bool active;
            public ulong origin;
            public float x, y, z, dx, dy, dz, updateTime;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int FnAnalog(ulong action, ref AnalogData data, uint size, ulong restrict);
    }
}
