using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using SIPSorcery.Net;

namespace MCLink.Core;

public interface IBinaryDataChannel
{
    bool IsOpen { get; }
    bool IsClosed { get; }
    ulong BufferedAmount { get; }
    event Action<byte[]>? MessageReceived;
    event Action? Closed;
    void Send(byte[] frame);
    void Close();
}

public sealed class DataChannelByteStream : IAsyncDisposable
{
    private const int MaximumPayloadSize = 16 * 1024;
    // WebRTC 自己也有发送缓冲区。积压到这里就先等一等，避免慢网络把内存一路顶高。
    private const ulong MaximumBufferedAmount = 1_048_576;
    // 帧首字节：0 是数据，1 表示本方向的 TCP 已读完，2 表示本地 TCP 已准备好。
    private static readonly byte[] FinFrame = [1];
    private static readonly byte[] ReadyFrame = [2];

    private readonly IBinaryDataChannel _channel;
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _receiveGate = new(1, 1);
    private readonly TaskCompletionSource _peerReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[]? _currentPayload;
    private int _currentOffset;
    private int _writesCompleted;
    private int _disposed;

    public DataChannelByteStream(IBinaryDataChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        _channel.MessageReceived += HandleMessageReceived;
        _channel.Closed += HandleClosed;
        if (_channel.IsClosed)
        {
            HandleClosed();
        }
    }

    public async Task SendAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (data.IsEmpty)
        {
            return;
        }

        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            var offset = 0;
            while (offset < data.Length)
            {
                ThrowIfWritesCompleted();
                while (_channel.BufferedAmount > MaximumBufferedAmount)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfChannelClosed();
                    await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
                }

                ThrowIfChannelClosed();
                var count = Math.Min(MaximumPayloadSize, data.Length - offset);
                var frame = new byte[count + 1];
                data.Span.Slice(offset, count).CopyTo(frame.AsSpan(1));
                _channel.Send(frame);
                offset += count;
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task<int> ReceiveAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (destination.IsEmpty)
        {
            return 0;
        }

        await _receiveGate.WaitAsync(cancellationToken);
        try
        {
            if (_currentPayload is null)
            {
                try
                {
                    _currentPayload = await _incoming.Reader.ReadAsync(cancellationToken);
                    _currentOffset = 0;
                }
                catch (ChannelClosedException exception) when (exception.InnerException is not null)
                {
                    ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                    throw;
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }
            }

            var count = Math.Min(destination.Length, _currentPayload.Length - _currentOffset);
            _currentPayload.AsMemory(_currentOffset, count).CopyTo(destination);
            _currentOffset += count;
            if (_currentOffset == _currentPayload.Length)
            {
                _currentPayload = null;
                _currentOffset = 0;
            }

            return count;
        }
        finally
        {
            _receiveGate.Release();
        }
    }

    public void CompleteWrites()
    {
        ThrowIfDisposed();
        _sendGate.Wait();
        try
        {
            if (Interlocked.Exchange(ref _writesCompleted, 1) == 0)
            {
                // TCP 的 EOF 要跨过数据通道传给另一边；这不是关闭整个 P2P 连接。
                ThrowIfChannelClosed();
                _channel.Send(FinFrame.ToArray());
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    internal async Task SignalReadyAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfChannelClosed();
            _channel.Send(ReadyFrame.ToArray());
        }
        finally
        {
            _sendGate.Release();
        }
    }

    internal Task WaitForPeerReadyAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _peerReady.Task.WaitAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _channel.MessageReceived -= HandleMessageReceived;
            _channel.Closed -= HandleClosed;
            _incoming.Writer.TryComplete(new ObjectDisposedException(nameof(DataChannelByteStream)));
            _peerReady.TrySetResult();
            _channel.Close();
            _sendGate.Dispose();
            _receiveGate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void HandleMessageReceived(byte[] frame)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (frame.Length == 1 && frame[0] == 1)
        {
            _incoming.Writer.TryComplete();
            return;
        }

        if (frame.Length == 1 && frame[0] == 2)
        {
            _peerReady.TrySetResult();
            return;
        }

        if (frame.Length < 2 || frame.Length > MaximumPayloadSize + 1 || frame[0] != 0)
        {
            _incoming.Writer.TryComplete(new InvalidDataException("Invalid data channel relay frame."));
            _channel.Close();
            return;
        }

        if (!_incoming.Writer.TryWrite(frame.AsSpan(1).ToArray()))
        {
            _channel.Close();
        }
    }

    private void HandleClosed()
    {
        var exception = new IOException("The data channel closed before FIN.");
        _incoming.Writer.TryComplete(exception);
        _peerReady.TrySetResult();
    }

    private void ThrowIfChannelClosed()
    {
        if (!_channel.IsOpen)
        {
            throw new IOException("The data channel is closed.");
        }
    }

    private void ThrowIfWritesCompleted()
    {
        if (Volatile.Read(ref _writesCompleted) != 0)
        {
            throw new InvalidOperationException("Writes have already been completed.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);
}

public static class TcpDataChannelRelay
{
    private const int BufferSize = 16 * 1024;

    public static async Task RunAsync(
        TcpClient tcp,
        RTCDataChannel dataChannel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tcp);
        ArgumentNullException.ThrowIfNull(dataChannel);

        await using var prepared = Prepare(dataChannel);
        await prepared.RunAsync(tcp, cancellationToken);
    }

    internal static PreparedRelay Prepare(RTCDataChannel dataChannel)
    {
        ArgumentNullException.ThrowIfNull(dataChannel);
        return new PreparedRelay(dataChannel);
    }

    private static async Task RunPreparedAsync(
        TcpClient tcp,
        RtcBinaryDataChannel binaryChannel,
        DataChannelByteStream byteStream,
        CancellationToken cancellationToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tcpStream = tcp.GetStream();
        // 两个方向同时搬运字节。任一方向异常时会把另一边也停掉，避免留下半死连接。
        var tcpToDataChannel = PumpTcpToDataChannelAsync(tcpStream, byteStream, stopping.Token);
        var dataChannelToTcp = PumpDataChannelToTcpAsync(
            byteStream,
            tcpStream,
            tcp.Client,
            stopping.Token);

        try
        {
            var first = await Task.WhenAny(tcpToDataChannel, dataChannelToTcp);
            if (first.IsFaulted || first.IsCanceled)
            {
                stopping.Cancel();
                Close(tcp, binaryChannel);
            }

            await Task.WhenAll(tcpToDataChannel, dataChannelToTcp);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                          or SocketException
                                          or ObjectDisposedException
                                          or OperationCanceledException)
        {
        }
        finally
        {
            stopping.Cancel();
            Close(tcp, binaryChannel);
        }
    }

    internal sealed class PreparedRelay : IAsyncDisposable
    {
        private readonly RtcBinaryDataChannel _binaryChannel;
        private readonly DataChannelByteStream _byteStream;
        private int _started;

        internal PreparedRelay(RTCDataChannel dataChannel)
        {
            _binaryChannel = new RtcBinaryDataChannel(dataChannel);
            _byteStream = new DataChannelByteStream(_binaryChannel);
        }

        internal Task RunAsync(
            TcpClient tcp,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(tcp);
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException("The prepared relay has already been started.");
            }

            return RunPreparedAsync(tcp, _binaryChannel, _byteStream, cancellationToken);
        }

        internal Task SignalReadyAsync(CancellationToken cancellationToken = default) =>
            _byteStream.SignalReadyAsync(cancellationToken);

        internal Task WaitForPeerReadyAsync(CancellationToken cancellationToken = default) =>
            _byteStream.WaitForPeerReadyAsync(cancellationToken);

        public ValueTask DisposeAsync() => _byteStream.DisposeAsync();
    }

    private static async Task PumpTcpToDataChannelAsync(
        NetworkStream tcpStream,
        DataChannelByteStream dataChannelStream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        while (true)
        {
            var count = await tcpStream.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                // 本地 TCP 半关闭后，远端仍可能有数据要回传。
                dataChannelStream.CompleteWrites();
                return;
            }

            await dataChannelStream.SendAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }

    private static async Task PumpDataChannelToTcpAsync(
        DataChannelByteStream dataChannelStream,
        NetworkStream tcpStream,
        Socket tcpSocket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        while (true)
        {
            var count = await dataChannelStream.ReceiveAsync(buffer, cancellationToken);
            if (count == 0)
            {
                // 收到远端 FIN，只关闭本地 socket 的发送方向，让已在路上的数据读完。
                ShutdownSend(tcpSocket);
                return;
            }

            await tcpStream.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }

    private static void Close(TcpClient tcp, IBinaryDataChannel dataChannel)
    {
        try
        {
            tcp.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            dataChannel.Close();
        }
        catch (Exception exception) when (exception is IOException
                                          or SocketException
                                          or ObjectDisposedException)
        {
        }
    }

    private static void ShutdownSend(Socket socket)
    {
        try
        {
            socket.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class RtcBinaryDataChannel : IBinaryDataChannel
    {
        private readonly RTCDataChannel _channel;
        private int _closed;

        public RtcBinaryDataChannel(RTCDataChannel channel)
        {
            _channel = channel;
            _channel.onmessage += HandleMessage;
            _channel.onclose += HandleClosed;
        }

        public bool IsOpen => _channel.readyState == RTCDataChannelState.open;

        public bool IsClosed => _channel.readyState == RTCDataChannelState.closed;

        public ulong BufferedAmount => _channel.bufferedAmount;

        public event Action<byte[]>? MessageReceived;

        public event Action? Closed;

        public void Send(byte[] frame)
        {
            _channel.send(frame, 0, frame.Length);
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                _channel.onmessage -= HandleMessage;
                _channel.onclose -= HandleClosed;
                _channel.close();
            }
        }

        private void HandleMessage(
            RTCDataChannel _,
            DataChannelPayloadProtocols protocol,
            byte[] data)
        {
            if (protocol is DataChannelPayloadProtocols.WebRTC_Binary
                or DataChannelPayloadProtocols.WebRTC_Binary_Empty)
            {
                MessageReceived?.Invoke(data);
            }
            else
            {
                MessageReceived?.Invoke([]);
            }
        }

        private void HandleClosed()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                _channel.onmessage -= HandleMessage;
                _channel.onclose -= HandleClosed;
                Closed?.Invoke();
            }
        }
    }
}
