using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MCLink.Core;

public sealed record MinecraftStatus(string VersionName, int Protocol);

public sealed class MinecraftStatusProbe
{
    private const int MaximumPacketLength = 1024 * 1024;

    public async Task<MinecraftStatus?> ProbeAsync(
        IPAddress address,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, port, deadline.Token);
            var stream = client.GetStream();

            await stream.WriteAsync(BuildHandshakePacket(address, port), deadline.Token);
            await stream.WriteAsync(new byte[] { 1, 0 }, deadline.Token);

            var packetLength = await ReadVarIntAsync(stream, deadline.Token);
            if (packetLength is <= 0 or > MaximumPacketLength)
            {
                throw new InvalidDataException("Invalid Minecraft status packet length.");
            }

            var packet = new byte[packetLength];
            await stream.ReadExactlyAsync(packet, deadline.Token);
            var offset = 0;
            if (ReadVarInt(packet, ref offset) != 0)
            {
                throw new InvalidDataException("Unexpected Minecraft status packet id.");
            }

            var jsonLength = ReadVarInt(packet, ref offset);
            if (jsonLength < 0 || jsonLength > packet.Length - offset)
            {
                throw new InvalidDataException("Invalid Minecraft status JSON length.");
            }

            using var document = JsonDocument.Parse(packet.AsMemory(offset, jsonLength));
            var version = document.RootElement.GetProperty("version");
            return new MinecraftStatus(
                version.GetProperty("name").GetString() ?? string.Empty,
                version.GetProperty("protocol").GetInt32());
        }
        catch (Exception exception) when (exception is
            SocketException or
            IOException or
            JsonException or
            InvalidDataException or
            OperationCanceledException)
        {
            return null;
        }
    }

    private static byte[] BuildHandshakePacket(IPAddress address, int port)
    {
        var host = Encoding.UTF8.GetBytes(address.ToString());
        using var body = new MemoryStream();
        WriteVarInt(body, 0);
        WriteVarInt(body, -1);
        WriteVarInt(body, host.Length);
        body.Write(host);

        Span<byte> portBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBytes, checked((ushort)port));
        body.Write(portBytes);
        WriteVarInt(body, 1);

        using var packet = new MemoryStream();
        WriteVarInt(packet, checked((int)body.Length));
        body.Position = 0;
        body.CopyTo(packet);
        return packet.ToArray();
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken cancellationToken)
    {
        var value = 0;
        var buffer = new byte[1];
        for (var index = 0; index < 5; index++)
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken);
            value |= (buffer[0] & 0x7F) << (7 * index);
            if ((buffer[0] & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("VarInt is too long.");
    }

    private static int ReadVarInt(ReadOnlySpan<byte> data, ref int offset)
    {
        var value = 0;
        for (var index = 0; index < 5; index++)
        {
            if (offset >= data.Length)
            {
                throw new InvalidDataException("Incomplete VarInt.");
            }

            var current = data[offset++];
            value |= (current & 0x7F) << (7 * index);
            if ((current & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("VarInt is too long.");
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        var remaining = unchecked((uint)value);
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
