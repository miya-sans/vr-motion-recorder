using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

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
        TopLevel _pointerRoot;
        Point _pressPoint;
        IPointer _pressedPointer;
        double _pressValue;
        bool _canCancelScrub, _scrubbing, _editing, _syncing;

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
            ApplyUserValue(Math.Round(v * scale) / scale);
        }

        void ApplyUserValue(double v)
        {
            if (!IsEffectivelyEnabled || IsReadOnly || v == Value) return;
            SetCurrentValue(ValueProperty, v);
            ValueEdited?.Invoke(this, EventArgs.Empty);
        }

        void EndEdit(bool commit)
        {
            if (!_editing) return;
            _editing = false;
            if (commit && TryParse(Text, out double v)) SetFromUser(v);
            ShowValue();
            Cursor = new Cursor(StandardCursorType.SizeWestEast);
        }

        void FinishInput(bool commit)
        {
            if (!commit && _canCancelScrub) ApplyUserValue(_pressValue);
            _canCancelScrub = false;
            var pointer = _pressedPointer;
            EndPress();
            pointer?.Capture(null);
            EndEdit(commit);
            if (IsFocused) _pointerRoot?.FocusManager?.Focus(null);
        }

        void OnRootPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (!IsReadOnly && e.Source is Visual source && source != this && !this.IsVisualAncestorOf(source))
                FinishInput(commit: true);
        }

        void EndPress()
        {
            _pressedPointer = null;
            _scrubbing = false;
            _gesture?.Dispose();
            _gesture = null;
        }

        void BeginEdit()
        {
            _canCancelScrub = false;
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

            _pressedPointer = e.Pointer;
            _canCancelScrub = false;
            _scrubbing = false;
            _pressPoint = e.GetPosition(this);
            _pressValue = Value;
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            if (e.Pointer != _pressedPointer)
            {
                base.OnPointerMoved(e);
                return;
            }

            double dx = e.GetPosition(this).X - _pressPoint.X;
            if (!_scrubbing)
            {
                if (Math.Abs(dx) < DragThreshold) return;
                _scrubbing = true;
                _canCancelScrub = true;
                _gesture = GetGestureSource(this)?.BeginGesture();
            }

            SetFromUser(_pressValue + Math.Round(dx / 3) * Step * Factor(e.KeyModifiers));
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (e.Pointer != _pressedPointer)
            {
                base.OnPointerReleased(e);
                return;
            }

            bool click = !_scrubbing;
            EndPress();
            if (click)
            {
                Focus(NavigationMethod.Pointer);
                BeginEdit();
            }

            e.Pointer.Capture(null);
            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (e.Pointer == _pressedPointer) EndPress();
        }

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            base.OnGotFocus(e);
            if (e.Source != this) return;

            _pointerRoot = TopLevel.GetTopLevel(this);
            _pointerRoot?.AddHandler(PointerPressedEvent, OnRootPointerPressed, RoutingStrategies.Tunnel,
                handledEventsToo: true);
            if (e.NavigationMethod != NavigationMethod.Pointer && !_editing && _pressedPointer == null && !IsReadOnly)
                BeginEdit();
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            _canCancelScrub = false;
            _pointerRoot?.RemoveHandler(PointerPressedEvent, OnRootPointerPressed);
            _pointerRoot = null;
            EndEdit(commit: true);
            base.OnLostFocus(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter when !IsReadOnly:
                case Key.Escape when !IsReadOnly:
                    FinishInput(commit: e.Key == Key.Enter);
                    e.Handled = true;
                    return;
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
