using System.IO.Compression;
using System.Text.Json;

namespace MCLink.Core;

public enum PairingCodeRole
{
    Offer,
    Answer,
}

public sealed record PairingCodePayload(
    Guid SessionId,
    PairingCodeRole Role,
    DateTimeOffset ExpiresAtUtc,
    string Sdp);

public static class PairingCodeCodec
{
    public const string Prefix = "MCL1.";
    public const int MaximumUncompressedBytes = 128 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Encode(PairingCodePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.SessionId == Guid.Empty
            || !Enum.IsDefined(payload.Role)
            || payload.ExpiresAtUtc == default
            || string.IsNullOrWhiteSpace(payload.Sdp))
        {
            throw new InvalidDataException("The pairing-code payload is invalid.");
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(
            new WirePayload(payload.SessionId, payload.Role, payload.ExpiresAtUtc, payload.Sdp),
            JsonOptions);
        if (json.Length >= MaximumUncompressedBytes)
        {
            throw new InvalidDataException("The pairing-code payload is too large.");
        }

        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(json);
        }

        return Prefix + EncodeBase64Url(compressed.ToArray());
    }

    public static PairingCodePayload Decode(
        string code,
        PairingCodeRole expectedRole,
        DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(code) || !code.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The pairing code has an unsupported version.");
        }

        byte[] compressed;
        try
        {
            compressed = DecodeBase64Url(code[Prefix.Length..]);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The pairing code is not valid Base64URL.", exception);
        }

        byte[] json;
        try
        {
            using var input = new MemoryStream(compressed, writable: false);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8 * 1024];
            int bytesRead;
            while ((bytesRead = brotli.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (output.Length + bytesRead > MaximumUncompressedBytes)
                {
                    throw new InvalidDataException("The pairing-code payload is too large.");
                }

                output.Write(buffer, 0, bytesRead);
            }

            json = output.ToArray();
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("The pairing code is not valid Brotli data.", exception);
        }
        catch (IOException exception)
        {
            throw new InvalidDataException("The pairing code is not valid Brotli data.", exception);
        }

        WirePayload? wirePayload;
        try
        {
            wirePayload = JsonSerializer.Deserialize<WirePayload>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The pairing-code payload is not valid JSON.", exception);
        }

        if (wirePayload?.SessionId is not { } sessionId
            || sessionId == Guid.Empty
            || wirePayload.Role is not { } role
            || !Enum.IsDefined(role)
            || wirePayload.ExpiresAtUtc is not { } expiresAtUtc
            || string.IsNullOrWhiteSpace(wirePayload.Sdp))
        {
            throw new InvalidDataException("The pairing-code payload is invalid.");
        }

        if (role != expectedRole)
        {
            throw new InvalidDataException("The pairing code has the wrong role.");
        }

        if (expiresAtUtc <= now)
        {
            throw new InvalidDataException("The pairing code has expired.");
        }

        return new PairingCodePayload(sessionId, role, expiresAtUtc, wirePayload.Sdp);
    }

    private static string EncodeBase64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static byte[] DecodeBase64Url(string value)
    {
        if (value.Length == 0 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new FormatException("The value is not valid Base64URL.");
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            0 => padded,
            2 => padded + "==",
            3 => padded + "=",
            _ => throw new FormatException("The value is not valid Base64URL."),
        };

        return Convert.FromBase64String(padded);
    }

    private sealed record WirePayload(
        Guid? SessionId,
        PairingCodeRole? Role,
        DateTimeOffset? ExpiresAtUtc,
        string? Sdp);
}
