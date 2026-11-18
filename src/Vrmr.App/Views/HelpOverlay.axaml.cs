using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using VRMR.App.ViewModels;

namespace VRMR.App.Views
{
    /// <summary>The shortcut sheet. The list here is the single place the keyboard map is written down for users.</summary>
    public partial class HelpOverlay : UserControl
    {
        /// <summary>Group, keys, what they do. A null key starts a group.</summary>
        public static readonly (string keys, string action)[] Shortcuts =
        {
            (null, "Playback"),
            ("Space", "Play or pause"),
            ("← →", "One frame back or forward"),
            ("Home  End", "Go to the start or the end"),
            ("F", "Fit the avatar in the view"),
            (null, "Editing a take"),
            ("I  O", "Start or end the take at the playhead"),
            ("Del", "Remove the selected range"),
            ("Ctrl+Z", "Undo"),
            ("Ctrl+Shift+Z  Ctrl+Y", "Redo"),
            ("Ctrl+S", "Save the edits"),
            ("F2", "Rename the take"),
            ("W  E  Esc", "Placement gizmo: move, turn, off"),
            (null, "Timeline"),
            ("Drag on the ruler", "Move the playhead"),
            ("Drag in the lanes", "Select a range"),
            ("Drag a yellow handle", "Trim the start or the end"),
            ("Right-click a cut", "Bring the cut part back"),
            ("Wheel, right drag", "Zoom, pan"),
            (null, "3D view"),
            ("Drag", "Orbit"),
            ("Right or middle drag", "Pan"),
            ("Wheel", "Zoom"),
            ("Double-click", "Fit everything"),
            (null, "Window"),
            ("Ctrl+F", "Search the takes"),
            ("Ctrl+O", "Open a take file"),
            ("Ctrl+1  Ctrl+2  Ctrl+3", "Show or hide the library, the inspector, the timeline"),
            ("Ctrl+,", "Settings"),
            ("F1", "This sheet"),
            (null, "Number fields"),
            ("Drag sideways", "Change the value (Shift fine, Ctrl coarse)"),
            ("↑ ↓", "Step the value"),
        };

        public HelpOverlay()
        {
            InitializeComponent();
            var left = this.FindControl<StackPanel>("LeftColumn");
            var right = this.FindControl<StackPanel>("RightColumn");
            int half = 0, groups = 0;
            foreach (var s in Shortcuts) if (s.keys == null) groups++;
            int seen = 0;
            for (int i = 0; i < Shortcuts.Length; i++)
            {
                var (keys, action) = Shortcuts[i];
                if (keys == null) { seen++; if (seen > (groups + 1) / 2 && half == 0) half = i; }
                var column = half == 0 ? left : right;
                if (keys == null)
                {
                    var head = new TextBlock { Text = action, Margin = new Thickness(0, column.Children.Count == 0 ? 0 : 10, 0, 2) };
                    head.Classes.Add("title");
                    column.Children.Add(head);
                    continue;
                }
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*") };
                var k = new TextBlock { Text = keys, VerticalAlignment = VerticalAlignment.Center };
                k.Classes.Add("secondary");
                var a = new TextBlock { Text = action, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                Grid.SetColumn(a, 1);
                row.Children.Add(k); row.Children.Add(a);
                column.Children.Add(row);
            }
        }

        void OnScrimPressed(object sender, PointerPressedEventArgs e)
        {
            // a click beside the card closes the sheet
            var card = this.FindControl<Border>("Card");
            if (e.Source is Visual v && card != null && (v == card || card.IsVisualAncestorOf(v))) return;
            if (DataContext is ShellViewModel shell) shell.HelpOpen = false;
        }
    }
}
