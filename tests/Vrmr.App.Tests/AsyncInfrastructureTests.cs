using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using Xunit;

namespace VRMR.App.Tests
{
    /// <summary>Async test dispatch preserves UI access and delivers failures to the test runner.</summary>
    [Collection("app")]
    public class AsyncInfrastructureTests
    {
        [Fact]
        public async Task HeadlessContinuationsStayOnTheUiThread()
        {
            await HeadlessUi.RunAsync(async () =>
            {
                Assert.True(Dispatcher.UIThread.CheckAccess());
                await Task.Delay(10);
                Assert.True(Dispatcher.UIThread.CheckAccess());
                bool posted = false;
                Dispatcher.UIThread.Post(() => posted = true);
                Assert.True(await HeadlessUi.WaitForAsync(() => posted));
                Assert.False(await HeadlessUi.WaitForAsync(() => false, 10));
            });
        }

        [Fact]
        public async Task AsyncUiFailuresReachTheCaller()
        {
            var failure = new InvalidOperationException("after await");
            var caught = await Assert.ThrowsAsync<InvalidOperationException>(() => HeadlessUi.RunAsync(async () =>
            {
                await Task.Delay(10);
                throw failure;
            }));
            Assert.Same(failure, caught);
        }

        [Fact]
        public async Task ManualAsyncWaitDrainsWorkAndTimesOut()
        {
            var ui = new ManualDispatcher();
            bool delivered = false;
            var posting = Task.Run(() => ui.Post(() => delivered = true), TestContext.Current.CancellationToken);
            Assert.True(await ui.WaitForAsync(() => delivered));
            await posting;
            Assert.False(await ui.WaitForAsync(() => false, 10));
        }
    }
}
