using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Text;
using MCLink.Core;

namespace MCLink.Tests;

public sealed class MinecraftStatusProbeTests
{
    [Fact]
    public async Task ProbeAsyncReadsMinecraftStatusResponse()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeStatusAsync(listener, timeout.Token);

        try
        {
            var status = await new MinecraftStatusProbe().ProbeAsync(
                IPAddress.Loopback,
                port,
                TimeSpan.FromSeconds(2),
                timeout.Token);

            Assert.NotNull(status);
            Assert.Equal("1.21", status.VersionName);
            Assert.Equal(767, status.Protocol);
            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task ServeStatusAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = client.GetStream();
        await ReadPacketAsync(stream, cancellationToken);
        await ReadPacketAsync(stream, cancellationToken);

        const string json = "{\"version\":{\"name\":\"1.21\",\"protocol\":767}}";
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        using var body = new MemoryStream();
        WriteVarInt(body, 0);
        WriteVarInt(body, jsonBytes.Length);
        body.Write(jsonBytes);

        using var packet = new MemoryStream();
        WriteVarInt(packet, checked((int)body.Length));
        body.Position = 0;
        await body.CopyToAsync(packet, cancellationToken);
        await stream.WriteAsync(packet.ToArray(), cancellationToken);
    }

    private static async Task ReadPacketAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var length = await ReadVarIntAsync(stream, cancellationToken);
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken cancellationToken)
    {
        var result = 0;
        for (var index = 0; index < 5; index++)
        {
            var buffer = new byte[1];
            await stream.ReadExactlyAsync(buffer, cancellationToken);
            result |= (buffer[0] & 0x7F) << (7 * index);
            if ((buffer[0] & 0x80) == 0)
            {
                return result;
            }
        }

        throw new InvalidDataException("VarInt is too long.");
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        var remaining = (uint)value;
        do
        {
            var current = (byte)(remaining & 0x7F);
            remaining >>= 7;
            if (remaining != 0)
            {
                current |= 0x80;
            }

            stream.WriteByte(current);
        }
        while (remaining != 0);
    }
}
