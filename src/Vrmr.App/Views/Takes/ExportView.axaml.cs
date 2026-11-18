using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using VRMR.App.ViewModels;

namespace VRMR.App.Views.Takes
{
    using Key = Avalonia.Input.Key;

    /// <summary>The Export tab of the inspector.</summary>
    public partial class ExportView : UserControl
    {
        public ExportView()
        {
            InitializeComponent();
            DataContextChanged += (o, e) =>
            {
                if (DataContext is ExportViewModel vm) vm.PickFolder = PickFolderAsync;
            };
            // a typed folder counts once it is confirmed: Enter, or leaving the box
            FolderBox.LostFocus += (o, e) => CommitFolder();
            FolderBox.KeyDown += (o, e) =>
            {
                if (e.Key != Key.Enter) return;
                CommitFolder();
                e.Handled = true;
            };
        }

        void CommitFolder()
        {
            (DataContext as ExportViewModel)?.CommitFolderCommand.Execute(null);
        }

        async Task<string> PickFolderAsync(string start)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return null;
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Export folder", AllowMultiple = false,
                SuggestedStartLocation = await top.StorageProvider.TryGetFolderFromPathAsync(start)
            });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
    }
}
