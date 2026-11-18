using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using VRMR.App.ViewModels;

namespace VRMR.App.Views.Takes
{
    /// <summary>The Take tab of the inspector.</summary>
    public partial class TakeInspectorView : UserControl
    {
        public TakeInspectorView()
        {
            InitializeComponent();
            DataContextChanged += (o, e) =>
            {
                if (DataContext is TakeInspectorViewModel vm) vm.PickUnityModel = PickUnityModelAsync;
            };
        }

        /// <summary>Puts the keyboard focus into the name box (F2).</summary>
        public void FocusName()
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            (DataContext as TakeInspectorViewModel)?.SetVisible(true);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            (DataContext as TakeInspectorViewModel)?.SetVisible(false);
            base.OnDetachedFromVisualTree(e);
        }

        async Task<string> PickUnityModelAsync()
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return null;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Pick your avatar's model inside a Unity project (its rig must be Humanoid)",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Humanoid model")
                        { Patterns = new[] { "*.fbx", "*.FBX", "*.meta", "*.blend" } }
                }
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
    }
}
