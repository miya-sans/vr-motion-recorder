using System;
using System.Runtime.InteropServices;

namespace VRMR
{
    /// <summary>
    ///     Windows efficiency mode (EcoQoS) for this process: while it is on, Windows runs the process's threads on
    ///     efficient cores at a lower clock, which leaves the fast cores to VRChat and SteamVR. The app turns it on while
    ///     its window is in the background and nothing is being recorded. Threads that must keep their timing (capture)
    ///     opt out with <see cref="ExemptCurrentThread" />. Does nothing on systems without the feature.
    /// </summary>
    public static class EfficiencyMode
    {
        const int ProcessPowerThrottling = 4, ThreadPowerThrottling = 3;
        const uint ExecutionSpeed = 0x1;

        static readonly object Lock = new();
        static bool _on;

        /// <summary>Whether this process has asked for efficiency mode.</summary>
        public static bool IsOn
        {
            get
            {
                lock (Lock) return _on;
            }
        }

        [DllImport("kernel32")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32")]
        static extern IntPtr GetCurrentThread();

        [DllImport("kernel32", SetLastError = true)]
        static extern int SetProcessInformation(IntPtr process, int infoClass, ref ThrottlingState info, uint size);

        [DllImport("kernel32", SetLastError = true)]
        static extern int GetProcessInformation(IntPtr process, int infoClass, ref ThrottlingState info, uint size);

        [DllImport("kernel32", SetLastError = true)]
        static extern int SetThreadInformation(IntPtr thread, int infoClass, ref ThrottlingState info, uint size);

        /// <summary>The rule the app follows: in the background (not the active window, or minimised) and not recording.</summary>
        public static bool ShouldBeOn(bool windowActive, bool minimised, bool recording)
        {
            return !recording && (!windowActive || minimised);
        }

        /// <summary>
        ///     Applies <see cref="ShouldBeOn" />. Call when the window's state or the recording state changes. Returns
        ///     whether the mode is on now.
        /// </summary>
        public static bool Update(bool windowActive, bool minimised, bool recording)
        {
            Set(ShouldBeOn(windowActive, minimised, recording));
            return IsOn;
        }

        /// <summary>Turns efficiency mode on or off for the whole process. Returns false when the system does not support it.</summary>
        public static bool Set(bool on)
        {
            lock (Lock)
            {
                if (on == _on) return true;
                if (!OperatingSystem.IsWindows()) return false;
                try
                {
                    var state = new ThrottlingState
                        { Version = 1, ControlMask = ExecutionSpeed, StateMask = on ? ExecutionSpeed : 0 };
                    if (SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state,
                            (uint)Marshal.SizeOf<ThrottlingState>()) == 0) return false;
                    _on = on;
                    return true;
                }
                catch (EntryPointNotFoundException) { return false; } // before Windows 10 1709
            }
        }

        /// <summary>What Windows reports for this process: true = throttled, false = not, null = unknown.</summary>
        public static bool? ReadBack()
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                var state = new ThrottlingState { Version = 1 };
                if (GetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state,
                        (uint)Marshal.SizeOf<ThrottlingState>()) == 0) return null;
                return (state.StateMask & ExecutionSpeed) != 0;
            }
            catch (EntryPointNotFoundException) { return null; }
        }

        /// <summary>
        ///     Keeps the calling thread at full speed whatever the process's mode is. Capture threads call this once when
        ///     they start, so live data keeps its timing while the window is in the background.
        /// </summary>
        public static void ExemptCurrentThread()
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                var state = new ThrottlingState { Version = 1, ControlMask = ExecutionSpeed, StateMask = 0 };
                SetThreadInformation(GetCurrentThread(), ThreadPowerThrottling, ref state,
                    (uint)Marshal.SizeOf<ThrottlingState>());
            }
            catch (EntryPointNotFoundException) { }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ThrottlingState
        {
            public uint Version, ControlMask, StateMask;
        }
    }
}
