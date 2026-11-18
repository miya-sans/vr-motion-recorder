using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;

namespace VRMR.App.Controls
{
    using Key = Avalonia.Input.Key;

    /// <summary>Something that can group a run of changes into one undo step (the open take, the open scene).</summary>
    public interface IGestureSource
    {
        /// <summary>Starts a gesture; everything changed until the result is disposed is one undo step.</summary>
        IDisposable BeginGesture();
    }

    /// <summary>
    ///     A number you can type, drag sideways to scrub, or step with the arrow keys. Shift makes drags and steps fine,
    ///     Ctrl coarse. A drag is one undo step. The mouse wheel never changes the value.
    /// </summary>
    public class NumberField : TextBox
    {
        const double DragThreshold = 3;

        public static readonly StyledProperty<double> ValueProperty =
            AvaloniaProperty.Register<NumberField, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<double> MinimumProperty =
            AvaloniaProperty.Register<NumberField, double>(nameof(Minimum), double.NegativeInfinity);

        public static readonly StyledProperty<double> MaximumProperty =
            AvaloniaProperty.Register<NumberField, double>(nameof(Maximum), double.PositiveInfinity);

        public static readonly StyledProperty<double> StepProperty =
            AvaloniaProperty.Register<NumberField, double>(nameof(Step), 1);

        public static readonly StyledProperty<int> DecimalsProperty =
            AvaloniaProperty.Register<NumberField, int>(nameof(Decimals), 1);

        public static readonly StyledProperty<string> UnitProperty =
            AvaloniaProperty.Register<NumberField, string>(nameof(Unit));

        public static readonly StyledProperty<bool> ShowSignProperty =
            AvaloniaProperty.Register<NumberField, bool>(nameof(ShowSign));

        /// <summary>Set on a panel: number fields inside it report their drags to this, so a drag is one undo step.</summary>
        public static readonly AttachedProperty<IGestureSource> GestureSourceProperty =
            AvaloniaProperty.RegisterAttached<NumberField, Control, IGestureSource>("GestureSource", inherits: true);

        IDisposable _gesture;
        Point _pressPoint;
        double _pressValue;
        bool _pressed, _scrubbing, _editing, _syncing;

        static NumberField()
        {
            ValueProperty.Changed.AddClassHandler<NumberField>((f, e) => f.ShowValue());
            DecimalsProperty.Changed.AddClassHandler<NumberField>((f, e) => f.ShowValue());
            UnitProperty.Changed.AddClassHandler<NumberField>((f, e) => f.ShowValue());
        }

        public NumberField()
        {
            Classes.Add("number");
            Cursor = new Cursor(StandardCursorType.SizeWestEast);
            ShowValue();
        }

        protected override Type StyleKeyOverride => typeof(TextBox);

        public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
        public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
        public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

        /// <summary>What one arrow press or three pixels of drag change.</summary>
        public double Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }

        public int Decimals { get => GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }

        /// <summary>Shown after the number while not editing (cm, °, s).</summary>
        public string Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

        /// <summary>Show a plus sign on positive values (offsets).</summary>
        public bool ShowSign { get => GetValue(ShowSignProperty); set => SetValue(ShowSignProperty, value); }

        public static IGestureSource GetGestureSource(Control c)
        {
            return c.GetValue(GestureSourceProperty);
        }

        public static void SetGestureSource(Control c, IGestureSource value)
        {
            c.SetValue(GestureSourceProperty, value);
        }

        /// <summary>Raised when the user changed the value (typing committed, a drag move, an arrow step).</summary>
        public event EventHandler ValueEdited;

        string Format(double v, bool withUnit)
        {
            string s = v.ToString("F" + Math.Max(0, Decimals), CultureInfo.InvariantCulture);
            if (ShowSign && v > 0 && double.Parse(s, CultureInfo.InvariantCulture) != 0) s = "+" + s;
            return withUnit && !string.IsNullOrEmpty(Unit) ? s + " " + Unit : s;
        }

        void ShowValue()
        {
            if (_editing) return;
            _syncing = true;
            try { Text = Format(Value, true); }
            finally { _syncing = false; }
        }

        /// <summary>Reads typed text as a number: decimal comma or point, an optional unit after it.</summary>
        public static bool TryParse(string text, out double value)
        {
            text = (text ?? "").Trim().Replace(',', '.');
            int end = 0;
            while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.' || text[end] == '-' ||
                                         text[end] == '+' || text[end] == 'e' || text[end] == 'E')) end++;
            return double.TryParse(text.Substring(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        void SetFromUser(double v)
        {
            v = Math.Max(Minimum, Math.Min(Maximum, v));
            double scale = Math.Pow(10, Math.Max(0, Decimals));
            v = Math.Round(v * scale) / scale;
            if (v == Value) return;
            SetCurrentValue(ValueProperty, v);
            ValueEdited?.Invoke(this, EventArgs.Empty);
        }

        void Commit()
        {
            if (!_editing) return;
            _editing = false;
            if (TryParse(Text, out double v)) SetFromUser(v);
            ShowValue();
            Cursor = new Cursor(StandardCursorType.SizeWestEast);
        }

        void BeginEdit()
        {
            _editing = true;
            _syncing = true;
            try { Text = Format(Value, false); }
            finally { _syncing = false; }

            Cursor = new Cursor(StandardCursorType.Ibeam);
            SelectAll();
        }

        static double Factor(KeyModifiers m)
        {
            return (m & KeyModifiers.Shift) != 0 ? 0.1 : (m & KeyModifiers.Control) != 0 ? 10 : 1;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (_editing || IsReadOnly || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                base.OnPointerPressed(e);
                return;
            }

            _pressed = true;
            _scrubbing = false;
            _pressPoint = e.GetPosition(this);
            _pressValue = Value;
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            if (!_pressed)
            {
                base.OnPointerMoved(e);
                return;
            }

            double dx = e.GetPosition(this).X - _pressPoint.X;
            if (!_scrubbing)
            {
                if (Math.Abs(dx) < DragThreshold) return;
                _scrubbing = true;
                _gesture = GetGestureSource(this)?.BeginGesture();
            }

            SetFromUser(_pressValue + Math.Round(dx / 3) * Step * Factor(e.KeyModifiers));
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (!_pressed)
            {
                base.OnPointerReleased(e);
                return;
            }

            _pressed = false;
            e.Pointer.Capture(null);
            if (_scrubbing)
            {
                _scrubbing = false;
                _gesture?.Dispose();
                _gesture = null;
            }
            else
            {
                Focus();
                BeginEdit();
            } // a click without a drag: type the value

            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            _pressed = false;
            if (_scrubbing)
            {
                _scrubbing = false;
                _gesture?.Dispose();
                _gesture = null;
            }
        }

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            base.OnGotFocus(e);
            if (!_editing && !_pressed && !IsReadOnly) BeginEdit(); // keyboard focus: ready to type
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            Commit();
            base.OnLostFocus(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    Commit();
                    BeginEdit();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    if (_editing)
                    {
                        _editing = false;
                        ShowValue();
                        BeginEdit();
                        e.Handled = true;
                        return;
                    }

                    break;
                case Key.Up:
                case Key.Down:
                    if (IsReadOnly) break;
                    if (_editing && TryParse(Text, out double typed)) SetFromUser(typed);
                    _editing = false;
                    SetFromUser(Value + (e.Key == Key.Up ? 1 : -1) * Step * Factor(e.KeyModifiers));
                    BeginEdit();
                    e.Handled = true;
                    return;
            }

            base.OnKeyDown(e);
        }

        // the wheel scrolls the panel and never changes a value
        protected override void OnPointerWheelChanged(PointerWheelEventArgs e) { }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == TextProperty && !_syncing && !_editing && IsFocused) _editing = true;
        }
    }
}
