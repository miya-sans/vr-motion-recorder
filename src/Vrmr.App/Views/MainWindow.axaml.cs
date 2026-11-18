using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using VRMR.App.Controls;
using VRMR.App.ViewModels;
using VRMR.App.Viewport;
using VRMR.App.Views.FirstRun;
using VRMR.App.Views.Settings;
using VRMR.App.Views.Takes;
using VisualExtensions = Avalonia.VisualTree.VisualExtensions;

namespace VRMR.App.Views
{
    using Key = Avalonia.Input.Key;

    /// <summary>
    ///     The window: lays out the panels, remembers their sizes and the window's place, routes the keyboard map to the
    ///     workspace, and hands the 3D view to it. No logic of the app lives here.
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>Below this width the inspector lies over the view instead of pushing it.</summary>
        public const double NarrowWidth = 1280;

        readonly ShellViewModel _shell;
        bool _narrow;

        public MainWindow()
        {
            InitializeComponent();
            AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
            AddHandler(PointerWheelChangedEvent, OnPreviewWheel, RoutingStrategies.Tunnel);
        }

        public MainWindow(ShellViewModel shell) : this()
        {
            _shell = shell;
            DataContext = shell;
            Section.StateStore = shell.Host.UiState;
            NumberField.SetGestureSource(InspectorHost, shell.Takes);
            RestorePlacement();
            ApplyPanelSizes();
            shell.PropertyChanged += (o, e) =>
            {
                if (e.PropertyName is nameof(ShellViewModel.LibraryOpen) or nameof(ShellViewModel.InspectorOpen)
                    or nameof(ShellViewModel.TimelineOpen)) ApplyPanelSizes();
            };
            shell.Library.ConfirmDelete = ConfirmDeleteAsync;
            shell.Library.PickFile = PickTakeAsync;
            LibrarySplitter.DragCompleted += (o, e) =>
            {
                if (_shell.LibraryOpen) _shell.LibraryWidth = MainGrid.ColumnDefinitions[0].ActualWidth;
            };
            InspectorSplitter.DragCompleted += (o, e) =>
            {
                if (_shell.InspectorOpen && !_narrow) _shell.InspectorWidth = MainGrid.ColumnDefinitions[4].ActualWidth;
            };
            TimelineSplitter.DragCompleted += (o, e) =>
            {
                if (_shell.TimelineOpen) _shell.TimelineHeight = CentreGrid.RowDefinitions[3].ActualHeight;
            };
            View.HiddenMeshes = () => shell.Host.AvatarService.HiddenMeshes;
            shell.Host.SettingsStore.Changed += what =>
            {
                if (what == nameof(VRMR.Settings.PreviewHiddenMeshes)) View.SceneChanged();
            };
            shell.Takes.AttachView(View);
            var settings = new SettingsPage { DataContext = shell.SettingsPage };
            settings.FindControl<ContentControl>("CalibrationDefaultsHost").Content = new CalibrationSettingsView
                { DataContext = shell.CalibrationDefaults };
            PageHost.Content = settings;
            FirstRunHost.Content = new FirstRunView { DataContext = shell.FirstRun };
            TakesInspectorView.SetExportView(new ExportView { DataContext = new ExportViewModel(shell.Host) });
        }

        /// <summary>The 3D view in the centre.</summary>
        public IViewport Viewport => View;

        /// <summary>The take timeline under the transport.</summary>
        public TimelineControl TimelineView => Timeline;

        // ------------------------------------------------------------------------------------------ keyboard

        /// <summary>True while typing would be stolen by a shortcut: a text box has the focus.</summary>
        bool Typing => FocusManager?.GetFocusedElement() is TextBox;

        // ------------------------------------------------------------------------------------------ layout

        void ApplyPanelSizes()
        {
            if (_shell == null) return;
            var cols = MainGrid.ColumnDefinitions;
            cols[0].Width = _shell.LibraryOpen ? new GridLength(Math.Max(200, _shell.LibraryWidth)) : new GridLength(0);
            cols[0].MinWidth = _shell.LibraryOpen ? 200 : 0;
            bool side = _shell.InspectorOpen && !_narrow;
            cols[4].Width = side ? new GridLength(Math.Max(280, _shell.InspectorWidth)) : new GridLength(0);
            cols[4].MinWidth = side ? 280 : 0;
            // narrow windows: the inspector lies over the right edge of the view
            Grid.SetColumn(InspectorHost, _narrow ? 2 : 4);
            InspectorHost.HorizontalAlignment = _narrow ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            InspectorHost.Width = _narrow ? Math.Max(280, _shell.InspectorWidth) : double.NaN;
            InspectorHost.BorderThickness = _narrow ? new Thickness(1, 0, 0, 0) : default;
            InspectorHost.BorderBrush = this.TryFindResource("BorderBrush", ActualThemeVariant, out object b)
                ? b as IBrush
                : null;
            InspectorSplitter.IsVisible = side;
            var rows = CentreGrid.RowDefinitions;
            rows[3].Height = _shell.TimelineOpen
                ? new GridLength(Math.Max(96, _shell.TimelineHeight))
                : new GridLength(0);
            rows[3].MinHeight = _shell.TimelineOpen ? 96 : 0;
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            bool narrow = e.NewSize.Width < NarrowWidth;
            if (narrow != _narrow)
            {
                _narrow = narrow;
                ApplyPanelSizes();
            }
        }

        void RestorePlacement()
        {
            var ui = _shell.Host.UiState;
            double w = ui.GetNumber("window.width", 1400), h = ui.GetNumber("window.height", 860);
            Width = Math.Max(MinWidth, w);
            Height = Math.Max(MinHeight, h);
            double x = ui.GetNumber("window.x", double.NaN), y = ui.GetNumber("window.y", double.NaN);
            if (!double.IsNaN(x) && !double.IsNaN(y))
            {
                var pos = new PixelPoint((int)x, (int)y);
                // only when the title bar would be on a screen that still exists
                bool visible = Screens?.All.Any(s => s.WorkingArea.Contains(new PixelPoint(pos.X + 60, pos.Y + 20))) ??
                               false;
                if (visible)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Position = pos;
                }
                else WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            else WindowStartupLocation = WindowStartupLocation.CenterScreen;

            if (ui.GetBool("window.maximized", false)) WindowState = WindowState.Maximized;
        }

        void SavePlacement()
        {
            if (_shell == null) return;
            var ui = _shell.Host.UiState;
            ui.Set("window.maximized", WindowState == WindowState.Maximized);
            if (WindowState == WindowState.Normal)
            {
                ui.Set("window.x", Position.X);
                ui.Set("window.y", Position.Y);
                ui.Set("window.width", Math.Round(Width));
                ui.Set("window.height", Math.Round(Height));
            }
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            SavePlacement();
            base.OnClosing(e);
        }

        // ------------------------------------------------------------------------------------------ dialogs

        async Task<bool> ConfirmDeleteAsync(string name)
        {
            string note = "The take can be restored from the Recycle Bin." +
                          (_shell.SceneAvailable ? " Scenes that use it lose it until then." : "");
            var dialog = new ConfirmDialog($"Move \"{name}\" to the Recycle Bin?", note, "Delete");
            return await dialog.ShowDialog<bool>(this);
        }

        async Task<string> PickTakeAsync()
        {
            var start = await StorageProvider.TryGetFolderFromPathAsync(_shell.Library.Folder);
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open a take", AllowMultiple = false, SuggestedStartLocation = start,
                FileTypeFilter = new[]
                    { new FilePickerFileType("VR Motion Recorder take") { Patterns = new[] { "*.vrmr" } } }
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }

        // ------------------------------------------------------------------------------------------ mouse wheel

        /// <summary>
        ///     The wheel never changes a value. A drop-down that has the focus would step through its choices: over a closed
        ///     drop-down the wheel scrolls the panel it sits in instead. An open list scrolls as usual (it is a window of its
        ///     own).
        /// </summary>
        void OnPreviewWheel(object sender, PointerWheelEventArgs e)
        {
            if (!(e.Source is Visual source)) return;
            var combo = source as ComboBox ?? VisualExtensions.FindAncestorOfType<ComboBox>(source);
            if (combo == null || combo.IsDropDownOpen) return;
            e.Handled = true;
            var scroller = VisualExtensions.FindAncestorOfType<ScrollViewer>(combo);
            if (scroller == null) return;
            double max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
            scroller.Offset = new Vector(scroller.Offset.X,
                Math.Max(0, Math.Min(max, scroller.Offset.Y - e.Delta.Y * 48)));
        }

        void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_shell == null || e.Handled) return;
            bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0,
                shift = (e.KeyModifiers & KeyModifiers.Shift) != 0,
                alt = (e.KeyModifiers & KeyModifiers.Alt) != 0;
            if (alt) return;

            // window-wide commands work everywhere, also while typing
            if (e.Key == Key.F1)
            {
                _shell.HelpOpen = !_shell.HelpOpen;
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && _shell.CloseTopPage())
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F2 && _shell.IsTakes && _shell.Takes.HasTake)
            {
                _shell.InspectorOpen = true;
                TakesInspectorView.FocusName();
                e.Handled = true;
                return;
            }

            if (ctrl)
            {
                switch (e.Key)
                {
                    case Key.S:
                        if (_shell.IsTakes)
                        {
                            _shell.Takes.SaveCommand.Execute(null);
                            e.Handled = true;
                        }

                        return;
                    case Key.O:
                        _shell.Library.OpenFileCommand.Execute(null);
                        e.Handled = true;
                        return;
                    case Key.F:
                        _shell.LibraryOpen = true;
                        this.GetVisualDescendantOfType<LibraryPanel>()?.FocusSearch();
                        e.Handled = true;
                        return;
                    case Key.OemComma:
                        _shell.SettingsOpen = true;
                        e.Handled = true;
                        return;
                    case Key.D1:
                        _shell.LibraryOpen = !_shell.LibraryOpen;
                        e.Handled = true;
                        return;
                    case Key.D2:
                        _shell.InspectorOpen = !_shell.InspectorOpen;
                        e.Handled = true;
                        return;
                    case Key.D3:
                        _shell.TimelineOpen = !_shell.TimelineOpen;
                        e.Handled = true;
                        return;
                }
            }

            if (Typing || _shell.SettingsOpen || _shell.HelpOpen || _shell.FirstRun.IsOpen || !_shell.IsTakes) return;

            var takes = _shell.Takes;
            if (ctrl)
            {
                switch (e.Key)
                {
                    case Key.Z:
                        if (shift) takes.RedoCommand.Execute(null);
                        else takes.UndoCommand.Execute(null);
                        e.Handled = true;
                        return;
                    case Key.Y:
                        takes.RedoCommand.Execute(null);
                        e.Handled = true;
                        return;
                }

                return;
            }

            // the placement gizmo's keys come first while it is on
            if (takes.GizmoOn && !shift)
            {
                switch (e.Key)
                {
                    case Key.W:
                    case Key.G:
                        takes.SetGizmo(true, false);
                        e.Handled = true;
                        return;
                    case Key.E:
                    case Key.R:
                        takes.SetGizmo(true, true);
                        e.Handled = true;
                        return;
                    case Key.Escape:
                        takes.SetGizmo(false);
                        e.Handled = true;
                        return;
                }
            }

            if (shift) return;
            // Space and the arrows belong to a focused list, combo box or button; elsewhere they drive the transport
            var focus = FocusManager?.GetFocusedElement();
            bool inList = focus is ListBoxItem || focus is ComboBox || focus is ComboBoxItem || focus is MenuItem;
            switch (e.Key)
            {
                case Key.Space:
                    if (focus is Button || focus is CheckBox || inList) return;
                    takes.TogglePlayCommand.Execute(null);
                    break;
                case Key.Left:
                    if (inList) return;
                    takes.StepBackCommand.Execute(null);
                    break;
                case Key.Right:
                    if (inList) return;
                    takes.StepForwardCommand.Execute(null);
                    break;
                case Key.Home:
                    if (inList) return;
                    takes.SeekStartCommand.Execute(null);
                    break;
                case Key.End:
                    if (inList) return;
                    takes.SeekEndCommand.Execute(null);
                    break;
                case Key.I: takes.SetInCommand.Execute(null); break;
                case Key.O: takes.SetOutCommand.Execute(null); break;
                case Key.Delete: takes.CutCommand.Execute(null); break;
                case Key.F: takes.FrameAllCommand.Execute(null); break;
                default: return;
            }

            e.Handled = true;
        }
    }

    static class VisualSearch
    {
        /// <summary>The first control of a type below a visual, or null.</summary>
        public static T GetVisualDescendantOfType<T>(this Visual root) where T : class
        {
            foreach (var v in VisualExtensions.GetVisualDescendants(root))
                if (v is T t)
                    return t;
            return null;
        }
    }
}
