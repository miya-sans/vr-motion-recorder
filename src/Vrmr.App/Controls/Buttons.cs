using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace VRMR.App.Controls
{
    /// <summary>
    ///     A button that shows an icon, alone or before its text. Icon-only buttons need a tooltip: it also becomes the
    ///     accessible name.
    /// </summary>
    public class IconButton : Button
    {
        public static readonly StyledProperty<Geometry> IconProperty =
            AvaloniaProperty.Register<IconButton, Geometry>(nameof(Icon));

        public static readonly StyledProperty<bool> IconFilledProperty =
            AvaloniaProperty.Register<IconButton, bool>(nameof(IconFilled));

        public Geometry Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
        public bool IconFilled { get => GetValue(IconFilledProperty); set => SetValue(IconFilledProperty, value); }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ToolTip.TipProperty) AccessibleNames.FromTip(this);
        }
    }

    /// <summary>An icon button that stays pressed while on (loop, live, the gizmo).</summary>
    public class IconToggle : ToggleButton
    {
        public static readonly StyledProperty<Geometry> IconProperty =
            AvaloniaProperty.Register<IconToggle, Geometry>(nameof(Icon));

        public static readonly StyledProperty<bool> IconFilledProperty =
            AvaloniaProperty.Register<IconToggle, bool>(nameof(IconFilled));

        public Geometry Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
        public bool IconFilled { get => GetValue(IconFilledProperty); set => SetValue(IconFilledProperty, value); }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ToolTip.TipProperty) AccessibleNames.FromTip(this);
        }
    }

    /// <summary>A small on/off chip (the view chips: Mesh, Skeleton, Devices…). Dimmed when what it shows is not available.</summary>
    public class ToggleChip : ToggleButton
    {
        public static readonly StyledProperty<bool> AvailableProperty =
            AvaloniaProperty.Register<ToggleChip, bool>(nameof(Available), true);

        /// <summary>False when the view has nothing of this kind to show right now (the chip still remembers its state).</summary>
        public bool Available { get => GetValue(AvailableProperty); set => SetValue(AvailableProperty, value); }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == AvailableProperty) PseudoClasses.Set(":unavailable", !Available);
        }
    }

    static class AccessibleNames
    {
        /// <summary>The first line of the tooltip names an icon-only control for screen readers.</summary>
        public static void FromTip(Control c)
        {
            if (!string.IsNullOrEmpty(AutomationProperties.GetName(c))) return;
            if (ToolTip.GetTip(c) is string tip && tip.Length > 0)
            {
                int cut = tip.IndexOfAny(new[] { '\n', '(', '.' });
                AutomationProperties.SetName(c, (cut > 0 ? tip.Substring(0, cut) : tip).Trim());
            }
        }
    }
}
