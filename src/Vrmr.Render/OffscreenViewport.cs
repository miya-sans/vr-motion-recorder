using System;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;
using VRMR.Render.Gl;

namespace VRMR.Render
{
    /// <summary>
    /// Draws frames into memory without a window: the same renderer as <see cref="ViewportControl"/>, on its own OpenGL
    /// ES context. For thumbnails, comparisons and tests. Windows only for now: it uses ANGLE's EGL from the library
    /// Avalonia ships (av_libglesv2), which must be next to the program.
    /// </summary>
    public sealed class OffscreenViewport : IDisposable
    {
        const string Lib = "av_libglesv2";
        [DllImport(Lib, EntryPoint = "EGL_GetPlatformDisplayEXT")] static extern IntPtr GetPlatformDisplay(int platform, IntPtr nativeDisplay, int[] attributes);
        [DllImport(Lib, EntryPoint = "EGL_Initialize")] static extern int Initialize(IntPtr display, out int major, out int minor);
        [DllImport(Lib, EntryPoint = "EGL_ChooseConfig")] static extern int ChooseConfig(IntPtr display, int[] attributes, IntPtr[] configs, int size, out int count);
        [DllImport(Lib, EntryPoint = "EGL_CreatePbufferSurface")] static extern IntPtr CreatePbufferSurface(IntPtr display, IntPtr config, int[] attributes);
        [DllImport(Lib, EntryPoint = "EGL_CreateContext")] static extern IntPtr CreateContext(IntPtr display, IntPtr config, IntPtr share, int[] attributes);
        [DllImport(Lib, EntryPoint = "EGL_MakeCurrent")] static extern int MakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);
        [DllImport(Lib, EntryPoint = "EGL_DestroyContext")] static extern int DestroyContext(IntPtr display, IntPtr context);
        [DllImport(Lib, EntryPoint = "EGL_DestroySurface")] static extern int DestroySurface(IntPtr display, IntPtr surface);
        [DllImport(Lib, EntryPoint = "EGL_GetProcAddress", CharSet = CharSet.Ansi, BestFitMapping = false)] static extern IntPtr GetProcAddress(string name);
        [DllImport(Lib, EntryPoint = "EGL_GetError")] static extern int GetError();
        [DllImport(Lib, EntryPoint = "EGL_BindAPI")] static extern int BindApi(int api);

        const int None = 0x3038, PlatformAngle = 0x3202, PlatformAngleType = 0x3203, AngleD3D11 = 0x3208, AngleDeviceType = 0x3209;
        const int DeviceHardware = 0x320A, DeviceWarp = 0x320B, OpenGlEsApi = 0x30A0;
        const int SurfaceType = 0x3033, PbufferBit = 1, RenderableType = 0x3040, OpenGlEs3Bit = 0x40;
        const int RedSize = 0x3024, GreenSize = 0x3023, BlueSize = 0x3022, AlphaSize = 0x3021, Width = 0x3057, Height = 0x3056, ContextClientVersion = 0x3098;

        readonly IntPtr _display, _surface, _context;
        readonly SceneRenderer _renderer;
        readonly SceneDraw _draw = new SceneDraw();
        readonly SceneBuilder _builder = new SceneBuilder();
        readonly OverlayList _overlay = new OverlayList();
        bool _disposed;

        /// <summary>The camera frames are drawn with.</summary>
        public ViewCamera Camera { get; } = new ViewCamera();
        public ViewportTheme Theme = new ViewportTheme();
        public bool ShowMesh = true, ShowSkeleton = true, ShowDevices = true, ShowHeadset, ShowPlayArea = true, ShowBaseStations = true;
        /// <summary>Multisampling of the mesh: 0, 2, 4 or 8.</summary>
        public int Msaa;
        /// <summary>Device pixels per device-independent pixel: line widths and text scale with it.</summary>
        public float Scaling = 1f;
        /// <summary>Draws over the scene, as <see cref="ViewportControl.DrawOverlay"/> does.</summary>
        public Action<OverlayList> DrawOverlay;
        public string GlVersion => _renderer.Version;
        public string Adapter => _renderer.Adapter;
        /// <summary>Avatar mesh triangles in the last frame.</summary>
        public int Triangles => _renderer.LastTriangles;

        /// <summary>Creates the GL context on the calling thread. software: Direct3D's software rasteriser instead of the GPU (machines without one).</summary>
        public OffscreenViewport(bool software = false)
        {
            _display = GetPlatformDisplay(PlatformAngle, IntPtr.Zero, new[] { PlatformAngleType, AngleD3D11, AngleDeviceType, software ? DeviceWarp : DeviceHardware, None });
            if (_display == IntPtr.Zero || Initialize(_display, out _, out _) == 0) throw Failure("EGL could not be initialised");
            var configs = new IntPtr[1];
            if (ChooseConfig(_display, new[] { SurfaceType, PbufferBit, RenderableType, OpenGlEs3Bit, RedSize, 8, GreenSize, 8, BlueSize, 8, AlphaSize, 8, None }, configs, 1, out int n) == 0 || n < 1)
                throw Failure("no OpenGL ES 3 configuration");
            _surface = CreatePbufferSurface(_display, configs[0], new[] { Width, 16, Height, 16, None });
            BindApi(OpenGlEsApi);
            _context = CreateContext(_display, configs[0], IntPtr.Zero, new[] { ContextClientVersion, 3, None });
            if (_surface == IntPtr.Zero || _context == IntPtr.Zero || MakeCurrent(_display, _surface, _surface, _context) == 0) throw Failure("no OpenGL ES 3 context");
            _renderer = new SceneRenderer(GL.GetApi(name => GetProcAddress(name)));
        }

        static Exception Failure(string what) => new InvalidOperationException("Off-screen 3D view: " + what + " (EGL error 0x" + GetError().ToString("X") + ").");

        /// <summary>
        /// Draws a frame at width x height device pixels and returns its pixels. Call it on the thread that created this
        /// object. The frame is only read.
        /// </summary>
        public ViewportImage Render(ViewFrame frame, int width, int height)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OffscreenViewport));
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (MakeCurrent(_display, _surface, _surface, _context) == 0) throw Failure("the context could not be made current");
            var toggles = new ViewToggles { Mesh = ShowMesh, Skeleton = ShowSkeleton, Devices = ShowDevices, Headset = ShowHeadset, PlayArea = ShowPlayArea, BaseStations = ShowBaseStations };
            Camera.SetViewport(width / Scaling, height / Scaling);
            _draw.Atlas.SetFamily(Theme.FontFamily);
            _draw.Atlas.BeginFrame();
            _draw.Batch.Clear();
            _draw.Batch.Scale = _draw.Scale = Scaling;
            _draw.Msaa = Msaa;
            _builder.BuildScene(_draw, frame, Camera, Theme, toggles);
            _overlay.Bind(_draw.Batch, _draw.Atlas, Camera);
            DrawOverlay?.Invoke(_overlay);
            _builder.BuildChrome(_draw, frame, Camera, Theme, toggles, pointerOver: false);
            _renderer.Render(_draw, width, height, null, flip: false);
            var image = new ViewportImage { Width = width, Height = height, Rgba = new byte[width * height * 4] };
            _renderer.ReadPixels(image.Rgba);
            return image;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (MakeCurrent(_display, _surface, _surface, _context) != 0) _renderer.Dispose();
            MakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            DestroyContext(_display, _context);
            DestroySurface(_display, _surface);
        }
    }
}
