using Avalonia;
using Avalonia.Controls;
using VRMR.App.ViewModels;

namespace VRMR.App.Views.Settings
{
    /// <summary>Settings → View.</summary>
    public partial class SettingsViewOptionsView : UserControl
    {
        public SettingsViewOptionsView()
        {
            InitializeComponent();
            DataContextChanged += (o, e) =>
            {
                if (DataContext is SettingsViewOptionsViewModel vm)
                    vm.ApplyTheme = theme =>
                    {
                        if (Application.Current is { } app) App.ApplyTheme(app, theme);
                    };
            };
        }
    }
}
