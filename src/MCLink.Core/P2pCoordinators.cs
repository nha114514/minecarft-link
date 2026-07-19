using System.Net;

namespace MCLink.Core;

public sealed class P2pHostCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan DefaultConnectionTimeout = TimeSpan.FromSeconds(90);

    private readonly IPEndPoint _minecraftEndpoint;
    private readonly Func<P2pPeerSession> _sessionFactory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _connectionTimeout;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _sync = new();
    private readonly object _stopSync = new();
    private readonly HashSet<Guid> _consumedSessionIds = [];
    private readonly List<ConnectedPeer> _connectedPeers = [];
    private PendingInvite? _pendingInvite;
    private Task? _stopTask;
    private int _stopped;

    public P2pHostCoordinator(
        IPEndPoint minecraftEndpoint,
        Func<P2pPeerSession>? sessionFactory = null,
        TimeProvider? timeProvider = null,
        TimeSpan? connectionTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(minecraftEndpoint);
        if (!IPAddress.IsLoopback(minecraftEndpoint.Address))
        {
            throw new ArgumentException(
                "The Minecraft endpoint must use a loopback address.",
                nameof(minecraftEndpoint));
        }

        _minecraftEndpoint = new IPEndPoint(minecraftEndpoint.Address, minecraftEndpoint.Port);
        _sessionFactory = sessionFactory ?? P2pPeerSession.CreateProduction;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connectionTimeout = ValidateTimeout(connectionTimeout ?? DefaultConnectionTimeout);
    }

    public int ConnectedPeerCount
    {
        get
        {
            lock (_sync)
            {
                return _connectedPeers.Count;
            }
        }
    }

    public async Task<string> CreateInviteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfStopped();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _stopping.Token);
        await _operationGate.WaitAsync(linked.Token);
        try
        {
            ThrowIfStopped();
            PendingInvite? oldInvite;
            lock (_sync)
            {
                oldInvite = _pendingInvite;
                _pendingInvite = null;
            }

            if (oldInvite is not null)
            {
                await oldInvite.Session.DisposeAsync();
            }

            var session = _sessionFactory()
                ?? throw new InvalidOperationException("The peer-session factory returned null.");
            try
            {
                var sessionId = Guid.NewGuid();
                var expiresAt = _timeProvider.GetUtcNow().AddMinutes(2);
                var sdp = await session.CreateOfferSdpAsync(linked.Token);
                var code = PairingCodeCodec.Encode(new PairingCodePayload(
                    sessionId,
                    PairingCodeRole.Offer,
                    expiresAt,
                    sdp));

                lock (_sync)
                {
                    ThrowIfStopped();
                    _pendingInvite = new PendingInvite(sessionId, session);
                }

                return code;
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task AcceptResponseAsync(
        string responseCode,
        CancellationToken cancellationToken = default)
    {
        ThrowIfStopped();
        var response = PairingCodeCodec.Decode(
            responseCode,
            PairingCodeRole.Answer,
            _timeProvider.GetUtcNow());
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _stopping.Token);
        await _operationGate.WaitAsync(linked.Token);
        try
        {
            ThrowIfStopped();
            PendingInvite pending;
            lock (_sync)
            {
                if (_consumedSessionIds.Contains(response.SessionId))
                {
                    throw new InvalidDataException("The response code has already been used.");
                }

                pending = _pendingInvite
                    ?? throw new InvalidDataException("There is no pending invitation.");
                if (pending.SessionId != response.SessionId)
                {
                    throw new InvalidDataException("The response belongs to another invitation.");
                }

                _pendingInvite = null;
                _consumedSessionIds.Add(response.SessionId);
            }

            var bridge = new HostMinecraftBridge(_minecraftEndpoint);
            try
            {
                bridge.Attach(pending.Session);
                pending.Session.ApplyAnswerSdp(response.Sdp);
                await pending.Session.WaitForConnectedAsync(_connectionTimeout, linked.Token);
                lock (_sync)
                {
                    ThrowIfStopped();
                    _connectedPeers.Add(new ConnectedPeer(pending.Session, bridge));
                }
            }
            catch
            {
                await bridge.StopAsync();
                await pending.Session.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task StopAsync()
    {
        lock (_stopSync)
        {
            return _stopTask ??= StopCoreAsync();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task StopCoreAsync()
    {
        Interlocked.Exchange(ref _stopped, 1);
        _stopping.Cancel();
        await _operationGate.WaitAsync();
        PendingInvite? pending;
        ConnectedPeer[] connected;
        try
        {
            lock (_sync)
            {
                pending = _pendingInvite;
                _pendingInvite = null;
                connected = _connectedPeers.ToArray();
                _connectedPeers.Clear();
            }
        }
        finally
        {
            _operationGate.Release();
        }

        if (pending is not null)
        {
            await pending.Session.DisposeAsync();
        }

        foreach (var peer in connected)
        {
            await peer.Bridge.StopAsync();
            await peer.Session.DisposeAsync();
        }

        _stopping.Dispose();
    }

    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _stopped) != 0,
        this);

    private static TimeSpan ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return timeout;
    }

    private sealed record PendingInvite(Guid SessionId, P2pPeerSession Session);

    private sealed record ConnectedPeer(P2pPeerSession Session, HostMinecraftBridge Bridge);
}

public sealed class P2pGuestCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan DefaultConnectionTimeout = TimeSpan.FromSeconds(90);

    private readonly Func<P2pPeerSession> _sessionFactory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _connectionTimeout;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _sync = new();
    private readonly object _stopSync = new();
    private readonly TaskCompletionSource<int> _localPort = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private P2pPeerSession? _session;
    private GuestLoopbackProxy? _proxy;
    private Task? _connectionTask;
    private Task? _stopTask;
    private int _stopped;

    public P2pGuestCoordinator(
        Func<P2pPeerSession>? sessionFactory = null,
        TimeProvider? timeProvider = null,
        TimeSpan? connectionTimeout = null)
    {
        _sessionFactory = sessionFactory ?? P2pPeerSession.CreateProduction;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connectionTimeout = ValidateTimeout(connectionTimeout ?? DefaultConnectionTimeout);
    }

    public async Task<string> CreateResponseAsync(
        string inviteCode,
        CancellationToken cancellationToken = default)
    {
        ThrowIfStopped();
        var invite = PairingCodeCodec.Decode(
            inviteCode,
            PairingCodeRole.Offer,
            _timeProvider.GetUtcNow());
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _stopping.Token);
        await _operationGate.WaitAsync(linked.Token);
        try
        {
            ThrowIfStopped();
            lock (_sync)
            {
                if (_session is not null)
                {
                    throw new InvalidOperationException("A guest session is already active.");
                }
            }

            var session = _sessionFactory()
                ?? throw new InvalidOperationException("The peer-session factory returned null.");
            try
            {
                var answerSdp = await session.CreateAnswerSdpAsync(invite.Sdp, linked.Token);
                var responseCode = PairingCodeCodec.Encode(new PairingCodePayload(
                    invite.SessionId,
                    PairingCodeRole.Answer,
                    invite.ExpiresAtUtc,
                    answerSdp));

                lock (_sync)
                {
                    ThrowIfStopped();
                    _session = session;
                    _connectionTask = ConnectAsync(session);
                }

                return responseCode;
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task<int> WaitForLocalPortAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfStopped();
        lock (_sync)
        {
            if (_session is null)
            {
                throw new InvalidOperationException("CreateResponseAsync must be called first.");
            }
        }

        return _localPort.Task.WaitAsync(cancellationToken);
    }

    public Task StopAsync()
    {
        lock (_stopSync)
        {
            return _stopTask ??= StopCoreAsync();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task ConnectAsync(P2pPeerSession session)
    {
        try
        {
            await session.WaitForConnectedAsync(_connectionTimeout, _stopping.Token);
            var proxy = new GuestLoopbackProxy(session);
            await proxy.StartAsync(_stopping.Token);
            var stopProxy = false;
            lock (_sync)
            {
                if (_stopped != 0)
                {
                    stopProxy = true;
                }
                else
                {
                    _proxy = proxy;
                }
            }

            if (stopProxy)
            {
                await proxy.StopAsync();
                _localPort.TrySetCanceled();
                return;
            }

            _localPort.TrySetResult(proxy.BoundPort);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            _localPort.TrySetCanceled(_stopping.Token);
        }
        catch (Exception exception)
        {
            _localPort.TrySetException(exception);
        }
    }

    private async Task StopCoreAsync()
    {
        Interlocked.Exchange(ref _stopped, 1);
        _stopping.Cancel();
        await _operationGate.WaitAsync();
        _operationGate.Release();

        Task? connectionTask;
        lock (_sync)
        {
            connectionTask = _connectionTask;
        }

        if (connectionTask is not null)
        {
            await connectionTask;
        }

        GuestLoopbackProxy? proxy;
        P2pPeerSession? session;
        lock (_sync)
        {
            proxy = _proxy;
            _proxy = null;
            session = _session;
            _session = null;
        }

        if (proxy is not null)
        {
            await proxy.StopAsync();
        }

        if (session is not null)
        {
            await session.DisposeAsync();
        }

        _localPort.TrySetCanceled();
        _stopping.Dispose();
    }

    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _stopped) != 0,
        this);

    private static TimeSpan ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return timeout;
    }
}
