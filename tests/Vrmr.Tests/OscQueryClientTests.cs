using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace VRMR.Tests
{
    public class OscQueryClientTests
    {
        static async Task Respond(TcpListener listener, string body, int status, CancellationToken token)
        {
            using var peer = await listener.AcceptTcpClientAsync(token);
            using var stream = peer.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
            Assert.Equal("GET /avatar HTTP/1.1", await reader.ReadLineAsync(token));
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(token))) { }
            byte[] content = Encoding.UTF8.GetBytes(body);
            byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, token);
            await stream.WriteAsync(content, token);
        }

        [Fact]
        public async Task FetchReadsTheAvatarAndNestedParameters()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            const string body = "{\"CONTENTS\":{\"change\":{\"VALUE\":[\"avtr_test\"]},\"name\":{\"VALUE\":[\"人形\"]},\"eyeheight\":{\"VALUE\":[1.6]},\"parameters\":{\"CONTENTS\":{\"test\":{\"FULL_PATH\":\"/avatar/parameters/Test\",\"TYPE\":\"f\",\"VALUE\":[0.75]}}}}}";
            var token = TestContext.Current.CancellationToken;
            var responding = Respond(listener, body, 200, token);
            var snapshot = await Task.Run(() => VrcOscQueryClient.Fetch(IPAddress.Loopback, port, cancellationToken: token), token);
            await responding;
            Assert.Equal("avtr_test", snapshot.AvatarId);
            Assert.Equal("人形", snapshot.AvatarName);
            Assert.Equal(1.6f, snapshot.EyeHeight);
            Assert.Equal(0.75f, snapshot.Params["Test"].Item2);
        }

        [Fact]
        public async Task FetchRejectsHttpErrors()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var token = TestContext.Current.CancellationToken;
            var responding = Respond(listener, "unavailable", 503, token);
            await Assert.ThrowsAsync<HttpRequestException>(() => Task.Run(() =>
                VrcOscQueryClient.Fetch(IPAddress.Loopback, port, cancellationToken: token), token));
            await responding;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FetchStopsWhenTheRequestTimesOutOrIsCancelled(bool cancel)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            if (cancel) cancellation.CancelAfter(100);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() =>
                VrcOscQueryClient.Fetch(IPAddress.Loopback, port, cancel ? 5000 : 100, cancellation.Token),
                TestContext.Current.CancellationToken));
        }
    }
}
