using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace VRMR.App.Controls
{
    /// <summary>
    ///     One icon of the app's outline set (Themes/Icons.axaml): geometry on a 16 x 16 grid, stroked in the text colour,
    ///     or filled for the solid ones.
    /// </summary>
    public sealed class Icon : Control
    {
        const double Grid = 16;

        public static readonly StyledProperty<Geometry> DataProperty =
            AvaloniaProperty.Register<Icon, Geometry>(nameof(Data));

        public static readonly StyledProperty<bool> FilledProperty =
            AvaloniaProperty.Register<Icon, bool>(nameof(Filled));

        public static readonly StyledProperty<IBrush> ForegroundProperty =
            TextElement.ForegroundProperty.AddOwner<Icon>();

        public static readonly StyledProperty<double> SizeProperty =
            AvaloniaProperty.Register<Icon, double>(nameof(Size), Grid);

        static Icon()
        {
            AffectsRender<Icon>(DataProperty, FilledProperty, ForegroundProperty);
            AffectsMeasure<Icon>(SizeProperty);
        }

        /// <summary>The icon's geometry (a resource such as Icon.Search).</summary>
        public Geometry Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

        /// <summary>Fill instead of stroke (play, pause, record).</summary>
        public bool Filled { get => GetValue(FilledProperty); set => SetValue(FilledProperty, value); }

        public IBrush Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

        /// <summary>Edge length in pixels (16 by default).</summary>
        public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

        protected override Size MeasureOverride(Size availableSize)
        {
            return new Size(Size, Size);
        }

        public override void Render(DrawingContext context)
        {
            var data = Data;
            var brush = Foreground;
            if (data == null || brush == null) return;
            double k = Bounds.Width / Grid;
            using (context.PushTransform(Matrix.CreateScale(k, k)))
            {
                if (Filled) context.DrawGeometry(brush, null, data);
                else
                    context.DrawGeometry(null,
                        new Pen(brush, 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), data);
            }
        }
    }
}
