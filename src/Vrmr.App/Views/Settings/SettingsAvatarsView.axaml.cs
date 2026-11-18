using Avalonia.Controls;
using VRMR.App.ViewModels;

namespace VRMR.App.Views.Settings
{
    /// <summary>Settings → Avatars.</summary>
    public partial class SettingsAvatarsView : UserControl
    {
        public SettingsAvatarsView()
        {
            InitializeComponent();
            DataContextChanged += (o, e) =>
            {
                if (DataContext is not SettingsAvatarsViewModel vm) return;
                vm.PickFiles = () => FilePickers.AvatarFilesAsync(this);
                vm.PickUnityModel = () => FilePickers.UnityModelAsync(this, null);
                vm.PickSaveFbx = (name, start) => FilePickers.SaveFbxAsync(this, name, start);
                vm.ConfirmRemove = name => FilePickers.ConfirmAsync(this, $"Remove \"{name}\" from the library?",
                    "Its files go to the Recycle Bin and can be restored from there. Takes keep their own copy of the skeleton.",
                    "Remove");
            };
        }
    }
}
