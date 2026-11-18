using Avalonia.Controls;

namespace VRMR.App.Views.Takes
{
    /// <summary>The transport under the 3D view. In a narrow window it drops, in this order, the time, the button labels and the step buttons (their keys keep working).</summary>
    public partial class TransportBar : UserControl
    {
        const double HideTimeBelow = 990, HideLabelsBelow = 880, HideStepsBelow = 770;

        public TransportBar()
        {
            InitializeComponent();
            SizeChanged += (o, e) => Fit(e.NewSize.Width);
        }

        void Fit(double width)
        {
            TimeLabel.IsVisible = width >= HideTimeBelow;
            bool labels = width >= HideLabelsBelow;
            OriginButton.Content = labels ? "Origin" : null;
            CalibrateButton.Content = labels ? "Calibrate" : null;
            LiveButton.Content = labels ? "Live" : null;
            bool steps = width >= HideStepsBelow;
            SkipStartButton.IsVisible = StepBackButton.IsVisible = StepForwardButton.IsVisible = SkipEndButton.IsVisible = steps;
        }
    }
}
