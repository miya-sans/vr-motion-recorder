using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using VRMR.App.Platform;
using VRMR.App.Services;
using VRMR.App.ViewModels;
using VRMR.App.Views;
using Xunit;

namespace VRMR.App.Tests
{
    /// <summary>
    /// Renders the main screens and states without showing a window. With VRMR_SHOTS set the frames are saved as PNG
    /// for looking at; the assertions only check that each state was reached and drawn.
    /// </summary>
    [Collection("app")]
    public class ScreenshotTests
    {
        static MainWindow Open(Composition.AppHost host, double width = 1400, double height = 860, double scale = 1)
        {
            var window = new MainWindow(new ShellViewModel(host)) { Width = width, Height = height };
            window.Show();
            if (scale != 1) window.SetRenderScaling(scale);
            HeadlessUi.Settle(150);
            return window;
        }

        [Fact]
        public void EmptyLibraryShowsWhatToDoNext()
        {
            using var data = new ScratchData();
            HeadlessUi.Run(() =>
            {
                var host = data.NewHost(new AvaloniaUiDispatcher());
                var window = Open(host);
                var shell = (ShellViewModel)window.DataContext;
                Assert.True(HeadlessUi.WaitFor(() => !shell.Library.IsLoading));
                Assert.True(shell.Library.IsEmpty);
                Assert.True(shell.Takes.ShowEmpty);
                Assert.NotNull(HeadlessUi.Capture(window, "01-empty"));
                window.Close();
                host.Dispose();
            });
        }

        [Fact]
        public void TakeOpensSolvesAndDraws()
        {
            Assert.SkipUnless(PrivateData.Available, PrivateData.SkipReason);
            HeadlessUi.Run(() =>
            {
                var host = PrivateData.NewHost(new AvaloniaUiDispatcher());
                var window = Open(host);
                var shell = (ShellViewModel)window.DataContext;
                Assert.True(HeadlessUi.WaitFor(() => shell.Library.Items.Count > 0));
                HeadlessUi.Capture(window, "02-library");
                shell.Library.Selected = shell.Library.Items.Last();
                Assert.True(HeadlessUi.WaitFor(() => host.Session.Solve == SolveState.Done, 60000), "the take did not solve: " + host.Session.SolveError);
                HeadlessUi.Settle(200);
                Assert.NotNull(host.Session.Clip);
                Assert.False(shell.Takes.IsLive);
                Assert.NotNull(HeadlessUi.Capture(window, "03-take-open"));
                window.Close();
                host.Dispose();
            });
        }
    }
}
