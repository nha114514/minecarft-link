using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MCLink.Core;

namespace MCLink.Tests;

public sealed class P2pBridgeTests
{
    [Fact]
    public async Task StopImmediatelyAfterStartDoesNotExposeListenerAbort()
    {
        await using var session = P2pPeerSession.CreateForTests();

        for (var index = 0; index < 100; index++)
        {
            var proxy = new GuestLoopbackProxy(session);
            await proxy.StartAsync();
            await proxy.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task GuestProxyRoundTripsExact256KiBThroughHostMinecraftBridge()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var backend = new LoopbackEchoBackend();
        await using var host = P2pPeerSession.CreateForTests();
        await using var guest = P2pPeerSession.CreateForTests();
        var bridge = new HostMinecraftBridge(backend.EndPoint);
        var proxy = new GuestLoopbackProxy(guest);

        try
        {
            bridge.Attach(host);
            await ConnectAsync(host, guest, timeout.Token);
            await proxy.StartAsync(timeout.Token);

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, proxy.BoundPort, timeout.Token);
            var payload = RandomNumberGenerator.GetBytes(256 * 1024);
            var echoed = new byte[payload.Length];
            var stream = client.GetStream();

            await stream.WriteAsync(payload, timeout.Token);
            await stream.ReadExactlyAsync(echoed, timeout.Token);

            Assert.Equal(SHA256.HashData(payload), SHA256.HashData(echoed));
        }
        finally
        {
            await proxy.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await bridge.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task DisposingBackendEndsGuestReadWithinTwoSeconds()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var backend = new LoopbackEchoBackend();
        await using var host = P2pPeerSession.CreateForTests();
        await using var guest = P2pPeerSession.CreateForTests();
        var bridge = new HostMinecraftBridge(backend.EndPoint);
        var proxy = new GuestLoopbackProxy(guest);

        try
        {
            bridge.Attach(host);
            await ConnectAsync(host, guest, timeout.Token);
            await proxy.StartAsync(timeout.Token);

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, proxy.BoundPort, timeout.Token);
            await backend.WaitForConnectionAsync(timeout.Token);
            await backend.DisposeAsync();

            var read = client.GetStream().ReadAsync(new byte[1]).AsTask();
            try
            {
                Assert.Equal(0, await read.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            catch (IOException)
            {
                Assert.True(true);
            }
        }
        finally
        {
            await proxy.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await bridge.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task ConnectAsync(
        P2pPeerSession host,
        P2pPeerSession guest,
        CancellationToken cancellationToken)
    {
        var offer = await host.CreateOfferSdpAsync(cancellationToken);
        var answer = await guest.CreateAnswerSdpAsync(offer, cancellationToken);
        host.ApplyAnswerSdp(answer);
        await Task.WhenAll(
            host.WaitForConnectedAsync(TimeSpan.FromSeconds(10), cancellationToken),
            guest.WaitForConnectedAsync(TimeSpan.FromSeconds(10), cancellationToken));
    }

    private sealed class LoopbackEchoBackend : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();
        private readonly TaskCompletionSource _connected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _runTask;
        private TcpClient? _client;
        private int _disposed;

        public LoopbackEchoBackend()
        {
            _listener.Start();
            EndPoint = (IPEndPoint)_listener.LocalEndpoint;
            _runTask = RunAsync();
        }

        public IPEndPoint EndPoint { get; }

        public Task WaitForConnectionAsync(CancellationToken cancellationToken) =>
            _connected.Task.WaitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _listener.Stop();
            _stopping.Cancel();
            _client?.Close();
            await _runTask.WaitAsync(TimeSpan.FromSeconds(2));
            _stopping.Dispose();
        }

        private async Task RunAsync()
        {
            try
            {
                _client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                _connected.TrySetResult();
                var stream = _client.GetStream();
                var buffer = new byte[16 * 1024];
                while (true)
                {
                    var count = await stream.ReadAsync(buffer, _stopping.Token);
                    if (count == 0)
                    {
                        return;
                    }

                    await stream.WriteAsync(buffer.AsMemory(0, count), _stopping.Token);
                }
            }
            catch (Exception exception) when (
                Volatile.Read(ref _disposed) != 0 &&
                exception is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
            {
            }
        }
    }
}
