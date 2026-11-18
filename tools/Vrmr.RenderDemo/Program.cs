using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using SkiaSharp;
using VRMR.Render;

namespace VRMR.RenderDemo
{
    /// <summary>What the command line asked for.</summary>
    internal sealed class DemoOptions
    {
        public string DataDir, TakeFilter = "", WorkDir = Path.Combine(Path.GetTempPath(), "vrmr-render-demo");
        public bool Bench, MeshOnly, Quiet;
        public double Seconds = 10;
        public (int Width, int Height)? ViewPixels;
        public int Msaa;
        public string ShotDir, WindowShot;
        public int[] Frames = { 0 };
        public float? Yaw, Pitch, Distance;
        public Vec3? Target;
        public float Scaling = 1f;

        public const string Usage = @"vrmr-render-demo [<folder>] [options]

Shows a take in the GPU 3D view. <folder> holds .vrmr takes and an Avatars folder (an avatar library); without it
the built-in mannequin is shown.

  --take <text>         the take whose file name contains <text> (default: the first one)
  --work <dir>          where the log and the calibration cache go (default: a folder in the system's temp folder)
  --size <w>x<h>        size of the 3D view in device pixels
  --msaa <n>            multisampling of the mesh: 0 (default), 2, 4 or 8
  --bench               run the four benchmark states, print the numbers, exit
  --seconds <n>         seconds per benchmark state (default 10)
  --shot <dir>          draw frames off-screen (no window) to PNG files in <dir>, exit
  --window-shot <file>  open the window, save what the view drew as a PNG, exit
  --frames <a,b,c>      the take's frames for --shot and --window-shot (default 0)
  --yaw <deg> --pitch <deg> --distance <m> --target <x,y,z>   the camera for the shots
  --scaling <k>         display scaling to draw --shot with (default 1)
  --mesh-only           shots without floor, bones, devices and text (to compare against another renderer)";

        public static DemoOptions Parse(string[] args)
        {
            var o = new DemoOptions();
            var inv = CultureInfo.InvariantCulture;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(a + " needs a value");
                switch (a)
                {
                    case "--take": o.TakeFilter = Next(); break;
                    case "--work": o.WorkDir = Path.GetFullPath(Next()); break;
                    case "--size": { var p = Next().Split('x'); o.ViewPixels = (int.Parse(p[0], inv), int.Parse(p[1], inv)); break; }
                    case "--msaa": o.Msaa = int.Parse(Next(), inv); break;
                    case "--bench": o.Bench = true; o.Quiet = true; break;
                    case "--seconds": o.Seconds = double.Parse(Next(), inv); break;
                    case "--shot": o.ShotDir = Path.GetFullPath(Next()); break;
                    case "--window-shot": o.WindowShot = Path.GetFullPath(Next()); o.Quiet = true; break;
                    case "--frames": o.Frames = Next().Split(',').Select(s => int.Parse(s, inv)).ToArray(); break;
                    case "--yaw": o.Yaw = float.Parse(Next(), inv); break;
                    case "--pitch": o.Pitch = float.Parse(Next(), inv); break;
                    case "--distance": o.Distance = float.Parse(Next(), inv); break;
                    case "--target": { var p = Next().Split(','); o.Target = new Vec3(float.Parse(p[0], inv), float.Parse(p[1], inv), float.Parse(p[2], inv)); break; }
                    case "--scaling": o.Scaling = float.Parse(Next(), inv); break;
                    case "--mesh-only": o.MeshOnly = true; break;
                    default:
                        if (a.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Unknown option " + a);
                        o.DataDir = Path.GetFullPath(a);
                        break;
                }
            }
            if (o.Bench && o.ViewPixels == null) o.ViewPixels = (1100, 640);
            return o;
        }

        /// <summary>Applies the camera options; values that were not given stay as they are.</summary>
        public void ApplyCamera(ViewCamera camera)
        {
            if (Target.HasValue) camera.Target = Target.Value;
            if (Yaw.HasValue) camera.Yaw = Yaw.Value;
            if (Pitch.HasValue) camera.Pitch = Pitch.Value;
            if (Distance.HasValue) camera.Distance = Distance.Value;
        }

        /// <summary>A theme whose floor is invisible, for --mesh-only.</summary>
        public static ViewportTheme BareTheme()
        {
            var t = new ViewportTheme();
            t.GridMinor = t.GridMajor = t.OriginX = t.OriginZ = Rgba.Transparent;
            return t;
        }
    }

    internal sealed class DemoApp : Application
    {
        public static DemoOptions Options;
        public static TakeSource Source;

        public override void Initialize() => Styles.Add(new FluentTheme());

        public override void OnFrameworkInitializationCompleted()
        {
            var lifetime = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime;
            var o = Options;
            var window = new DemoWindow(Source, o);
            lifetime.MainWindow = window;
            if (o.MeshOnly) { window.View.ViewTheme = DemoOptions.BareTheme(); window.View.ShowSkeleton = false; window.View.ShowDevices = false; window.View.ShowHint = false; }
            if (o.Bench)
                window.Opened += (_, _) => DispatcherTimer.RunOnce(() => new Bench(window, o.Seconds, Console.WriteLine, window.Close).Start(), TimeSpan.FromSeconds(2));
            else if (o.WindowShot != null)
                window.Opened += (_, _) => DispatcherTimer.RunOnce(() =>
                {
                    window.View.Follow = false;
                    window.Seek(o.Frames[0]);
                    o.ApplyCamera(window.View.Camera);
                    window.View.Capture(image => { Program.SavePng(image, o.WindowShot); Console.WriteLine("saved " + o.WindowShot); Dispatcher.UIThread.Post(window.Close); });
                }, TimeSpan.FromSeconds(1));
            base.OnFrameworkInitializationCompleted();
        }
    }

    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            DemoOptions o;
            try { o = DemoOptions.Parse(args); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Console.Error.WriteLine(); Console.Error.WriteLine(DemoOptions.Usage); return 2; }
            if (args.Contains("--help") || args.Contains("-h")) { Console.WriteLine(DemoOptions.Usage); return 0; }
            try
            {
                var source = o.DataDir == null ? TakeSource.Mannequin() : TakeSource.Load(o.DataDir, o.TakeFilter, o.WorkDir, Console.WriteLine);
                if (o.ShotDir != null) return Shots(o, source);
                DemoApp.Options = o; DemoApp.Source = source;
                return AppBuilder.Configure<DemoApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(Array.Empty<string>());
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        /// <summary>--shot: the frames drawn off-screen, one PNG each.</summary>
        static int Shots(DemoOptions o, TakeSource source)
        {
            Directory.CreateDirectory(o.ShotDir);
            var (width, height) = o.ViewPixels ?? (1060, 600);
            using var view = new OffscreenViewport { Msaa = o.Msaa, Scaling = o.Scaling };
            Console.WriteLine("GL: " + view.GlVersion + " on " + view.Adapter);
            if (o.MeshOnly) { view.Theme = DemoOptions.BareTheme(); view.ShowSkeleton = false; view.ShowDevices = false; }
            view.Camera.SetViewport(width / o.Scaling, height / o.Scaling);
            var frame = new ViewFrame();
            foreach (int index in o.Frames)
            {
                frame = new ViewFrame();
                var actor = source.Fill(frame, index, devices: !o.MeshOnly);
                if (!o.MeshOnly) { frame.Overlay.Append(source.Source).Append("   frame ").Append(index).Append('/').Append(source.Frames - 1); frame.OverlayRight.Append(source.Name); }
                // the camera the window would have after "frame all", unless the options say otherwise
                if (actor.Profile.Has(HumanBones.Hips))
                {
                    var hips = actor.NodePos[actor.Profile.Human[HumanBones.Hips]];
                    view.Camera.Target = new Vec3(hips.x, Math.Max(0.4f, actor.Profile.EyeHeight * 0.55f), hips.z);
                    view.Camera.Distance = Math.Max(1.2f, actor.Profile.EyeHeight * 2.6f);
                }
                o.ApplyCamera(view.Camera);
                var image = view.Render(frame, width, height);
                string file = Path.Combine(o.ShotDir, $"gpu_{index:00000}.png");
                SavePng(image, file);
                var c = view.Camera;
                Console.WriteLine(FormattableString.Invariant($"frame {index}: {file}  camera --yaw {c.Yaw} --pitch {c.Pitch} --distance {c.Distance} --target {c.Target.x},{c.Target.y},{c.Target.z}  triangles {view.Triangles}"));
            }
            return 0;
        }

        public static void SavePng(ViewportImage image, string file)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
            System.Runtime.InteropServices.Marshal.Copy(image.Rgba, 0, bitmap.GetPixels(), image.Rgba.Length);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(file);
            data.SaveTo(stream);
        }
    }
}
