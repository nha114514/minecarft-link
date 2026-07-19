using System.Net;
using System.Net.Sockets;
using SIPSorcery.Net;

namespace MCLink.Core;

internal static class P2pBridgeConnectionLimit
{
    // 房主和加入者共用这 16 个名额，避免一次涌入太多 Minecraft 连接把进程拖住。
    internal static readonly SemaphoreSlim Shared = new(16, 16);
}

public sealed class GuestLoopbackProxy
{
    private readonly P2pPeerSession _session;
    // 只绑定回环地址。加入者的 Minecraft 客户端能访问，局域网里其他设备看不到这个端口。
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _sync = new();
    private readonly object _stopSync = new();
    private readonly HashSet<TcpClient> _clients = [];
    private readonly HashSet<RTCDataChannel> _channels = [];
    private readonly List<Task> _connectionTasks = [];
    private Task? _acceptTask;
    private Task? _stopTask;
    private int _boundPort;
    private int _started;
    private int _stopped;

    public GuestLoopbackProxy(P2pPeerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public int BoundPort => Volatile.Read(ref _boundPort);

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_stopped != 0, this);
            if (_started != 0)
            {
                return Task.CompletedTask;
            }

            _listener.Start();
            Volatile.Write(ref _boundPort, ((IPEndPoint)_listener.LocalEndpoint).Port);
            _started = 1;
            _acceptTask = AcceptLoopAsync();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_stopSync)
        {
            return _stopTask ??= StopCoreAsync();
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                lock (_sync)
                {
                    if (_stopped != 0)
                    {
                        Close(client);
                        continue;
                    }

                    _clients.Add(client);
                    _connectionTasks.Add(RelayClientAsync(client));
                }
            }
        }
        catch (Exception exception) when (
            Volatile.Read(ref _stopped) != 0 &&
            exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task RelayClientAsync(TcpClient client)
    {
        RTCDataChannel? channel = null;
        TcpDataChannelRelay.PreparedRelay? relay = null;
        var permitAcquired = false;
        try
        {
            await P2pBridgeConnectionLimit.Shared.WaitAsync(_stopping.Token);
            permitAcquired = true;
            // 每个 Minecraft TCP 连接单独占一个 DataChannel，互不抢字节流。
            channel = await _session.CreateDataChannelAsync(
                $"tcp-{Guid.NewGuid():N}",
                Timeout.InfiniteTimeSpan,
                createdChannel => relay = TcpDataChannelRelay.Prepare(createdChannel),
                _stopping.Token);

            lock (_sync)
            {
                if (_stopped != 0)
                {
                    Close(channel);
                    return;
                }

                _channels.Add(channel);
            }

            await relay!.SignalReadyAsync(_stopping.Token);
            await relay.WaitForPeerReadyAsync(_stopping.Token);
            await relay.RunAsync(client, _stopping.Token);
        }
        catch (Exception exception) when (
            exception is IOException
                or SocketException
                or ObjectDisposedException
                or OperationCanceledException
                or TimeoutException
                or InvalidDataException)
        {
        }
        finally
        {
            lock (_sync)
            {
                _clients.Remove(client);
                if (channel is not null)
                {
                    _channels.Remove(channel);
                }
            }

            Close(client);
            if (channel is not null)
            {
                Close(channel);
            }

            if (relay is not null)
            {
                await relay.DisposeAsync();
            }

            if (permitAcquired)
            {
                P2pBridgeConnectionLimit.Shared.Release();
            }
        }
    }

    private async Task StopCoreAsync()
    {
        Task? acceptTask;
        lock (_sync)
        {
            _stopped = 1;
            acceptTask = _acceptTask;
        }

        _listener.Stop();
        _stopping.Cancel();

        Task[] connectionTasks;
        lock (_sync)
        {
            foreach (var client in _clients)
            {
                Close(client);
            }

            foreach (var channel in _channels)
            {
                Close(channel);
            }

            connectionTasks = _connectionTasks.ToArray();
        }

        if (acceptTask is not null)
        {
            await acceptTask;
        }

        await Task.WhenAll(connectionTasks);
        _stopping.Dispose();
    }

    private static void Close(TcpClient client)
    {
        try
        {
            client.Close();
        }
        catch (Exception exception) when (
            exception is ObjectDisposedException or SocketException)
        {
        }
    }

    private static void Close(RTCDataChannel channel)
    {
        try
        {
            channel.close();
        }
        catch (Exception exception) when (
            exception is IOException
                or SocketException
                or ObjectDisposedException
                or OperationCanceledException
                or TimeoutException
                or InvalidDataException)
        {
        }
    }
}

public sealed class HostMinecraftBridge
{
    private readonly IPEndPoint _minecraftEndpoint;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _sync = new();
    private readonly object _stopSync = new();
    private readonly HashSet<TcpClient> _clients = [];
    private readonly HashSet<RTCDataChannel> _channels = [];
    private readonly List<Task> _connectionTasks = [];
    private P2pPeerSession? _session;
    private Task? _stopTask;
    private int _stopped;

    public HostMinecraftBridge(IPEndPoint minecraftEndpoint)
    {
        ArgumentNullException.ThrowIfNull(minecraftEndpoint);
        // 只允许转到本机 Minecraft，连接码不能被用来探测房主局域网里的其他服务。
        if (!IPAddress.IsLoopback(minecraftEndpoint.Address))
        {
            throw new ArgumentException(
                "The Minecraft endpoint must use a loopback address.",
                nameof(minecraftEndpoint));
        }

        _minecraftEndpoint = new IPEndPoint(
            minecraftEndpoint.Address,
            minecraftEndpoint.Port);
    }

    public void Attach(P2pPeerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_stopped != 0, this);
            if (_session is not null)
            {
                throw new InvalidOperationException("A peer session is already attached.");
            }

            _session = session;
            _session.DataChannelReceived += HandleDataChannelReceived;
        }
    }

    public Task StopAsync()
    {
        lock (_stopSync)
        {
            return _stopTask ??= StopCoreAsync();
        }
    }

    private void HandleDataChannelReceived(RTCDataChannel channel)
    {
        // 控制通道和 Minecraft 通道共用同一个 PeerConnection，靠标签把它们区分开。
        if (!channel.label.StartsWith("tcp-", StringComparison.Ordinal))
        {
            return;
        }

        TcpDataChannelRelay.PreparedRelay relay;
        try
        {
            relay = TcpDataChannelRelay.Prepare(channel);
        }
        catch
        {
            Close(channel);
            return;
        }

        lock (_sync)
        {
            if (_stopped != 0)
            {
                Close(channel);
                relay.DisposeAsync().GetAwaiter().GetResult();
                return;
            }

            _channels.Add(channel);
            _connectionTasks.Add(RelayChannelAsync(channel, relay));
        }
    }

    private async Task RelayChannelAsync(
        RTCDataChannel channel,
        TcpDataChannelRelay.PreparedRelay relay)
    {
        TcpClient? client = null;
        var permitAcquired = false;
        try
        {
            await P2pBridgeConnectionLimit.Shared.WaitAsync(_stopping.Token);
            permitAcquired = true;
            await WaitForOpenAsync(channel, _stopping.Token);
            await relay.WaitForPeerReadyAsync(_stopping.Token);

            client = new TcpClient(_minecraftEndpoint.AddressFamily);
            lock (_sync)
            {
                if (_stopped != 0)
                {
                    Close(client);
                    return;
                }

                _clients.Add(client);
            }

            await client.ConnectAsync(
                _minecraftEndpoint.Address,
                _minecraftEndpoint.Port,
                _stopping.Token);
            await relay.SignalReadyAsync(_stopping.Token);
            await relay.RunAsync(client, _stopping.Token);
        }
        catch (Exception)
        {
        }
        finally
        {
            lock (_sync)
            {
                _channels.Remove(channel);
                if (client is not null)
                {
                    _clients.Remove(client);
                }
            }

            if (client is not null)
            {
                Close(client);
            }

            Close(channel);
            await relay.DisposeAsync();
            if (permitAcquired)
            {
                P2pBridgeConnectionLimit.Shared.Release();
            }
        }
    }

    private async Task StopCoreAsync()
    {
        Task[] connectionTasks;
        lock (_sync)
        {
            _stopped = 1;
            if (_session is not null)
            {
                _session.DataChannelReceived -= HandleDataChannelReceived;
            }

            connectionTasks = _connectionTasks.ToArray();
        }

        _stopping.Cancel();

        lock (_sync)
        {
            foreach (var client in _clients)
            {
                Close(client);
            }

            foreach (var channel in _channels)
            {
                Close(channel);
            }
        }

        await Task.WhenAll(connectionTasks);
        _stopping.Dispose();
    }

    private static async Task WaitForOpenAsync(
        RTCDataChannel channel,
        CancellationToken cancellationToken)
    {
        if (channel.readyState == RTCDataChannelState.open)
        {
            return;
        }

        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action handleOpened = () => opened.TrySetResult();
        Action handleClosed = () => opened.TrySetException(
            new IOException("The data channel closed before opening."));
        channel.onopen += handleOpened;
        channel.onclose += handleClosed;
        try
        {
            if (channel.readyState == RTCDataChannelState.open)
            {
                opened.TrySetResult();
            }
            else if (channel.readyState == RTCDataChannelState.closed)
            {
                handleClosed();
            }

            await opened.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            channel.onopen -= handleOpened;
            channel.onclose -= handleClosed;
        }
    }

    private static void Close(TcpClient client)
    {
        try
        {
            client.Close();
        }
        catch (Exception exception) when (
            exception is ObjectDisposedException or SocketException)
        {
        }
    }

    private static void Close(RTCDataChannel channel)
    {
        try
        {
            channel.close();
        }
        catch (Exception)
        {
        }
    }
}
