using System.Buffers.Binary;
using UnityEngine;

namespace Shiftup.Patch.Runtime
{
    internal sealed class ChunkStoreInvalidException : IOException
    {
        public ChunkStoreInvalidException(string message) : base(message) { }
    }

    internal sealed class ChunkStoreIndex
    {
        private readonly string _filePath;
        private readonly Dictionary<Hash128, ValueTuple<long, int>> _entries;

        public int Count => _entries.Count;
        public IEnumerable<Hash128> Hashes => _entries.Keys;

        public ChunkStoreIndex(string filePath)
        {
            _filePath = filePath;
            _entries = new Dictionary<Hash128, ValueTuple<long, int>>();
        }

        public void Load()
        {
            _entries.Clear();
            if (!File.Exists(_filePath))
                throw new ChunkStoreInvalidException($"Chunk store index file not found at '{_filePath}'.");

            using FileStream stream = File.OpenRead(_filePath);
            LoadCore(stream);
        }

        private unsafe void LoadCore(FileStream stream)
        {
            Span<byte> header = stackalloc byte[12];
            stream.ReadExactly(header);

            uint actualMagic = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0, 4));
            const uint expectedMagic = 0x58444943;
            if (actualMagic != expectedMagic)
                throw new ChunkStoreInvalidException($"Chunk store index has bad magic: expected=0x{expectedMagic:X8}, actual=0x{actualMagic:X8}.");

            uint actualVersion = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
            if (actualVersion != 1)
                throw new ChunkStoreInvalidException($"Chunk store index version is unsupported: expected=1, actual={actualVersion}.");

            int count = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4));
            long max = Math.Max(0L, (stream.Length - 28L) / 28L);
            if (count < 0 || count > max)
                throw new ChunkStoreInvalidException($"Chunk store index has implausible entry count: count={count}, max={max}.");

            _entries.EnsureCapacity(count);

            Hash128 checksum = default;
            fixed (byte* p = header) 
                checksum.Append(p, (ulong)header.Length);

            Span<byte> record = stackalloc byte[28];
            for (int i = 0; i < count; i++)
            {
                stream.ReadExactly(record);
                fixed (byte* p = record)
                    checksum.Append(p, (ulong)record.Length);

                Hash128 key = new Hash128(
                    BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(0, 8)),
                    BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(8, 8))
                );
                long offset = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(16, 8));
                int size = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(24, 4));
                _entries[key] = new ValueTuple<long, int>(offset, size);
            }

            Span<byte> trailingHash = stackalloc byte[16];
            stream.ReadExactly(trailingHash);
            Hash128 actualChecksum = new Hash128(
                BinaryPrimitives.ReadUInt64LittleEndian(trailingHash.Slice(0, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(trailingHash.Slice(8, 8))
            );
            if (checksum != actualChecksum)
                throw new ChunkStoreInvalidException($"Chunk store index checksum mismatch: expected={checksum}, actual={actualChecksum}.");
        }
        public bool Contains(Hash128 hash) => _entries.ContainsKey(hash);
        public bool TryGet(Hash128 hash, out ValueTuple<long, int> entry) => _entries.TryGetValue(hash, out entry);
        public void Set(Hash128 hash, ValueTuple<long, int> entry) => _entries[hash] = entry;
        public bool Remove(Hash128 hash) => _entries.Remove(hash);
        public Dictionary<Hash128, ValueTuple<long, int>> GetEnumerator() => _entries;
    }


    internal sealed class ChunkStore : IDisposable
    {
        private readonly string _filePath;
        private FileStream _stream;
        private readonly ChunkStoreIndex _index;
        private readonly object _lock;
        private bool _disposed;

        private ChunkStore(string filePath, string indexPath)
        {
            _lock = new object();
            _filePath = filePath;
            _index = new ChunkStoreIndex(indexPath);
            _stream = null!;
        }

        public static ChunkStore Open(string filePath, string indexPath, bool createIfNotExists = false)
        {
            bool exists = File.Exists(filePath);
            if (!exists)
            {
                if (!createIfNotExists)
                    throw new FileNotFoundException($"Chunk store data file not found at '{filePath}'.", filePath);
                throw new NotImplementedException("New-file path requires native WriteHeader and ChunkStoreIndex.Save bodies.");
            }

            ChunkStore store = new ChunkStore(filePath, indexPath);
            store._stream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                store.LoadExistingFile();
                return store;
            }
            catch
            {
                store._stream.Dispose();
                throw;
            }
        }

        private void LoadExistingFile()
        {
            if (_stream.Length < 256)
                throw new ChunkStoreInvalidException(
                    "Chunk store data file is shorter than the header: " +
                    $"length={_stream.Length}B, expected>=256B."
                );

            ReadHeader();
            _index.Load();
            long end = GetDataEnd();
            if (_stream.Length < end)
                throw new ChunkStoreInvalidException(
                    "Indexed entries extend past the end of the chunk store data file: " +
                    $"length={_stream.Length}B, indexedEnd={end}."
                );
        }

        private unsafe void ReadHeader()
        {
            Span<byte> header = stackalloc byte[256];
            _stream.Position = 0;
            _stream.ReadExactly(header);

            uint actualMagic = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0, 4));
            const uint expectedMagic = 0x424C4243;
            if (actualMagic != expectedMagic)
                throw new ChunkStoreInvalidException($"Chunk store has bad magic: expected=0x{expectedMagic:X8}, actual=0x{actualMagic:X8}.");

            uint actualVersion = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
            if (actualVersion != 1)
                throw new ChunkStoreInvalidException($"Chunk store version is unsupported: expected=1, actual={actualVersion}.");

            Hash128 checksum = default;
            fixed (byte* p = header)
                checksum.Append(p, 12);

            Hash128 stored = new Hash128(
                BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(12, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(20, 8))
            );
            if (checksum != stored)
                throw new ChunkStoreInvalidException($"Chunk store header checksum mismatch: expected={checksum}, actual={stored}.");
        }

        public bool ContainsHash(Hash128 hash)
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                return _index.Contains(hash);
            }
        }

        public List<Hash128> GetAllHashes()
        {
            lock (_lock)
            {
                ThrowIfDisposed();

                return new List<Hash128>(_index.Hashes);
            }
        }

        public long GetBytesFor(IEnumerable<Hash128> hashes)
        {
            lock (_lock)
            {
                ThrowIfDisposed();

                long total = 0;
                foreach (Hash128 hash in hashes)
                {
                    if (_index.TryGet(hash, out ValueTuple<long, int> entry))
                        total = unchecked(total + entry.Item2);
                }

                return total;
            }
        }

        private long GetDataEnd()
        {
            long end = 256;
            foreach (KeyValuePair<Hash128, ValueTuple<long, int>> pair in _index.GetEnumerator())
            {
                ValueTuple<long, int> value = pair.Value;
                long entryEnd = unchecked(value.Item1 + value.Item2);
                if (entryEnd > end)
                    end = entryEnd;
            }
            return end;
        }

        public bool TryRead(Hash128 hash, Span<byte> destination, out int bytesRead)
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                bytesRead = 0;

                if (!_index.TryGet(hash, out ValueTuple<long, int> entry))
                    return false;

                if (destination.Length < entry.Item2)
                {
                    throw new ArgumentException(
                        "Destination span is too small for the chunk: " +
                        $"destinationLength={destination.Length}B, chunkLength={entry.Item2}B.",
                        nameof(destination)
                    );
                }

                _stream.Position = entry.Item1;
                _stream.ReadExactly(destination.Slice(0, entry.Item2));
                bytesRead = entry.Item2;
                return true;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ChunkStore));
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;

                _stream?.Dispose();
                _disposed = true;
            }
        }
    }
}
