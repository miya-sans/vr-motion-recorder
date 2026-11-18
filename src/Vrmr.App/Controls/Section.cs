using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using VRMR.App.Services;

namespace VRMR.App.Controls
{
    /// <summary>How a short status text is coloured.</summary>
    public enum Tone { Neutral, Accent, Success, Warning, Error }

    /// <summary>
    /// A titled group that folds. Its open state is remembered per <see cref="StateKey"/>; the summary on the right
    /// tells what is inside while it is closed. <see cref="Level"/> 1 is a panel section, 2 a sub-section ("Advanced").
    /// </summary>
    public class Section : HeaderedContentControl
    {
        /// <summary>Where sections remember their state (set once by the app).</summary>
        public static UiStateStore StateStore { get; set; }

        public static readonly StyledProperty<bool> IsExpandedProperty = AvaloniaProperty.Register<Section, bool>(nameof(IsExpanded), true);
        public static readonly StyledProperty<string> StateKeyProperty = AvaloniaProperty.Register<Section, string>(nameof(StateKey));
        public static readonly StyledProperty<string> SummaryProperty = AvaloniaProperty.Register<Section, string>(nameof(Summary));
        public static readonly StyledProperty<Tone> SummaryToneProperty = AvaloniaProperty.Register<Section, Tone>(nameof(SummaryTone));
        public static readonly StyledProperty<int> LevelProperty = AvaloniaProperty.Register<Section, int>(nameof(Level), 1);

        bool _restored;

        public bool IsExpanded { get => GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

        /// <summary>Id under which the open state is remembered (null = not remembered).</summary>
        public string StateKey { get => GetValue(StateKeyProperty); set => SetValue(StateKeyProperty, value); }

        /// <summary>A few words shown in the header, mainly useful while the section is closed.</summary>
        public string Summary { get => GetValue(SummaryProperty); set => SetValue(SummaryProperty, value); }
        public Tone SummaryTone { get => GetValue(SummaryToneProperty); set => SetValue(SummaryToneProperty, value); }

        /// <summary>1 = section, 2 = sub-section.</summary>
        public int Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

        protected override void OnAttachedToLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
        {
            base.OnAttachedToLogicalTree(e);
            if (!_restored && StateKey != null && StateStore != null)
            {
                _restored = true;
                IsExpanded = StateStore.GetBool("fold." + StateKey, IsExpanded);
            }
            _restored = true;
            UpdatePseudoClasses();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsExpandedProperty)
            {
                if (_restored && StateKey != null) StateStore?.Set("fold." + StateKey, IsExpanded);
                UpdatePseudoClasses();
            }
            else if (change.Property == LevelProperty || change.Property == SummaryToneProperty) UpdatePseudoClasses();
        }

        void UpdatePseudoClasses()
        {
            PseudoClasses.Set(":expanded", IsExpanded);
            PseudoClasses.Set(":sub", Level >= 2);
            PseudoClasses.Set(":accent", SummaryTone == Tone.Accent);
            PseudoClasses.Set(":success", SummaryTone == Tone.Success);
            PseudoClasses.Set(":warning", SummaryTone == Tone.Warning);
            PseudoClasses.Set(":error", SummaryTone == Tone.Error);
        }
    }

    /// <summary>
    /// One setting: label on the left, editor on the right, an optional unit after it and a reset-to-default button
    /// that appears when the value differs from the default.
    /// </summary>
    public class FieldRow : ContentControl
    {
        public static readonly StyledProperty<string> LabelProperty = AvaloniaProperty.Register<FieldRow, string>(nameof(Label));
        public static readonly StyledProperty<string> UnitProperty = AvaloniaProperty.Register<FieldRow, string>(nameof(Unit));
        public static readonly StyledProperty<bool> CanResetProperty = AvaloniaProperty.Register<FieldRow, bool>(nameof(CanReset));
        public static readonly StyledProperty<System.Windows.Input.ICommand> ResetCommandProperty = AvaloniaProperty.Register<FieldRow, System.Windows.Input.ICommand>(nameof(ResetCommand));
        public static readonly StyledProperty<double> LabelWidthProperty = AvaloniaProperty.Register<FieldRow, double>(nameof(LabelWidth), double.NaN);

        public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
        public string Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

        /// <summary>The value differs from its default: the reset button shows.</summary>
        public bool CanReset { get => GetValue(CanResetProperty); set => SetValue(CanResetProperty, value); }
        public System.Windows.Input.ICommand ResetCommand { get => GetValue(ResetCommandProperty); set => SetValue(ResetCommandProperty, value); }

        /// <summary>Label column width; the theme's width when not set.</summary>
        public double LabelWidth { get => GetValue(LabelWidthProperty); set => SetValue(LabelWidthProperty, value); }
    }

    /// <summary>What an empty list or view shows: an icon, one sentence saying what to do next, and one button that does it.</summary>
    public class EmptyState : TemplatedControl
    {
        public static readonly StyledProperty<Geometry> IconProperty = AvaloniaProperty.Register<EmptyState, Geometry>(nameof(Icon));
        public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<EmptyState, string>(nameof(Title));
        public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<EmptyState, string>(nameof(Text));
        public static readonly StyledProperty<string> ActionLabelProperty = AvaloniaProperty.Register<EmptyState, string>(nameof(ActionLabel));
        public static readonly StyledProperty<System.Windows.Input.ICommand> CommandProperty = AvaloniaProperty.Register<EmptyState, System.Windows.Input.ICommand>(nameof(Command));

        public Geometry Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
        /// <summary>Optional short heading above the sentence.</summary>
        public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
        public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
        public string ActionLabel { get => GetValue(ActionLabelProperty); set => SetValue(ActionLabelProperty, value); }
        public System.Windows.Input.ICommand Command { get => GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
    }

    /// <summary>A source's state in the top bar: a dot (grey off, amber waiting, green receiving) and its name. Clicking opens its flyout with the details.</summary>
    public class StatusPill : Button
    {
        public static readonly StyledProperty<string> LabelProperty = AvaloniaProperty.Register<StatusPill, string>(nameof(Label));
        public static readonly StyledProperty<string> StateTextProperty = AvaloniaProperty.Register<StatusPill, string>(nameof(StateText));
        public static readonly StyledProperty<SourceLevel> StateProperty = AvaloniaProperty.Register<StatusPill, SourceLevel>(nameof(State));

        public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
        /// <summary>A word or two after the name ("receiving", "3/5 tracked").</summary>
        public string StateText { get => GetValue(StateTextProperty); set => SetValue(StateTextProperty, value); }
        public SourceLevel State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == StateProperty)
            {
                PseudoClasses.Set(":ok", State == SourceLevel.Ok);
                PseudoClasses.Set(":waiting", State == SourceLevel.Waiting);
                PseudoClasses.Set(":warning", State == SourceLevel.Warning);
            }
        }
    }

    /// <summary>A row of mutually exclusive choices shown as one control (Takes | Scene, Move | Turn).</summary>
    public class SegmentedControl : ListBox
    {
        protected override Type StyleKeyOverride => typeof(SegmentedControl);
    }
}
