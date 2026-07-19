using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MCLink.Core;

namespace MCLink.Tests;

public sealed class P2pCoordinatorTests
{
    [Fact]
    public async Task CoordinatorsPairAndRelayBytes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var backend = new LoopbackEchoBackend();
        await using var host = new P2pHostCoordinator(
            new IPEndPoint(IPAddress.Loopback, backend.Port),
            P2pPeerSession.CreateForTests,
            TimeProvider.System,
            TimeSpan.FromSeconds(10));
        await using var guest = new P2pGuestCoordinator(
            P2pPeerSession.CreateForTests,
            TimeProvider.System,
            TimeSpan.FromSeconds(10));

        var invite = await host.CreateInviteAsync(timeout.Token);
        var response = await guest.CreateResponseAsync(invite, timeout.Token);
        var accept = host.AcceptResponseAsync(response, timeout.Token);
        var localPort = await guest.WaitForLocalPortAsync(timeout.Token);
        await accept;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, localPort, timeout.Token);
        var payload = RandomNumberGenerator.GetBytes(64 * 1024);
        await client.GetStream().WriteAsync(payload, timeout.Token);
        var received = new byte[payload.Length];
        await client.GetStream().ReadExactlyAsync(received, timeout.Token);

        Assert.Equal(payload, received);
        Assert.Equal(1, host.ConnectedPeerCount);

        await Task.WhenAll(host.StopAsync(), guest.StopAsync())
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, host.ConnectedPeerCount);
    }

    [Fact]
    public async Task HostRejectsAResponseAfterItWasConsumed()
    {
        await using var backend = new LoopbackEchoBackend();
        await using var host = new P2pHostCoordinator(
            new IPEndPoint(IPAddress.Loopback, backend.Port),
            P2pPeerSession.CreateForTests,
            TimeProvider.System,
            TimeSpan.FromSeconds(10));
        await using var guest = new P2pGuestCoordinator(
            P2pPeerSession.CreateForTests,
            TimeProvider.System,
            TimeSpan.FromSeconds(10));
        var invite = await host.CreateInviteAsync();
        var response = await guest.CreateResponseAsync(invite);

        await Task.WhenAll(
            host.AcceptResponseAsync(response),
            guest.WaitForLocalPortAsync());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            host.AcceptResponseAsync(response));
    }

    [Fact]
    public async Task HostRejectsAResponseForAnotherPendingInvite()
    {
        await using var backend = new LoopbackEchoBackend();
        await using var firstHost = new P2pHostCoordinator(
            new IPEndPoint(IPAddress.Loopback, backend.Port),
            P2pPeerSession.CreateForTests);
        await using var secondHost = new P2pHostCoordinator(
            new IPEndPoint(IPAddress.Loopback, backend.Port),
            P2pPeerSession.CreateForTests);
        await using var guest = new P2pGuestCoordinator(P2pPeerSession.CreateForTests);
        _ = await firstHost.CreateInviteAsync();
        var secondInvite = await secondHost.CreateInviteAsync();
        var secondResponse = await guest.CreateResponseAsync(secondInvite);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            firstHost.AcceptResponseAsync(secondResponse));
    }

    [Fact]
    public async Task GuestTimeoutPropagatesAndStopIsIdempotent()
    {
        await using var unansweredHost = P2pPeerSession.CreateForTests();
        var offerSdp = await unansweredHost.CreateOfferSdpAsync();
        var invite = PairingCodeCodec.Encode(new PairingCodePayload(
            Guid.NewGuid(),
            PairingCodeRole.Offer,
            DateTimeOffset.UtcNow.AddMinutes(2),
            offerSdp));
        await using var guest = new P2pGuestCoordinator(
            P2pPeerSession.CreateForTests,
            TimeProvider.System,
            TimeSpan.FromMilliseconds(150));

        _ = await guest.CreateResponseAsync(invite);

        await Assert.ThrowsAsync<TimeoutException>(() => guest.WaitForLocalPortAsync());
        await Task.WhenAll(guest.StopAsync(), guest.StopAsync())
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class LoopbackEchoBackend : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _runTask;
        private TcpClient? _client;

        internal LoopbackEchoBackend()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _runTask = RunAsync();
        }

        internal int Port { get; }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _stopping.Cancel();
            _client?.Close();
            try
            {
                await _runTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (exception is OperationCanceledException
                                              or ObjectDisposedException
                                              or SocketException
                                              or TimeoutException)
            {
            }
            finally
            {
                _client?.Dispose();
                _stopping.Dispose();
            }
        }

        private async Task RunAsync()
        {
            try
            {
                _client = await _listener.AcceptTcpClientAsync(_stopping.Token);
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
            catch (Exception exception) when (_stopping.IsCancellationRequested
                                              && exception is OperationCanceledException
                                                  or ObjectDisposedException
                                                  or SocketException)
            {
            }
        }
    }
}
