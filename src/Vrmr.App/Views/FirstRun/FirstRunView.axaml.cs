using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using VRMR.App.ViewModels;
using VRMR.App.Views.Settings;

namespace VRMR.App.Views.FirstRun
{
    /// <summary>
    ///     The first-run guide. It shows itself while its view-model is open, with the keyboard focus on the button that
    ///     goes on.
    /// </summary>
    public partial class FirstRunView : UserControl
    {
        FirstRunViewModel _model;

        public FirstRunView()
        {
            InitializeComponent();
            DataContextChanged += (o, e) =>
            {
                if (_model != null) _model.PropertyChanged -= OnModelChanged;
                _model = DataContext as FirstRunViewModel;
                if (_model == null) return;
                _model.PropertyChanged += OnModelChanged;
                _model.PickFolder = (title, start) => FilePickers.FolderAsync(this, title, start);
                _model.PickFiles = () => FilePickers.AvatarFilesAsync(this);
            };
        }

        void OnModelChanged(object sender, PropertyChangedEventArgs e)
        {
            // Enter and Space go on from wherever the guide is
            if (sender is not FirstRunViewModel model || !model.IsOpen) return;
            if (e.PropertyName is nameof(FirstRunViewModel.IsOpen) or nameof(FirstRunViewModel.Step))
                Dispatcher.UIThread.Post(() => (model.IsLastStep ? FinishButton : NextButton).Focus());
        }
    }
}
