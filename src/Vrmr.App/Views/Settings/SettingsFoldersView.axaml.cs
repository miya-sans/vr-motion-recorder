using Avalonia.Controls;
using VRMR.App.ViewModels;

namespace VRMR.App.Views.Settings
{
    /// <summary>Settings → Folders.</summary>
    public partial class SettingsFoldersView : UserControl
    {
        public SettingsFoldersView()
        {
            InitializeComponent();
            DataContextChanged += (o, e) =>
            {
                if (DataContext is not SettingsFoldersViewModel vm) return;
                vm.PickFolder = (title, start) => FilePickers.FolderAsync(this, title, start);
                vm.PickBlender = () => FilePickers.BlenderAsync(this);
            };
        }
    }
}
