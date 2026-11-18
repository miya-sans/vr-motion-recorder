using Avalonia.Controls;
using VRMR.App.ViewModels;

namespace VRMR.App.Views.Takes
{
    /// <summary>The right-hand panel of the Takes workspace: Take, Export and Devices.</summary>
    public partial class TakesInspector : UserControl
    {
        public TakesInspector()
        {
            InitializeComponent();
        }

        /// <summary>Puts the Export tab's content in place.</summary>
        public void SetExportView(Control view)
        {
            ExportHost.Content = view;
        }

        /// <summary>Shows the Take tab with the keyboard focus in the name box (F2).</summary>
        public void FocusName()
        {
            if (DataContext is TakesWorkspaceViewModel vm && vm.HasTake)
            {
                vm.InspectorTab = 0;
                TakeView.FocusName();
            }
        }
    }
}
