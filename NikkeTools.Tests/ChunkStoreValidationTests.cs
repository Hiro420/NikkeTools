using Shiftup.Patch.Runtime;
using UnityEngine;
using Xunit;
using NikkeTools.Tests.TestData;
using static NikkeTools.Tests.TestData.StoreFileBuilder;

namespace NikkeTools.Tests;

/// <summary>
/// Contract tests for the CatalogV2 chunk store reader.
///
/// Every crafted file has a valid magic, a valid version, a plausible entry
/// count and a valid SpookyHashV2 checksum, so the integrity gate cannot be
/// what decides any of these cases.
///
/// Contracts are stated per test. Two outcomes are treated as failure, for
/// different reasons: <b>accepting</b> the input (silently wrong data reaches
/// the caller) and <b>leaking a raw framework exception</b> (the caller cannot
/// distinguish a corrupt file from a bug in this tool). Only the module's own
/// <see cref="ChunkStoreInvalidException"/> counts as a deliberate refusal.
/// </summary>
public sealed class ChunkStoreValidationTests
{
    [Fact]
    public void WellFormedStore_RoundTripsThePayload()
    {
        // Establishes that the builder and the reader agree on the format, so a
        // rejection in any other test is meaningful.
        using var files = new StoreFileBuilder();
        byte[] payload = Enumerable.Range(0, 64).Select(i => (byte)(i % 251)).ToArray();
        string dataPath = files.WriteData(payload);
        Hash128 hash = MakeHash(1);
        string indexPath = files.WriteIndex(new[] { new Entry(hash, DataHeaderSize, payload.Length) });

        using ChunkStore store = ChunkStore.Open(dataPath, indexPath);

        Assert.Single(store.GetAllHashes());
        byte[] destination = new byte[payload.Length];
        Assert.True(store.TryRead(hash, destination, out int bytesRead));
        Assert.Equal(payload.Length, bytesRead);
        Assert.Equal(payload, destination);
    }

    [Fact]
    public void IndexEntryOverlappingTheFileHeader_IsRejected()
    {
        // offset=0 is inside the 256-byte data header. Serving those bytes would
        // hand the caller the file magic instead of chunk data.
        using var files = new StoreFileBuilder();
        string dataPath = files.WriteData(new byte[64]);
        Hash128 hash = MakeHash(2);
        string indexPath = files.WriteIndex(new[] { new Entry(hash, 0, DataHeaderSize) });

        Assert.Throws<ChunkStoreInvalidException>(() => ChunkStore.Open(dataPath, indexPath));
    }

    [Fact]
    public void IndexEntryWithNegativeSize_IsRejectedWithTypedError()
    {
        using var files = new StoreFileBuilder();
        string dataPath = files.WriteData(new byte[64]);
        string indexPath = files.WriteIndex(new[] { new Entry(MakeHash(3), DataHeaderSize, -64) });

        Assert.Throws<ChunkStoreInvalidException>(() => ChunkStore.Open(dataPath, indexPath));
    }

    [Fact]
    public void IndexEntryExtendingPastEndOfFile_IsRejectedAtOpen()
    {
        // Discovering this at read time costs the caller a raw IOException from
        // the FileStream, so the check belongs to Open.
        using var files = new StoreFileBuilder();
        string dataPath = files.WriteData(new byte[64]);
        string indexPath = files.WriteIndex(new[] { new Entry(MakeHash(4), long.MaxValue - 8, 64) });

        Assert.Throws<ChunkStoreInvalidException>(() => ChunkStore.Open(dataPath, indexPath));
    }

    [Fact]
    public void IndexDeclaringDuplicateHashes_DoesNotSilentlyCollapse()
    {
        // Two records sharing one hash: the reader must not report one entry for
        // a file that declares two.
        using var files = new StoreFileBuilder();
        string dataPath = files.WriteData(new byte[128]);
        Hash128 hash = MakeHash(5);
        string indexPath = files.WriteIndex(new[]
        {
            new Entry(hash, DataHeaderSize, 64),
            new Entry(hash, DataHeaderSize + 64, 64),
        });

        ChunkStoreInvalidException? rejected = null;
        try
        {
            using ChunkStore store = ChunkStore.Open(dataPath, indexPath);
            Assert.Equal(2, store.GetAllHashes().Count);
        }
        catch (ChunkStoreInvalidException ex)
        {
            rejected = ex; // refusing duplicates outright is also a valid contract
        }

        if (rejected is not null)
            Assert.Contains("duplicate", rejected.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetBytesFor_ReportsHowManyHashesWereMissing()
    {
        // Returning a total that silently omits unknown hashes makes "all
        // present" and "some missing" indistinguishable to the caller.
        using var files = new StoreFileBuilder();
        string dataPath = files.WriteData(new byte[64]);
        Hash128 known = MakeHash(6);
        string indexPath = files.WriteIndex(new[] { new Entry(known, DataHeaderSize, 64) });

        using ChunkStore store = ChunkStore.Open(dataPath, indexPath);

        long total = store.GetBytesFor(new[] { known, MakeHash(200), MakeHash(201) }, out int missing);

        Assert.Equal(64, total);
        Assert.Equal(2, missing);
    }

    [Fact]
    public void TruncatedIndex_IsRejectedWithTypedError()
    {
        using var files = new StoreFileBuilder();
        string dataPath = files.WriteData(new byte[64]);
        string indexPath = files.WriteIndex(
            new[] { new Entry(MakeHash(7), DataHeaderSize, 64) },
            truncateTrailer: true);

        Assert.Throws<ChunkStoreInvalidException>(() => ChunkStore.Open(dataPath, indexPath));
    }

    [Fact]
    public void ChunkCacheWithZeroCapacity_DoesNotThrow()
    {
        // Capacity zero is reachable from ordinary use: Program builds the cache
        // from the store's entry count, and an empty store yields zero.
        var cache = new ChunkCache(0);

        cache.Add(MakeHash(8), new byte[16]);
    }

    [Fact]
    public void ChunkCacheWithNegativeCapacity_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkCache(-1));
    }

    [Fact]
    public void NegativeStreamPosition_IsRejected()
    {
        // Accepting a negative position shifts the next Read by |position| bytes
        // instead of failing.
        using var files = new StoreFileBuilder();
        byte[] payload = Enumerable.Repeat((byte)0xAA, 64).ToArray();
        string dataPath = files.WriteData(payload);
        Hash128 hash = MakeHash(9);
        string indexPath = files.WriteIndex(new[] { new Entry(hash, DataHeaderSize, payload.Length) });

        using ChunkStore store = ChunkStore.Open(dataPath, indexPath);
        var cache = new ChunkCache(8);
        cache.Add(hash, payload);

        var chunk = new ChunkInfo
        {
            Hash = hash,
            FileOffset = 0,
            OriginalSize = payload.Length,
            CompressedSize = payload.Length,
        };
        using var stream = new ChunkStream("key", new[] { chunk }, store, cache);

        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = -1);
    }
}
