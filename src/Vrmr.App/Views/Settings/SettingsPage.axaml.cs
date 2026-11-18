using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.VisualTree;
using VRMR.App.ViewModels;

namespace VRMR.App.Views.Settings
{
    using Key = Avalonia.Input.Key;

    /// <summary>
    /// The Settings page. It tells its view-model while it is on screen (live states are only read then), and makes
    /// Enter in a text field apply the value like leaving the field does.
    /// </summary>
    public partial class SettingsPage : UserControl
    {
        readonly List<Visual> _watched = new List<Visual>();
        SettingsViewModel _model;

        public SettingsPage()
        {
            InitializeComponent();
            AddHandler(KeyDownEvent, OnKeyDown);
            DataContextChanged += (o, e) =>
            {
                _model?.SetActive(false);
                _model = DataContext as SettingsViewModel;
                UpdateActive();
            };
            CategoryList.SelectionChanged += (o, e) => CategoryScroll.Offset = default; // a new category starts at its top
        }

        /// <summary>Puts the keyboard focus on the category list (when the page opens).</summary>
        public void FocusCategories() => CategoryList.Focus();

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            // the page is hidden by hiding its host: watch every visual above it
            for (Visual v = this; v != null; v = v.GetVisualParent())
            {
                v.PropertyChanged += OnVisibilityChanged;
                _watched.Add(v);
            }
            UpdateActive();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            foreach (var v in _watched) v.PropertyChanged -= OnVisibilityChanged;
            _watched.Clear();
            UpdateActive();
            base.OnDetachedFromVisualTree(e);
        }

        void OnVisibilityChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == IsVisibleProperty) UpdateActive();
        }

        void UpdateActive()
        {
            bool shown = _watched.Count > 0;
            foreach (var v in _watched) shown &= v.IsVisible;
            _model?.SetActive(shown);
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            // text fields apply when left; Enter applies without leaving
            if (e.Key != Key.Enter || e.Source is not TextBox box || box.AcceptsReturn) return;
            BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }
}
