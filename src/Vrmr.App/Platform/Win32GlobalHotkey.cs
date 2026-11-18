using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using VRMR.App.Services;

namespace VRMR.App.Platform
{
    /// <summary>
    ///     A system-wide hotkey on Windows. The hotkey belongs to a small thread of its own that only waits for the key
    ///     message, so no window is needed and nothing is polled.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class Win32GlobalHotkey : IGlobalHotkey
    {
        const int WM_HOTKEY = 0x0312, WM_QUIT = 0x0012, WM_APP = 0x8000;
        const uint MOD_NOREPEAT = 0x4000;

        Thread _thread;
        uint _threadId;

        public bool Register(uint modifiers, uint virtualKey, Action pressed)
        {
            Unregister();
            bool ok = false;
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                PeekMessageW(out _, IntPtr.Zero, WM_APP, WM_APP, 0); // creates this thread's message queue
                ok = RegisterHotKey(IntPtr.Zero, 1, modifiers | MOD_NOREPEAT, virtualKey);
                ready.Set();
                if (!ok) return;
                while (GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0)
                    if (m.message == WM_HOTKEY)
                    {
                        try { pressed(); }
                        catch (Exception ex) { AppLog.Error("Hotkey: " + ex); }
                    }

                UnregisterHotKey(IntPtr.Zero, 1);
            }) { IsBackground = true, Name = "record hotkey" };
            _thread.Start();
            ready.Wait();
            if (!ok)
            {
                _thread = null;
                _threadId = 0;
            }

            return ok;
        }

        public void Unregister()
        {
            if (_thread == null) return;
            PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(500);
            _thread = null;
            _threadId = 0;
        }

        public void Dispose()
        {
            Unregister();
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);

        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);

        [DllImport("user32.dll")]
        static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr w, IntPtr l);

        [DllImport("user32.dll")]
        static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam, lParam;
            public uint time;
            public int x, y;
        }
    }
}
