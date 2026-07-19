using MCLink.Core;

namespace MCLink.Tests;

public sealed class PairingCodeTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RoundTripsOfferWithoutPlaintextSdp()
    {
        var payload = new PairingCodePayload(Guid.NewGuid(), PairingCodeRole.Offer, Now.AddMinutes(2), "v=0\r\na=fingerprint:sha-256 AA\r\n");
        var code = PairingCodeCodec.Encode(payload);
        var decoded = PairingCodeCodec.Decode(code, PairingCodeRole.Offer, Now);
        Assert.StartsWith("MCL1.", code);
        Assert.DoesNotContain("fingerprint", code, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void RejectsExpiredWrongRoleAndOversizedPayloads()
    {
        var expired = PairingCodeCodec.Encode(new(Guid.NewGuid(), PairingCodeRole.Offer, Now.AddSeconds(-1), "v=0"));
        Assert.Throws<InvalidDataException>(() => PairingCodeCodec.Decode(expired, PairingCodeRole.Offer, Now));
        var answer = PairingCodeCodec.Encode(new(Guid.NewGuid(), PairingCodeRole.Answer, Now.AddMinutes(2), "v=0"));
        Assert.Throws<InvalidDataException>(() => PairingCodeCodec.Decode(answer, PairingCodeRole.Offer, Now));
        Assert.Throws<InvalidDataException>(() => PairingCodeCodec.Encode(new(Guid.NewGuid(), PairingCodeRole.Offer, Now.AddMinutes(2), new string('x', 128 * 1024))));
    }

    [Fact]
    public void RejectsInvalidBrotliAsInvalidData()
    {
        byte[] invalidBrotli = [0xFF, 0xFF, 0xFF, 0xFF];
        var encoded = Convert.ToBase64String(invalidBrotli)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var code = PairingCodeCodec.Prefix + encoded;

        Assert.Throws<InvalidDataException>(() =>
            PairingCodeCodec.Decode(code, PairingCodeRole.Offer, Now));
    }
}
