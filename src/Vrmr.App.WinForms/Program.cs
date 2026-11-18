using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("VR Motion Recorder")]
[assembly: System.Reflection.AssemblyProduct("VR Motion Recorder")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace VRMR
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            using (var mutex = new Mutex(true, "VRMotionRecorder.SingleInstance", out bool first))
            {
                if (!first)
                {
                    MessageBox.Show("VR Motion Recorder is already running.", "VR Motion Recorder");
                    return 1;
                }
                try { if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) SetProcessDPIAware(); } catch { try { SetProcessDPIAware(); } catch { } }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                AppLog.Init(Settings.AppDataDir);
                AppLog.Info("VR Motion Recorder starting");
                Application.ThreadException += (s, e) => { AppLog.Error("UI: " + e.Exception); MessageBox.Show(e.Exception.Message, "VR Motion Recorder error"); };
                AppDomain.CurrentDomain.UnhandledException += (s, e) => AppLog.Error("Fatal: " + e.ExceptionObject);

                var settings = Settings.Load();
                // first run inside a Unity project: link it automatically
                if (settings.UnityProjects.Count == 0)
                {
                    var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                    for (int i = 0; i < 4 && dir != null; i++, dir = dir.Parent)
                        if (UnityProjectScanner.IsUnityProject(dir.FullName)) { settings.UnityProjects.Add(dir.FullName); settings.Save(); break; }
                }
                Directory.CreateDirectory(settings.RecordingsDir);
                BodySolverSetup.Register();
                SkinnedAvatar.TextureDecoder = DecodeTexture;

                var recorder = new Recorder(settings);
                recorder.Start();
                try { Application.Run(new UI.MainForm(settings, recorder)); }
                finally { recorder.Dispose(); AppLog.Info("bye"); }
            }
            return 0;
        }

        /// <summary>The avatar preview's texture decoder for this UI (GDI+): the image scaled to fit maxSize, as 32-bit ARGB.</summary>
        static TexturePixels DecodeTexture(string path, int maxSize)
        {
            using (var src = new System.Drawing.Bitmap(path))
            {
                float k = Math.Min(1f, (float)maxSize / Math.Max(src.Width, src.Height));
                int w = Math.Max(1, (int)(src.Width * k)), h = Math.Max(1, (int)(src.Height * k));
                using (var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                        g.DrawImage(src, 0, 0, w, h);
                    }
                    var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    var px = new int[w * h];
                    for (int y = 0; y < h; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, px, y * w, w);
                    bmp.UnlockBits(bd);
                    return new TexturePixels { Width = w, Height = h, Argb = px };
                }
            }
        }
    }
}
