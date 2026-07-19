using System.Diagnostics;
using SIPSorcery.Net;

namespace MCLink.Core;

public sealed class P2pPeerSession : IAsyncDisposable
{
    private static readonly string[] ProductionStunUrls =
    [
        "stun:stun.sipsorcery.com:3478",
        "stun:stun.cloudflare.com:3478",
    ];

    private readonly RTCPeerConnection _peerConnection;
    private readonly TaskCompletionSource _connected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _dataChannelTransportReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<IOException> _terminated =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RTCDataChannel? _controlChannel;
    private IOException? _terminalException;
    private int _disposed;

    private P2pPeerSession(IEnumerable<string> stunUrls)
    {
        _peerConnection = new RTCPeerConnection(new RTCConfiguration
        {
            // STUN 只用于找双方可直连的候选地址；真正的数据不会经过它。
            iceServers = stunUrls.Select(url => new RTCIceServer { urls = url }).ToList(),
            iceTransportPolicy = RTCIceTransportPolicy.all,
            X_GatherTimeoutMs = 15_000,
        });
        _peerConnection.onconnectionstatechange += HandleConnectionStateChanged;
        _peerConnection.ondatachannel += channel =>
        {
            _dataChannelTransportReady.TrySetResult();
            InvokeDataChannelReceived(channel);
        };
    }

    public event Action<RTCDataChannel>? DataChannelReceived;

    public static P2pPeerSession CreateProduction() => new(ProductionStunUrls);

    public static P2pPeerSession CreateForTests() => new([]);

    public async Task<string> CreateOfferSdpAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (_controlChannel is null)
        {
            var creationTask = _peerConnection.createDataChannel(
                "control",
                new RTCDataChannelInit { ordered = true });
            var controlChannel = await AwaitDataChannelCreationAsync(
                creationTask,
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            controlChannel.onopen += () => _dataChannelTransportReady.TrySetResult();
            _controlChannel = controlChannel;
            if (controlChannel.readyState == RTCDataChannelState.open)
            {
                _dataChannelTransportReady.TrySetResult();
            }
        }

        // 连接码只交换一次，所以等 ICE 收集结束后再把完整候选信息写进 SDP。
        var offer = _peerConnection.createOffer(new RTCOfferOptions
        {
            X_WaitForIceGatheringToComplete = true,
        });
        await _peerConnection.setLocalDescription(offer).WaitAsync(cancellationToken);

        return offer.sdp;
    }

    public async Task<string> CreateAnswerSdpAsync(
        string offerSdp,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ApplyRemoteDescription(offerSdp, RTCSdpType.offer);

        var answer = _peerConnection.createAnswer(new RTCAnswerOptions
        {
            X_WaitForIceGatheringToComplete = true,
        });
        await _peerConnection.setLocalDescription(answer).WaitAsync(cancellationToken);

        return answer.sdp;
    }

    public void ApplyAnswerSdp(string answerSdp)
    {
        ThrowIfDisposed();
        ApplyRemoteDescription(answerSdp, RTCSdpType.answer);
    }

    public async Task WaitForConnectedAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfTerminated();
        await _connected.Task.WaitAsync(timeout, cancellationToken);
        ThrowIfTerminated();
    }

    public async Task<RTCDataChannel> CreateDataChannelAsync(
        string label,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        return await CreateDataChannelCoreAsync(
            label,
            timeout,
            initializeChannel: null,
            cancellationToken);
    }

    internal async Task<RTCDataChannel> CreateDataChannelAsync(
        string label,
        TimeSpan timeout,
        Action<RTCDataChannel> initializeChannel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initializeChannel);
        return await CreateDataChannelCoreAsync(
            label,
            timeout,
            initializeChannel,
            cancellationToken);
    }

    private async Task<RTCDataChannel> CreateDataChannelCoreAsync(
        string label,
        TimeSpan timeout,
        Action<RTCDataChannel>? initializeChannel,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var started = Stopwatch.GetTimestamp();
        await WaitForDataChannelTransportAsync(Remaining(timeout, started), cancellationToken);

        RTCDataChannel channel;
        try
        {
            ThrowIfTerminated();
            var creationTask = _peerConnection.createDataChannel(
                label,
                new RTCDataChannelInit { ordered = true });
            channel = await AwaitDataChannelCreationAsync(
                creationTask,
                Remaining(timeout, started),
                cancellationToken);
            initializeChannel?.Invoke(channel);
            ThrowIfTerminated();
        }
        catch
        {
            ThrowIfTerminated();
            throw;
        }

        try
        {
            if (channel.readyState == RTCDataChannelState.open)
            {
                return channel;
            }

            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            channel.onopen += () => opened.TrySetResult();
            if (channel.readyState == RTCDataChannelState.open)
            {
                opened.TrySetResult();
            }

            var completed = await Task.WhenAny(opened.Task, _terminated.Task)
                .WaitAsync(Remaining(timeout, started), cancellationToken);
            if (completed == _terminated.Task)
            {
                throw await _terminated.Task;
            }

            await opened.Task;
            ThrowIfTerminated();
            return channel;
        }
        catch
        {
            try
            {
                channel.close();
            }
            catch (Exception)
            {
            }

            ThrowIfTerminated();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            SetTerminalException(RTCPeerConnectionState.closed);
            _peerConnection.close();
            _peerConnection.Dispose();
            DataChannelReceived = null;
        }

        return ValueTask.CompletedTask;
    }

    private void ApplyRemoteDescription(string sdp, RTCSdpType type)
    {
        ArgumentNullException.ThrowIfNull(sdp);
        // 工具只做直连。对方若带来中继候选，宁可报错，也不悄悄走第三方中转。
        if (sdp.Contains("typ relay", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Relay ICE candidates are not accepted.");
        }

        var result = _peerConnection.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = type,
            sdp = sdp,
        });
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidDataException($"The remote {type} SDP was rejected ({result}).");
        }
    }

    private void HandleConnectionStateChanged(RTCPeerConnectionState state)
    {
        if (state == RTCPeerConnectionState.connected)
        {
            _connected.TrySetResult();
        }
        else if (state is RTCPeerConnectionState.failed
                 or RTCPeerConnectionState.disconnected
                 or RTCPeerConnectionState.closed)
        {
            SetTerminalException(state);
        }
    }

    private void InvokeDataChannelReceived(RTCDataChannel channel)
    {
        var subscribers = DataChannelReceived;
        if (subscribers is null)
        {
            return;
        }

        foreach (Action<RTCDataChannel> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(channel);
            }
            catch (Exception)
            {
            }
        }
    }

    private static async Task<RTCDataChannel> AwaitDataChannelCreationAsync(
        Task<RTCDataChannel> creationTask,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await creationTask.WaitAsync(timeout, cancellationToken);
        }
        catch
        {
            _ = CloseAfterAbandonmentAsync(creationTask);
            throw;
        }
    }

    private static async Task CloseAfterAbandonmentAsync(Task<RTCDataChannel> creationTask)
    {
        try
        {
            var channel = await creationTask.ConfigureAwait(false);
            channel.close();
        }
        catch (Exception)
        {
        }
    }

    private async Task WaitForDataChannelTransportAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfTerminated();
        var completed = await Task.WhenAny(_dataChannelTransportReady.Task, _terminated.Task)
            .WaitAsync(timeout, cancellationToken);
        if (completed == _terminated.Task)
        {
            throw await _terminated.Task;
        }

        await _dataChannelTransportReady.Task;
        ThrowIfTerminated();
    }

    private static TimeSpan Remaining(TimeSpan timeout, long started)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return timeout;
        }

        var remaining = timeout - Stopwatch.GetElapsedTime(started);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void SetTerminalException(RTCPeerConnectionState state)
    {
        var exception = new IOException($"The peer connection entered the '{state}' state.");
        var existing = Interlocked.CompareExchange(ref _terminalException, exception, null);
        exception = existing ?? exception;
        _connected.TrySetException(exception);
        _terminated.TrySetResult(exception);
    }

    private void ThrowIfTerminated()
    {
        var exception = Volatile.Read(ref _terminalException);
        if (exception is not null)
        {
            throw exception;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);
}
