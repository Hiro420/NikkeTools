using System.Text;
using NikkeStaticData;
using Xunit;

namespace NikkeTools.Tests;

/// <summary>
/// Contract tests for the hand-written protobuf reader that consumes the
/// <c>get-static-data-pack-info-mpk</c> response body.
///
/// The input arrives over HTTP, so a truncated or hostile body is an expected
/// operational condition. Each test asserts that a malformed body produces the
/// reader's own <see cref="InvalidDataException"/> rather than a raw framework
/// exception or, worse, a silently wrong value.
/// </summary>
public sealed class ProtoReaderTests
{
    [Fact]
    public void WellFormedMessage_ParsesEveryField()
    {
        // Establishes that the message builder is correct, so a parse failure in
        // another test is attributable to the reader.
        ResStaticDataPackInfo info = ResStaticDataPackInfo.ParseFrom(BuildWellFormed());
        info.Validate();

        Assert.Equal("https://example.test/pack", info.Url);
        Assert.Equal(123456789L, info.Size);
        Assert.Equal(32, info.Sha256Sum.Length);
        Assert.Equal(32, info.Salt1.Length);
        Assert.Equal(32, info.Salt2.Length);
        Assert.Equal("1.2.3", info.Version);
    }

    [Fact]
    public void TruncatedBody_IsRejected()
    {
        byte[] full = BuildWellFormed();

        Assert.Throws<InvalidDataException>(
            () => ResStaticDataPackInfo.ParseFrom(full.AsSpan(0, full.Length - 20).ToArray()));
    }

    [Fact]
    public void AbsurdLengthPrefix_IsRejectedWithoutAllocating()
    {
        // A six-byte body that claims a ~2 GiB field. Without a length check this
        // reaches the allocator and throws OutOfMemoryException.
        byte[] message = { 0x1A, 0xFF, 0xFF, 0xFF, 0xFF, 0x07 };

        Assert.Throws<InvalidDataException>(() => ResStaticDataPackInfo.ParseFrom(message));
    }

    [Fact]
    public void LengthPrefixOverflowingInt_IsRejected()
    {
        // 0xFFFFFFFF casts to -1 and would reach `new byte[-1]`.
        byte[] message = { 0x1A, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F };

        Assert.Throws<InvalidDataException>(() => ResStaticDataPackInfo.ParseFrom(message));
    }

    [Fact]
    public void OverlongVarint_IsRejected()
    {
        // Eleven bytes for a 64-bit field. Without a length cap the shift count
        // wraps to a 5-bit mask and the value comes back wrong but silently.
        //
        // The padding matters: without it the buffer ends inside the varint and the
        // bounds check fires first, so the test would pass for the wrong reason and
        // would keep passing even if the length cap were removed.
        var bytes = new List<byte> { (0x02 << 3) | 0x00 };
        for (int i = 0; i < 11; i++)
            bytes.Add(0xFF);
        bytes.Add(0x01);
        bytes.AddRange(new byte[16]);

        Assert.Throws<InvalidDataException>(() => ResStaticDataPackInfo.ParseFrom(bytes.ToArray()));
    }

    [Fact]
    public void VarintTerminatingButOverflowing32Bit_IsRejected()
    {
        // Five bytes, so the length cap is satisfied -- but the fifth byte carries
        // bits above 31, so the value cannot fit a uint. Without the overflow check
        // the bit pattern is silently truncated and parsing continues on a bogus
        // tag. This is the case the length cap alone cannot catch.
        var bytes = new List<byte> { 0xFF, 0xFF, 0xFF, 0xFF, 0x7F };
        bytes.AddRange(new byte[16]);

        Assert.Throws<InvalidDataException>(() => ResStaticDataPackInfo.ParseFrom(bytes.ToArray()));
    }

    [Fact]
    public void VarintTerminatingButOverflowing64Bit_IsRejected()
    {
        // Ten bytes, so the length cap is satisfied -- but the tenth byte carries
        // bits above 63. Without the overflow check the value comes back wrong with
        // no signal at all, which is the failure mode this whole test file is about.
        var bytes = new List<byte> { (0x02 << 3) | 0x00 };
        for (int i = 0; i < 9; i++)
            bytes.Add(0xFF);
        bytes.Add(0x7F);
        bytes.AddRange(new byte[16]);

        Assert.Throws<InvalidDataException>(() => ResStaticDataPackInfo.ParseFrom(bytes.ToArray()));
    }

    [Fact]
    public void OverlongTagVarint_IsRejected()
    {
        // The 32-bit path: a tag varint longer than five bytes, again with padding
        // so that running out of buffer cannot be what raises the error.
        var bytes = new List<byte>();
        for (int i = 0; i < 6; i++)
            bytes.Add(0xFF);
        bytes.Add(0x7F);
        bytes.AddRange(new byte[16]);

        Assert.Throws<InvalidDataException>(() => ResStaticDataPackInfo.ParseFrom(bytes.ToArray()));
    }

    [Fact]
    public void LengthDelimitedStringShortOfTheEnd_IsRejected()
    {
        // field 1 (string) declaring 10 bytes with only 3 present.
        byte[] message = { 0x0A, 0x0A, (byte)'a', (byte)'b', (byte)'c' };

        Assert.Throws<InvalidDataException>(() => ResStaticDataPackInfo.ParseFrom(message));
    }

    [Fact]
    public void MissingRequiredFields_FailValidation()
    {
        // Only the size field is present. Without Validate(), the salts decode to
        // empty arrays, Rfc2898DeriveBytes derives a key from an empty salt, and
        // the failure surfaces much later as an unrelated "invalid zip" error.
        byte[] message = { 0x10, 0x80, 0x80, 0x80, 0x01 };

        ResStaticDataPackInfo info = ResStaticDataPackInfo.ParseFrom(message);

        Assert.Throws<InvalidDataException>(() => info.Validate());
    }

    private static byte[] BuildWellFormed()
    {
        using var stream = new MemoryStream();

        void Varint(ulong value)
        {
            while (value >= 0x80)
            {
                stream.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }
            stream.WriteByte((byte)value);
        }

        void Tag(int field, int wireType) => Varint((ulong)((field << 3) | wireType));

        void LengthDelimited(int field, byte[] payload)
        {
            Tag(field, 2);
            Varint((ulong)payload.Length);
            stream.Write(payload);
        }

        LengthDelimited(1, Encoding.UTF8.GetBytes("https://example.test/pack"));
        Tag(2, 0);
        Varint(123456789);
        LengthDelimited(3, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        LengthDelimited(4, Enumerable.Range(0, 32).Select(i => (byte)(i + 32)).ToArray());
        LengthDelimited(5, Enumerable.Range(0, 32).Select(i => (byte)(i + 64)).ToArray());
        LengthDelimited(6, Encoding.UTF8.GetBytes("1.2.3"));

        return stream.ToArray();
    }
}
