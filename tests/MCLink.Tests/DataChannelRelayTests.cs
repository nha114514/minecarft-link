using System.Security.Cryptography;
using MCLink.Core;

namespace MCLink.Tests;

public sealed class DataChannelRelayTests
{
    [Fact]
    public async Task RelayPreservesBytesAndPropagatesHalfClose()
    {
        var (leftChannel, rightChannel) = FakeBinaryChannel.CreatePair();
        await using var left = new DataChannelByteStream(leftChannel);
        await using var right = new DataChannelByteStream(rightChannel);
        var payload = RandomNumberGenerator.GetBytes(200_000);

        await left.SendAsync(payload);
        left.CompleteWrites();

        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await right.ReceiveAsync(buffer)) != 0)
        {
            output.Write(buffer, 0, count);
        }

        Assert.Equal(payload, output.ToArray());
    }

    [Fact]
    public async Task SendAsyncWaitsForBufferedAmountAndCanBeCancelled()
    {
        var (leftChannel, _) = FakeBinaryChannel.CreatePair();
        await using var left = new DataChannelByteStream(leftChannel);
        leftChannel.BufferedAmount = 1_048_577;

        var send = left.SendAsync(new byte[] { 42 });
        await Task.Delay(30);
        Assert.False(send.IsCompleted);

        leftChannel.BufferedAmount = 1_048_576;
        await send.WaitAsync(TimeSpan.FromSeconds(1));

        leftChannel.BufferedAmount = 1_048_577;
        using var cancellation = new CancellationTokenSource();
        var cancelledSend = left.SendAsync(new byte[] { 43 }, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledSend);
    }

    [Fact]
    public async Task CompleteWritesWaitsForBackpressuredSendBeforeSendingFin()
    {
        var (leftChannel, _) = FakeBinaryChannel.CreatePair();
        await using var left = new DataChannelByteStream(leftChannel);
        leftChannel.BufferedAmount = 1_048_577;
        var send = left.SendAsync(new byte[] { 42 });
        await leftChannel.WaitForBufferedAmountReadAsync();
        var completeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var complete = Task.Run(() =>
        {
            completeStarted.TrySetResult();
            left.CompleteWrites();
        });
        await completeStarted.Task;
        await Task.Delay(30);

        Assert.False(complete.IsCompleted);
        leftChannel.BufferedAmount = 1_048_576;
        await Task.WhenAll(send, complete).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Collection(
            leftChannel.SentFrames,
            data => Assert.Equal(new byte[] { 0, 42 }, data),
            fin => Assert.Equal(new byte[] { 1 }, fin));
    }

    [Fact]
    public async Task SendAsyncUsesMaximumSizedDataFramesAndFinOnlyOnce()
    {
        var (leftChannel, _) = FakeBinaryChannel.CreatePair();
        await using var left = new DataChannelByteStream(leftChannel);
        var payload = RandomNumberGenerator.GetBytes(16_385);

        await left.SendAsync(payload);
        left.CompleteWrites();
        left.CompleteWrites();

        var frames = leftChannel.SentFrames;
        Assert.Collection(
            frames,
            first =>
            {
                Assert.Equal(16_385, first.Length);
                Assert.Equal(0, first[0]);
                Assert.Equal(payload.AsSpan(0, 16_384).ToArray(), first.AsSpan(1).ToArray());
            },
            second =>
            {
                Assert.Equal(new byte[] { 0, payload[^1] }, second);
            },
            fin => Assert.Equal(new byte[] { 1 }, fin));
    }

    [Fact]
    public async Task ReceiveAsyncDrainsQueuedDataInOrderBeforeFin()
    {
        var (leftChannel, rightChannel) = FakeBinaryChannel.CreatePair();
        await using var right = new DataChannelByteStream(rightChannel);
        leftChannel.Send(new byte[] { 0, 1, 2, 3 });
        leftChannel.Send(new byte[] { 0, 4, 5 });
        leftChannel.Send(new byte[] { 1 });
        var buffer = new byte[2];
        using var output = new MemoryStream();

        int count;
        while ((count = await right.ReceiveAsync(buffer)) != 0)
        {
            output.Write(buffer, 0, count);
        }

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, output.ToArray());
    }

    [Fact]
    public async Task ReadyFrameIsConsumedWithoutBecomingTcpPayload()
    {
        var (leftChannel, rightChannel) = FakeBinaryChannel.CreatePair();
        await using var right = new DataChannelByteStream(rightChannel);
        leftChannel.Send(new byte[] { 2 });
        leftChannel.Send(new byte[] { 0, 42 });
        leftChannel.Send(new byte[] { 1 });
        var buffer = new byte[1];

        Assert.Equal(1, await right.ReceiveAsync(buffer));
        Assert.Equal(42, buffer[0]);
        Assert.Equal(0, await right.ReceiveAsync(buffer));
    }

    [Theory]
    [MemberData(nameof(InvalidFrames))]
    public async Task ReceiveAsyncRejectsInvalidFrames(byte[] frame)
    {
        var (leftChannel, rightChannel) = FakeBinaryChannel.CreatePair();
        await using var right = new DataChannelByteStream(rightChannel);

        leftChannel.Send(frame);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await right.ReceiveAsync(new byte[16 * 1024]));
    }

    [Fact]
    public async Task ChannelClosureWithoutFinFaultsPendingRead()
    {
        var (leftChannel, rightChannel) = FakeBinaryChannel.CreatePair();
        await using var right = new DataChannelByteStream(rightChannel);
        var receive = right.ReceiveAsync(new byte[1]);

        leftChannel.Close();

        await Assert.ThrowsAsync<IOException>(() => receive);
    }

    [Fact]
    public async Task ChannelClosedBeforeConstructionFaultsReadPromptly()
    {
        var (_, rightChannel) = FakeBinaryChannel.CreatePair();
        rightChannel.Close();
        await using var right = new DataChannelByteStream(rightChannel);

        await Assert.ThrowsAsync<IOException>(async () =>
            await right.ReceiveAsync(new byte[1]).WaitAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task ChannelConnectingDuringConstructionCanReceiveAfterOpening()
    {
        var (leftChannel, rightChannel) = FakeBinaryChannel.CreatePair();
        rightChannel.SetConnecting();
        await using var right = new DataChannelByteStream(rightChannel);
        rightChannel.Open();
        leftChannel.Send(new byte[] { 0, 42 });
        leftChannel.Send(new byte[] { 1 });
        var buffer = new byte[1];

        Assert.Equal(1, await right.ReceiveAsync(buffer));
        Assert.Equal(42, buffer[0]);
        Assert.Equal(0, await right.ReceiveAsync(buffer));
    }

    public static TheoryData<byte[]> InvalidFrames
    {
        get
        {
            var frames = new TheoryData<byte[]>
            {
                Array.Empty<byte>(),
                new byte[] { 0 },
                new byte[] { 1, 0 },
                OversizedDataFrame(),
            };
            return frames;
        }
    }

    private static byte[] OversizedDataFrame()
    {
        var frame = new byte[16_386];
        frame[0] = 0;
        return frame;
    }

    private sealed class FakeBinaryChannel : IBinaryDataChannel
    {
        private readonly object _sentFramesLock = new();
        private readonly List<byte[]> _sentFrames = [];
        private readonly TaskCompletionSource _bufferedAmountRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private FakeBinaryChannel? _peer;
        private long _bufferedAmount;
        private int _closed;
        private int _open = 1;

        public bool IsOpen => Volatile.Read(ref _open) != 0 && Volatile.Read(ref _closed) == 0;

        public bool IsClosed => Volatile.Read(ref _closed) != 0;

        public ulong BufferedAmount
        {
            get
            {
                _bufferedAmountRead.TrySetResult();
                return (ulong)Interlocked.Read(ref _bufferedAmount);
            }
            set => Interlocked.Exchange(ref _bufferedAmount, checked((long)value));
        }

        public IReadOnlyList<byte[]> SentFrames
        {
            get
            {
                lock (_sentFramesLock)
                {
                    return _sentFrames.Select(frame => frame.ToArray()).ToArray();
                }
            }
        }

        public event Action<byte[]>? MessageReceived;

        public event Action? Closed;

        public static (FakeBinaryChannel Left, FakeBinaryChannel Right) CreatePair()
        {
            var left = new FakeBinaryChannel();
            var right = new FakeBinaryChannel();
            left._peer = right;
            right._peer = left;
            return (left, right);
        }

        public Task WaitForBufferedAmountReadAsync() =>
            _bufferedAmountRead.Task.WaitAsync(TimeSpan.FromSeconds(1));

        public void SetConnecting() => Volatile.Write(ref _open, 0);

        public void Open() => Volatile.Write(ref _open, 1);

        public void Send(byte[] frame)
        {
            if (!IsOpen)
            {
                throw new IOException("The fake data channel is closed.");
            }

            var copy = frame.ToArray();
            lock (_sentFramesLock)
            {
                _sentFrames.Add(copy);
            }

            _peer!.MessageReceived?.Invoke(copy.ToArray());
        }

        public void Close()
        {
            CloseLocal();
            _peer!.CloseLocal();
        }

        private void CloseLocal()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                Closed?.Invoke();
            }
        }
    }
}
