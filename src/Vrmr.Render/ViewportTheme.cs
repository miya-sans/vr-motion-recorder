namespace VRMR.Render
{
    /// <summary>
    ///     Every colour and font the 3D view uses. The app fills it from its theme tokens; the defaults are the dark theme.
    ///     Assign a new instance (or call <see cref="ViewportControl.RequestFrame" /> after changing fields) to redraw.
    /// </summary>
    public sealed class ViewportTheme
    {
        /// <summary>Local axes of trackers and axis markers.</summary>
        public Rgba AxisX = new(230, 90, 90), AxisY = new(110, 220, 120), AxisZ = new(100, 150, 255);

        public Rgba Background = new(18, 19, 23);

        public Rgba BoneLeft = new(96, 160, 255), BoneRight = new(255, 116, 116), BoneCenter = new(210, 214, 224);

        /// <summary>Hair, ears, tail, skirt and other bones outside the humanoid set.</summary>
        public Rgba BoneSecondary = new(150, 156, 170, 90);

        /// <summary>Font family for all text in the view; falls back to the system UI font when it is not available.</summary>
        public string FontFamily = "Inter";

        public Rgba GizmoView = new(220, 222, 230), GizmoHot = new(255, 214, 90);

        public Rgba GizmoX = new(232, 88, 88), GizmoY = new(110, 214, 110), GizmoZ = new(92, 146, 255);
        public Rgba GridMajor = new(44, 47, 58);
        public Rgba GridMinor = new(34, 36, 44);

        /// <summary>The line in the bottom-right corner while the pointer is over the view; null or empty for none.</summary>
        public string HintText = "drag: orbit   right-drag: pan   wheel: zoom   double-click: frame";

        /// <summary>
        ///     Text sizes in device-independent pixels: marker labels and tool text, corner overlay text, the centred empty
        ///     text.
        /// </summary>
        public float LabelSize = 11f, OverlaySize = 12f, EmptySize = 13f;

        /// <summary>Mesh parts without a texture.</summary>
        public Rgba MeshUntextured = new(0xB8, 0xBC, 0xC8);

        /// <summary>The origin's X arm and the forward (Z) arrow on the floor.</summary>
        public Rgba OriginX = new(220, 90, 90, 150), OriginZ = new(90, 140, 240, 150);

        public Rgba PlayAreaFill = new(80, 200, 220, 22), PlayAreaLine = new(80, 200, 220, 150);
        public Rgba Record = new(240, 72, 72);

        public Rgba Text = new(228, 230, 236), TextDim = new(148, 152, 166), TextFaint = new(100, 104, 118);

        /// <summary>A copy that can be changed without affecting this one.</summary>
        public ViewportTheme Clone()
        {
            return (ViewportTheme)MemberwiseClone();
        }
    }
}
