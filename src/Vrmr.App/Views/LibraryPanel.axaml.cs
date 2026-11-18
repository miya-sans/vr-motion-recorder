using Avalonia.Controls;

namespace VRMR.App.Views
{
    /// <summary>The library panel on the left.</summary>
    public partial class LibraryPanel : UserControl
    {
        public LibraryPanel() => InitializeComponent();

        /// <summary>Puts the keyboard focus into the search box (Ctrl+F).</summary>
        public void FocusSearch()
        {
            var box = this.FindControl<TextBox>("SearchBox");
            box?.Focus();
            box?.SelectAll();
        }
    }
}
