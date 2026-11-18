using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace VRMR.App.Views
{
    using Key = Avalonia.Input.Key;

    /// <summary>
    /// The one kind of dialog the app shows besides file pickers: a question about something that cannot be undone
    /// from inside the app. The safe answer is the default; Esc cancels.
    /// </summary>
    public sealed class ConfirmDialog : Window
    {
        public ConfirmDialog(string question, string detail, string confirmLabel)
        {
            Title = "VR Motion Recorder";
            SizeToContent = SizeToContent.WidthAndHeight;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            MinWidth = 380; MaxWidth = 520;

            var title = new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap };
            title.Classes.Add("title");
            var text = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16) };
            text.Classes.Add("secondary");
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88 };
            var confirm = new Button { Content = confirmLabel, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
            confirm.Classes.Add("danger");
            cancel.Click += (o, e) => Close(false);
            confirm.Click += (o, e) => Close(true);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, confirm } };
            Content = new StackPanel { Margin = new Thickness(20), Children = { title, text, buttons } };
            Opened += (o, e) => cancel.Focus();
            KeyDown += (o, e) => { if (e.Key == Key.Escape) Close(false); };
        }
    }
}
