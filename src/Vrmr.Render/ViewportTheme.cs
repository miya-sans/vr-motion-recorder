namespace VRMR.Render
{
    /// <summary>
    /// Every colour and font the 3D view uses. The app fills it from its theme tokens; the defaults are the dark theme.
    /// Assign a new instance (or call <see cref="ViewportControl.RequestFrame"/> after changing fields) to redraw.
    /// </summary>
    public sealed class ViewportTheme
    {
        public Rgba Background = new Rgba(18, 19, 23);
        public Rgba GridMinor = new Rgba(34, 36, 44);
        public Rgba GridMajor = new Rgba(44, 47, 58);
        /// <summary>The origin's X arm and the forward (Z) arrow on the floor.</summary>
        public Rgba OriginX = new Rgba(220, 90, 90, 150), OriginZ = new Rgba(90, 140, 240, 150);
        public Rgba PlayAreaFill = new Rgba(80, 200, 220, 22), PlayAreaLine = new Rgba(80, 200, 220, 150);

        public Rgba BoneLeft = new Rgba(96, 160, 255), BoneRight = new Rgba(255, 116, 116), BoneCenter = new Rgba(210, 214, 224);
        /// <summary>Hair, ears, tail, skirt and other bones outside the humanoid set.</summary>
        public Rgba BoneSecondary = new Rgba(150, 156, 170, 90);
        /// <summary>Mesh parts without a texture.</summary>
        public Rgba MeshUntextured = new Rgba(0xB8, 0xBC, 0xC8);

        /// <summary>Local axes of trackers and axis markers.</summary>
        public Rgba AxisX = new Rgba(230, 90, 90), AxisY = new Rgba(110, 220, 120), AxisZ = new Rgba(100, 150, 255);

        public Rgba Text = new Rgba(228, 230, 236), TextDim = new Rgba(148, 152, 166), TextFaint = new Rgba(100, 104, 118);
        public Rgba Record = new Rgba(240, 72, 72);

        public Rgba GizmoX = new Rgba(232, 88, 88), GizmoY = new Rgba(110, 214, 110), GizmoZ = new Rgba(92, 146, 255);
        public Rgba GizmoView = new Rgba(220, 222, 230), GizmoHot = new Rgba(255, 214, 90);

        /// <summary>Font family for all text in the view; falls back to the system UI font when it is not available.</summary>
        public string FontFamily = "Inter";
        /// <summary>Text sizes in device-independent pixels: marker labels and tool text, corner overlay text, the centred empty text.</summary>
        public float LabelSize = 11f, OverlaySize = 12f, EmptySize = 13f;
        /// <summary>The line in the bottom-right corner while the pointer is over the view; null or empty for none.</summary>
        public string HintText = "drag: orbit   right-drag: pan   wheel: zoom   double-click: frame";

        /// <summary>A copy that can be changed without affecting this one.</summary>
        public ViewportTheme Clone() => (ViewportTheme)MemberwiseClone();
    }
}
