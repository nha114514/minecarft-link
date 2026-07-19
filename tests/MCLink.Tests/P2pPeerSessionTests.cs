using System.Reflection;
using MCLink.Core;
using SIPSorcery.Net;

namespace MCLink.Tests;

public sealed class P2pPeerSessionTests
{
    [Fact]
    public async Task TwoPeersExchangeOfferAnswerAndOpenReliableChannel()
    {
        await using var host = P2pPeerSession.CreateForTests();
        await using var guest = P2pPeerSession.CreateForTests();
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        guest.DataChannelReceived += channel => channel.onmessage += (_, type, data) =>
        {
            if (type == DataChannelPayloadProtocols.WebRTC_Binary)
            {
                received.TrySetResult(data);
            }
        };

        var offer = await host.CreateOfferSdpAsync();
        var answer = await guest.CreateAnswerSdpAsync(offer);
        host.ApplyAnswerSdp(answer);
        await Task.WhenAll(
            host.WaitForConnectedAsync(TimeSpan.FromSeconds(10)),
            guest.WaitForConnectedAsync(TimeSpan.FromSeconds(10)));
        var channel = await host.CreateDataChannelAsync("tcp-test", TimeSpan.FromSeconds(5));
        channel.send(new byte[] { 1, 2, 3 });

        Assert.Equal(new byte[] { 1, 2, 3 }, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TerminalStateAfterSuccessRejectsLaterWaitsAndChannelCreation()
    {
        await using var host = P2pPeerSession.CreateForTests();
        await using var guest = P2pPeerSession.CreateForTests();

        var offer = await host.CreateOfferSdpAsync();
        var answer = await guest.CreateAnswerSdpAsync(offer);
        host.ApplyAnswerSdp(answer);
        await Task.WhenAll(
            host.WaitForConnectedAsync(TimeSpan.FromSeconds(10)),
            guest.WaitForConnectedAsync(TimeSpan.FromSeconds(10)));
        await host.CreateDataChannelAsync("ready", TimeSpan.FromSeconds(5));

        GetPeerConnection(host).close();

        await Assert.ThrowsAsync<IOException>(() =>
            host.WaitForConnectedAsync(TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<IOException>(() =>
            host.CreateDataChannelAsync("after-close", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task DataChannelReceivedContinuesAfterSubscriberThrows()
    {
        await using var host = P2pPeerSession.CreateForTests();
        await using var guest = P2pPeerSession.CreateForTests();
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        guest.DataChannelReceived += _ => throw new InvalidOperationException("Subscriber failure.");
        guest.DataChannelReceived += channel => channel.onmessage += (_, type, data) =>
        {
            if (type == DataChannelPayloadProtocols.WebRTC_Binary)
            {
                received.TrySetResult(data);
            }
        };

        var offer = await host.CreateOfferSdpAsync();
        var answer = await guest.CreateAnswerSdpAsync(offer);
        host.ApplyAnswerSdp(answer);
        await Task.WhenAll(
            host.WaitForConnectedAsync(TimeSpan.FromSeconds(10)),
            guest.WaitForConnectedAsync(TimeSpan.FromSeconds(10)));
        var channel = await host.CreateDataChannelAsync("subscriber-test", TimeSpan.FromSeconds(5));
        channel.send(new byte[] { 4, 5, 6 });

        Assert.Equal(new byte[] { 4, 5, 6 }, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static RTCPeerConnection GetPeerConnection(P2pPeerSession session)
    {
        var field = typeof(P2pPeerSession).GetField(
            "_peerConnection",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<RTCPeerConnection>(field?.GetValue(session));
    }
}
