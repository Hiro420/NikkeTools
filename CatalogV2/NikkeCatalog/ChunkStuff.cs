using System.Buffers;
using System.Runtime.InteropServices;

namespace Shiftup.Patch.Runtime
{
    using UnityEngine;

    internal struct ChunkInfo
    {
        private Hash128 _Hash_k__BackingField;
        private long _FileOffset_k__BackingField;
        private int _OriginalSize_k__BackingField;
        private int _CompressedSize_k__BackingField;

        public Hash128 Hash { readonly get => _Hash_k__BackingField; set => _Hash_k__BackingField = value; }
        public long FileOffset { readonly get => _FileOffset_k__BackingField; set => _FileOffset_k__BackingField = value; }
        public int OriginalSize { readonly get => _OriginalSize_k__BackingField; set => _OriginalSize_k__BackingField = value; }
        public int CompressedSize { readonly get => _CompressedSize_k__BackingField; set => _CompressedSize_k__BackingField = value; }
    }

    internal sealed class ChunkCache
    {
        private readonly int _capacity;
        private readonly Dictionary<Hash128, LinkedListNode<CachedChunk>> _cache;
        private readonly LinkedList<CachedChunk> _lruList;
        private readonly object _lock;

        private struct CachedChunk
        {
            private Hash128 _Hash_k__BackingField;
            private byte[] _Data_k__BackingField;
            private int _Length_k__BackingField;

            public Hash128 Hash { readonly get => _Hash_k__BackingField; set => _Hash_k__BackingField = value; }
            public byte[] Data { readonly get => _Data_k__BackingField; set => _Data_k__BackingField = value; }
            public int Length { readonly get => _Length_k__BackingField; set => _Length_k__BackingField = value; }
        }

        public ChunkCache(int capacity)
        {
            _capacity = capacity;
            _cache = new Dictionary<Hash128, LinkedListNode<CachedChunk>>();
            _lruList = new LinkedList<CachedChunk>();
            _lock = new object();
        }

        public bool TryRead(Hash128 hash, int sourceOffset, Span<byte> destination)
        {
            if (sourceOffset < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(sourceOffset), sourceOffset,
                    "Cache source offset must be non-negative."
                );

            lock (_lock)
            {
                if (!_cache.TryGetValue(hash, out LinkedListNode<CachedChunk>? node))
                    return false;

                CachedChunk chunk = node.Value;
                if ((long)chunk.Length - sourceOffset < destination.Length)
                    return false;

                Promote(node);
                chunk.Data.AsSpan(sourceOffset, destination.Length).CopyTo(destination);
                return true;
            }
        }

        public void Add(Hash128 hash, ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
                throw new ArgumentException($"Chunk data must not be empty: hash={hash}.", nameof(data));

            lock (_lock)
            {
                if (_cache.TryGetValue(hash, out LinkedListNode<CachedChunk>? existing))
                {
                    Promote(existing);
                    return;
                }

                if (_cache.Count >= _capacity)
                {
                    LinkedListNode<CachedChunk> last = _lruList.Last!;
                    _lruList.RemoveLast();
                    _cache.Remove(last.Value.Hash);
                    ArrayPool<byte>.Shared.Return(last.Value.Data, clearArray: false);
                }

                byte[] rented = ArrayPool<byte>.Shared.Rent(data.Length);
                data.CopyTo(rented.AsSpan());
                CachedChunk value = new CachedChunk
                {
                    Hash = hash,
                    Data = rented,
                    Length = data.Length
                };
                LinkedListNode<CachedChunk> node = new LinkedListNode<CachedChunk>(value);
                _lruList.AddFirst(node);
                _cache.Add(hash, node);
            }
        }

        private void Promote(LinkedListNode<CachedChunk> node)
        {
            if (node.List == _lruList && node != _lruList.First)
            {
                _lruList.Remove(node);
                _lruList.AddFirst(node);
            }
        }
    }

    internal sealed class ChunkStream : Stream
    {
        private readonly string _Key_k__BackingField;
        private readonly ChunkInfo[] _chunkMap;
        private readonly ChunkStore _chunkStore;
        private readonly ChunkCache _chunkCache;
        private readonly Action<ChunkStream>? _onDispose;
        private readonly long _length;
        private long _position;
        private bool _disposed;

        public string Key => _Key_k__BackingField;

        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public ChunkStream(string key, ChunkInfo[] chunkMap, ChunkStore store, ChunkCache cache, Action<ChunkStream>? onDispose = null)
        {
            _Key_k__BackingField = key;
            _chunkMap = chunkMap;
            _chunkStore = store;
            _chunkCache = cache;
            _onDispose = onDispose;
            long end = 0;
            foreach (ChunkInfo chunk in chunkMap)
            {
                long chunkEnd = checked(chunk.FileOffset + chunk.OriginalSize);
                if (chunkEnd > end) 
                    end = chunkEnd;
            }
            _length = end;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), offset, "Buffer offset must be non-negative.");
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), count, "Read count must be non-negative.");
            if (buffer.Length - (long)offset < count)
                throw new ArgumentException(
                    "Offset and count exceed buffer length: " +
                    $"offset={offset}, count={count}B, bufferLength={buffer.Length}B.",
                    nameof(count)
                );

            if (count == 0 || _position >= _length)
                return 0;

            long originalPosition = _position;
            long requestedEnd = Math.Min(unchecked(originalPosition + count), _length);
            int copied = 0;

            foreach (ChunkInfo chunk in _chunkMap)
            {
                long chunkStart = chunk.FileOffset;
                long chunkEnd = unchecked(chunkStart + chunk.OriginalSize);
                if (chunkStart >= requestedEnd || chunkEnd <= originalPosition)
                    continue;

                long copyStart = Math.Max(originalPosition, chunkStart);
                long copyEnd = Math.Min(requestedEnd, chunkEnd);

                int copyLength = checked((int)(copyEnd - copyStart));
                int chunkOffset = checked((int)(copyStart - chunkStart));
                int destinationOffset = checked((int)(copyStart - originalPosition));
                int targetIndex = checked(offset + destinationOffset);

                ReadChunkInto(in chunk, chunkOffset, buffer.AsSpan(targetIndex, copyLength));
                copied = checked(copied + copyLength);
            }

            _position = unchecked(_position + copied);
            return copied;
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("ChunkStream does not support writing.");
        public override void SetLength(long value) => throw new NotSupportedException("ChunkStream does not support setting length.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("ChunkStream does not support seeking.");

        private void ReadChunkInto(in ChunkInfo chunk, int chunkOffset, Span<byte> destination)
        {
            if (_chunkCache.TryRead(chunk.Hash, chunkOffset, destination))
                return;

            byte[] rentedCompressed = ArrayPool<byte>.Shared.Rent(chunk.CompressedSize);
            try
            {
                Span<byte> compressed = rentedCompressed.AsSpan(0, chunk.CompressedSize);
                bool found = _chunkStore.TryRead(chunk.Hash, compressed, out int bytesRead);
                if (!found || bytesRead != chunk.CompressedSize)
                    throw new InvalidDataException(
                        $"Chunk data is invalid: key='{Key}', hash={chunk.Hash}, " +
                        $"fileOffset={chunk.FileOffset}, " +
                        $"expected={chunk.CompressedSize}B, actual={bytesRead}B."
                    );

                if (chunkOffset == 0 && destination.Length == chunk.OriginalSize)
                {
                    Shiftup.Zstd.Runtime.ZstdCompressor.Decompress(compressed, destination);
                }
                else
                {
                    byte[] rentedOriginal = ArrayPool<byte>.Shared.Rent(chunk.OriginalSize);
                    try
                    {
                        Span<byte> original = rentedOriginal.AsSpan(0, chunk.OriginalSize);
                        Shiftup.Zstd.Runtime.ZstdCompressor.Decompress(compressed, original);
                        original.Slice(chunkOffset, destination.Length).CopyTo(destination);
                        _chunkCache.Add(chunk.Hash, original);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rentedOriginal);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedCompressed);
            }
        }

        public override void Flush() { }

        private void ThrowIfDisposed()
        {
            if (_disposed) 
                throw new ObjectDisposedException(nameof(ChunkStream));
        }
        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing) _onDispose?.Invoke(this);
            }
            base.Dispose(disposing);
        }
    }
}

namespace Shiftup.Zstd.Runtime
{
    internal static class ZstdCompressor
    {
        [DllImport("libzstd", EntryPoint = "ZSTD_decompress", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe nuint ZSTD_decompress(void* destination, nuint destinationCapacity, void* source, nuint sourceSize);

        [DllImport("libzstd", EntryPoint = "ZSTD_isError", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint ZSTD_isError(nuint code);

        [DllImport("libzstd", EntryPoint = "ZSTD_getErrorName", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ZSTD_getErrorName(nuint code);

        public static unsafe void Decompress(ReadOnlySpan<byte> data, Span<byte> destination)
        {
            fixed (byte* source = data)
            fixed (byte* target = destination)
            {
                nuint result = ZSTD_decompress(target, (nuint)destination.Length, source, (nuint)data.Length);
                if (ZSTD_isError(result) != 0)
                {
                    string error = Marshal.PtrToStringAnsi(ZSTD_getErrorName(result)) ?? "unknown";
                    throw new InvalidDataException(
                        "Zstd decompression failed: " +
                        $"compressed={data.Length}B, expected={destination.Length}B, " +
                        $"error='{error}'."
                    );
                }
                if (result != (nuint)destination.Length)
                {
                    throw new InvalidDataException(
                        "Zstd decompression failed: " +
                        $"compressed={data.Length}B, expected={destination.Length}B, " +
                        $"actual={result}B."
                    );
                }
            }
        }
    }
}
