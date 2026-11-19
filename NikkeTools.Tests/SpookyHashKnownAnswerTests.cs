using System.Text.Json;
using UnityEngine;
using Xunit;

namespace NikkeTools.Tests;

/// <summary>
/// Known-answer tests for the hand-written SpookyHashV2 in <c>Hash128.cs</c>.
///
/// This is the integrity foundation of the CatalogV2 chunk store: the store
/// index, the data-file header and every chunk address are authenticated with
/// it. If the hash drifts, the whole validation layer silently stops validating
/// anything, so it is worth pinning to published vectors even though nothing
/// here is currently broken.
///
/// Vectors were produced with Bob Jenkins' original C++ implementation (via the
/// PyPI <c>spookyhash</c> binding) over lengths that straddle every internal
/// boundary of the algorithm: 15/16/17 (short path branches on >15), 31/32/33
/// and 95/96/97 (tail handling), and 191/192/193 (the Short-to-long switch).
/// </summary>
public sealed class SpookyHashKnownAnswerTests
{
    private sealed record Vector(
        int Length,
        string Seed1,
        string Seed2,
        string ExpectedHash1,
        string ExpectedHash2,
        string DataHex);

    private static IReadOnlyList<Vector> LoadVectors()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "spookyhash-vectors.json");
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<Vector>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;
    }

    [Fact]
    public void ReferenceVectorsArePresent()
    {
        IReadOnlyList<Vector> vectors = LoadVectors();

        Assert.Equal(44, vectors.Count);
        // Sanity: the vectors really do vary with length and with seed. A file of
        // 44 identical rows would make every assertion below vacuous.
        Assert.Equal(22, vectors.Select(v => v.Length).Distinct().Count());
        Assert.Equal(2, vectors.Select(v => v.Seed1).Distinct().Count());
    }

    [Fact]
    public void Hash128_MatchesReferenceImplementation()
    {
        List<string> mismatches = new();

        foreach (Vector vector in LoadVectors())
        {
            byte[] data = Convert.FromHexString(vector.DataHex);
            Assert.Equal(vector.Length, data.Length);

            Hash128 hash = new(
                Convert.ToUInt64(vector.Seed1, 16),
                Convert.ToUInt64(vector.Seed2, 16));

            unsafe
            {
                fixed (byte* p = data)
                    hash.Append(p, (ulong)data.Length);
            }

            string actual1 = hash.u64_0.ToString("x16");
            string actual2 = hash.u64_1.ToString("x16");

            if (actual1 != vector.ExpectedHash1 || actual2 != vector.ExpectedHash2)
            {
                mismatches.Add(
                    $"length={vector.Length} seed=({vector.Seed1},{vector.Seed2}) " +
                    $"expected=({vector.ExpectedHash1},{vector.ExpectedHash2}) " +
                    $"actual=({actual1},{actual2})");
            }
        }

        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} of 44 vectors disagree with the reference implementation:" +
            Environment.NewLine + string.Join(Environment.NewLine, mismatches));
    }

    [Fact]
    public void ChecksumIsFoldedPerRecord_NotHashedAsOneBuffer()
    {
        // ChunkStoreIndex computes its checksum by appending the 12-byte header and
        // then each 28-byte record to one accumulated state. That fold is NOT the
        // same function as hashing the concatenation in a single call -- the
        // previous 128-bit result is used as the seed for the next buffer, not as
        // streaming state.
        //
        // This pins the shape of that fold. Someone "simplifying" the reader to a
        // single one-shot hash over a concatenated buffer would change the expected
        // checksum of every existing index file, so the change must be deliberate
        // and visible rather than accidental.
        //
        // Note: whether the writer on the game side folds the same way is not
        // established here -- that needs a real store.cdb.idx, which this
        // repository does not ship.
        byte[] header = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();
        byte[] record = Enumerable.Range(0, 28).Select(i => (byte)(i * 7)).ToArray();
        byte[] concatenated = header.Concat(record).ToArray();

        Hash128 folded = default;
        unsafe
        {
            fixed (byte* pHeader = header)
                folded.Append(pHeader, (ulong)header.Length);
            fixed (byte* pRecord = record)
                folded.Append(pRecord, (ulong)record.Length);
        }

        Hash128 oneShot = default;
        unsafe
        {
            fixed (byte* pAll = concatenated)
                oneShot.Append(pAll, (ulong)concatenated.Length);
        }

        Assert.NotEqual(oneShot.u64_0, folded.u64_0);
        Assert.NotEqual(oneShot.u64_1, folded.u64_1);

        // Golden value for the fold itself, so a change to Append's chaining is
        // caught even if both sides of the comparison above move together.
        Assert.Equal("71349933aee0a165", folded.u64_0.ToString("x16"));
        Assert.Equal("dcca295f8b539f5d", folded.u64_1.ToString("x16"));
    }

    [Fact]
    public void AppendingIsOrderSensitive()
    {
        // Guards against an accidental "commutative fold" -- the record order is
        // what binds an entry's hash to its offset, so swapping two buffers must
        // change the result.
        byte[] first = { 1, 2, 3, 4, 5, 6, 7, 8 };
        byte[] second = { 9, 10, 11, 12, 13, 14, 15, 16 };

        Hash128 forward = default;
        unsafe
        {
            fixed (byte* p1 = first)
                forward.Append(p1, (ulong)first.Length);
            fixed (byte* p2 = second)
                forward.Append(p2, (ulong)second.Length);
        }

        Hash128 reversed = default;
        unsafe
        {
            fixed (byte* p2 = second)
                reversed.Append(p2, (ulong)second.Length);
            fixed (byte* p1 = first)
                reversed.Append(p1, (ulong)first.Length);
        }

        Assert.NotEqual(forward.u64_0, reversed.u64_0);
        Assert.NotEqual(forward.u64_1, reversed.u64_1);
    }
}
