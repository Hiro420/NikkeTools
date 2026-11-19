using System.Buffers.Binary;
using Shiftup.Patch.Runtime;
using UnityEngine;
using Xunit;

namespace NikkeTools.Tests.TestData;

/// <summary>
/// Builds byte-exact <c>store.cdb</c> / <c>store.cdb.idx</c> pairs using the
/// repository's own <see cref="Hash128"/> implementation to compute the
/// checksums.
///
/// That detail is the point: every file produced here carries a valid magic,
/// a valid version, a plausible entry count and a valid SpookyHashV2 checksum.
/// A reader that rejects them can only be rejecting on semantic grounds, and a
/// reader that accepts them cannot be relying on the checksum layer to catch
/// the problem.
/// </summary>
internal sealed class StoreFileBuilder : IDisposable
{
    private const uint DataMagic = 0x424C4243;   // "CBLB", little-endian
    private const uint IndexMagic = 0x58444943;  // "CIDX", little-endian
    internal const int DataHeaderSize = 256;

    private readonly string _directory;

    internal StoreFileBuilder()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nikke-tests-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_directory);
    }

    internal sealed record Entry(Hash128 Hash, long Offset, int Size);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* best effort: a locked temp dir must not fail the test */ }
    }

    private string PathFor(string name) => Path.Combine(_directory, name);

    private static Hash128 Checksum(params byte[][] parts)
    {
        Hash128 hash = default;
        foreach (byte[] part in parts)
        {
            unsafe
            {
                fixed (byte* p = part)
                    hash.Append(p, (ulong)part.Length);
            }
        }
        return hash;
    }

    /// <summary>
    /// Writes the 256-byte data file header followed by <paramref name="payload"/>.
    /// </summary>
    internal string WriteData(byte[] payload)
    {
        byte[] header = new byte[DataHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), DataMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 1);

        Hash128 sum = Checksum(header.AsSpan(0, 12).ToArray());
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(12, 8), sum.u64_0);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(20, 8), sum.u64_1);

        string path = PathFor(Guid.NewGuid().ToString("N") + ".cdb");
        using FileStream stream = File.Create(path);
        stream.Write(header);
        stream.Write(payload);
        return path;
    }

    /// <summary>
    /// Writes the index. <paramref name="truncateTrailer"/> drops the last four
    /// bytes of the checksum trailer, simulating an interrupted download.
    /// </summary>
    internal string WriteIndex(IReadOnlyList<Entry> entries, bool truncateTrailer = false)
    {
        byte[] header = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), IndexMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), entries.Count);

        List<byte[]> records = new(entries.Count);
        foreach (Entry entry in entries)
        {
            byte[] record = new byte[28];
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0, 8), entry.Hash.u64_0);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8, 8), entry.Hash.u64_1);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(16, 8), entry.Offset);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(24, 4), entry.Size);
            records.Add(record);
        }

        List<byte[]> parts = new(records.Count + 1) { header };
        parts.AddRange(records);

        Hash128 sums = Checksum(parts.ToArray());
        byte[] trailer = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(trailer.AsSpan(0, 8), sums.u64_0);
        BinaryPrimitives.WriteUInt64LittleEndian(trailer.AsSpan(8, 8), sums.u64_1);

        string path = PathFor(Guid.NewGuid().ToString("N") + ".cdb.idx");
        using FileStream stream = File.Create(path);
        stream.Write(header);
        foreach (byte[] record in records)
            stream.Write(record);
        stream.Write(truncateTrailer ? trailer.AsSpan(0, 12) : trailer);
        return path;
    }

    /// <summary>A deterministic, distinct hash per <paramref name="seed"/>.</summary>
    internal static Hash128 MakeHash(byte seed)
    {
        Span<byte> bytes = stackalloc byte[16];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(seed + i);

        return new Hash128(
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(0, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(8, 8)));
    }
}
