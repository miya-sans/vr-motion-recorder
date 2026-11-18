using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless;
using VRMR.App.Composition;
using VRMR.App.Platform;
using VRMR.App.Services;
using VRMR.App.ViewModels;
using VRMR.App.Views;
using Xunit;

namespace VRMR.App.Tests
{
    /// <summary>
    ///     Renders the main screens and states without showing a window. With VRMR_SHOTS set the frames are saved as PNG
    ///     for looking at; the assertions only check that each state was reached and drawn.
    /// </summary>
    [Collection("app")]
    public class ScreenshotTests
    {
        static async Task<MainWindow> Open(AppHost host, double width = 1400, double height = 860, double scale = 1)
        {
            var window = new MainWindow(new ShellViewModel(host)) { Width = width, Height = height };
            window.Show();
            if (scale != 1) window.SetRenderScaling(scale);
            await HeadlessUi.SettleAsync(150);
            return window;
        }

        [Fact]
        public async Task EmptyLibraryShowsWhatToDoNext()
        {
            using var data = new ScratchData();
            await HeadlessUi.RunAsync(async () =>
            {
                var host = data.NewHost(new AvaloniaUiDispatcher());
                var window = await Open(host);
                var shell = (ShellViewModel)window.DataContext;
                Assert.True(await HeadlessUi.WaitForAsync(() => !shell.Library.IsLoading));
                Assert.True(shell.Library.IsEmpty);
                Assert.True(shell.Takes.ShowEmpty);
                Assert.NotNull(HeadlessUi.Capture(window, "01-empty"));
                window.Close();
                host.Dispose();
            });
        }

        [Fact]
        public async Task TakeOpensSolvesAndDraws()
        {
            Assert.SkipUnless(PrivateData.Available, PrivateData.SkipReason);
            await HeadlessUi.RunAsync(async () =>
            {
                var host = PrivateData.NewHost(new AvaloniaUiDispatcher());
                var window = await Open(host);
                var shell = (ShellViewModel)window.DataContext;
                Assert.True(await HeadlessUi.WaitForAsync(() => shell.Library.Items.Count > 0));
                HeadlessUi.Capture(window, "02-library");
                shell.Library.Selected = shell.Library.Items.Last();
                Assert.True(await HeadlessUi.WaitForAsync(() => host.Session.Solve == SolveState.Done, 60000),
                    "the take did not solve: " + host.Session.SolveError);
                await HeadlessUi.SettleAsync(200);
                Assert.NotNull(host.Session.Clip);
                Assert.False(shell.Takes.IsLive);
                Assert.NotNull(HeadlessUi.Capture(window, "03-take-open"));
                window.Close();
                host.Dispose();
            });
        }
    }
}
