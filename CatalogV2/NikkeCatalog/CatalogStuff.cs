using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Shiftup.Patch.Runtime
{
    public sealed class CatalogInvalidException : IOException
    {
        public CatalogInvalidException(string message) : base(message) { }
    }

    internal sealed class CatalogDb : IDisposable
    {
        private readonly string _DbPath_k__BackingField;
        private SqliteConnection _connection;
        private readonly object _lock;
        private bool _disposed;

        public string DbPath => _DbPath_k__BackingField;

        private CatalogDb(string dbPath)
        {
            _DbPath_k__BackingField = dbPath;
            _connection = null!;
            _lock = new object();
        }

        public static CatalogDb Open(string dbPath, string signaturePath, bool useInMemoryDb)
        {
            CatalogDb db = new CatalogDb(dbPath);
            try
            {
                db.OpenInternal(signaturePath, useInMemoryDb);
                return db;
            }
            catch
            {
                db.Dispose();
                throw;
            }
        }

        private void OpenInternal(string signaturePath, bool useInMemoryDb)
        {
            if (!File.Exists(_DbPath_k__BackingField))
                throw new FileNotFoundException(
                    $"Catalog database not found at '{_DbPath_k__BackingField}'.",
                    _DbPath_k__BackingField
                );

            if (!string.IsNullOrEmpty(signaturePath) && !File.Exists(signaturePath))
                throw new CatalogInvalidException($"Catalog signature file not found at '{signaturePath}'.");

            _ = useInMemoryDb;

            byte[] database;
            using (FileStream file = File.OpenRead(_DbPath_k__BackingField))
            {
                Span<byte> magic = stackalloc byte[16];
                int length = file.Read(magic);

                if (length >= 4 &&
                    magic[0] == (byte)'N' && magic[1] == (byte)'K' &&
                    magic[2] == (byte)'D' && magic[3] == (byte)'B')
                {
                    database = CatalogDecryptor.DecryptFile(_DbPath_k__BackingField);
                }
                else if (length >= 16 && magic.SequenceEqual(Encoding.ASCII.GetBytes("SQLite format 3\0")))
                {
                    database = File.ReadAllBytes(_DbPath_k__BackingField);
                }
                else
                {
                    throw new CatalogInvalidException($"Unknown catalog header in '{_DbPath_k__BackingField}'.");
                }
                // For manual work
                File.WriteAllBytes(DbPath.Replace(".ndb", ".dec.ndb"), database);
            }

            IntPtr nativeBytes = IntPtr.Zero;
            try
            {
                ReadOnlySpan<byte> sqliteMagic = "SQLite format 3\0"u8;
                if (!database.AsSpan().StartsWith(sqliteMagic))
                    throw new CatalogInvalidException("Decrypted catalog is not an SQLite database.");

                _connection = new SqliteConnection("Data Source=:memory:;Mode=Memory;Cache=Private;Pooling=False");
                _connection.Open();

                nativeBytes = SQLitePCL.raw.sqlite3_malloc64(database.LongLength);
                if (nativeBytes == IntPtr.Zero)
                    throw new OutOfMemoryException("Cannot allocate SQLite catalog buffer.");

                Marshal.Copy(database, 0, nativeBytes, database.Length);

                int rc;
                try
                {
                    rc = SQLitePCL.raw.sqlite3_deserialize(
                        _connection.Handle,
                        "main",
                        nativeBytes,
                        database.LongLength,
                        database.LongLength,
                        SQLitePCL.raw.SQLITE_DESERIALIZE_FREEONCLOSE |
                        SQLitePCL.raw.SQLITE_DESERIALIZE_READONLY
                    );
                }
                finally
                {
                    nativeBytes = IntPtr.Zero;
                }

                if (rc != SQLitePCL.raw.SQLITE_OK)
                    throw new CatalogInvalidException($"SQLite failed to deserialize the catalog (error={rc}).");

                using SqliteCommand command = _connection.CreateCommand();
                command.CommandText = "PRAGMA user_version;";
                int version = Convert.ToInt32(command.ExecuteScalar());
                if (version != 1)
                    throw new CatalogInvalidException($"Catalog schema version is unsupported: expected=1, actual={version}.");
            }
            finally
            {
                if (nativeBytes != IntPtr.Zero)
                    SQLitePCL.raw.sqlite3_free(nativeBytes);
                CryptographicOperations.ZeroMemory(database);
            }
        }

        public ChunkInfo[] GetChunkMapForKey(string key)
        {
            lock (_lock)
            {
                ThrowIfDisposed();

                using SqliteCommand command = _connection.CreateCommand();
                command.CommandText = @"
                    SELECT
                        c.hash,
                        cfm.file_offset,
                        c.original_size,
                        c.compressed_size
                    FROM
                        files_chunktype f
                    JOIN
                        chunk_file_map cfm ON f.file_id = cfm.file_id
                    JOIN
                        chunks c ON cfm.chunk_id = c.chunk_id
                    WHERE
                        f.key = @key";
                command.Parameters.Add(new SqliteParameter("@key", key));

                using SqliteDataReader reader = command.ExecuteReader();
                List<ChunkInfo> items = new List<ChunkInfo>();
                while (reader.Read())
                {
                    byte[] bytes = (byte[])reader.GetValue(0);
                    if (bytes.Length < 16)
                        throw new ArgumentException($"Array is too small for a Hash128: expected=16B, actual={bytes.Length}B.", nameof(bytes));

                    Hash128 hash = new Hash128(
                        BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0, 8)),
                        BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8, 8))
                    );
                    items.Add(new ChunkInfo
                    {
                        Hash = hash,
                        FileOffset = reader.GetInt64(1),
                        OriginalSize = reader.GetInt32(2),
                        CompressedSize = reader.GetInt32(3)
                    });
                }
                return items.ToArray();
            }
        }

        public string[] GetAllKeys()
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                using SqliteCommand command = _connection.CreateCommand();
                command.CommandText = "SELECT key FROM files_chunktype;";
                using SqliteDataReader reader = command.ExecuteReader();
                List<string> keys = new List<string>();
                while (reader.Read())
                {
                    keys.Add(reader.GetString(0));
                }
                return keys.ToArray();
            }
        }

        public FilesRawType[] GetAllFileRawTypes()
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                using SqliteCommand command = _connection.CreateCommand();
                command.CommandText = "SELECT file_id, key, hash, extension, size, group_id FROM files_rawtype;";
                using SqliteDataReader reader = command.ExecuteReader();
                List<FilesRawType> items = new List<FilesRawType>();
                while (reader.Read())
                {
                    items.Add(new FilesRawType
                    {
                        FileId = reader.GetInt32(0),
                        Key = reader.GetString(1),
                        Hash = (byte[])reader.GetValue(2),
                        Extention = reader.GetString(3),
                        Size = reader.GetInt64(4),
                        GroupId = reader.GetInt32(5)
                    });
                }
                return items.ToArray();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CatalogDb));
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;

                _connection?.Dispose();
                _disposed = true;
            }
        }
    }
}

public struct FilesRawType
{
    public int FileId;
    public string Key;
    public byte[] Hash;
    public string Extention;
    public long Size;
    public int GroupId;
}

internal class CatalogDecryptor
{
    public static byte[] DecryptFile(string inputPath)
    {
        using var fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(fs);

        var header = new NikkeDatabaseHeader
        {
            Magic = reader.ReadBytes(4),
            Version = ReadUInt32BigEndian(reader),
            AesKey = reader.ReadBytes(16),
            SegmentSize = ReadUInt32BigEndian(reader),
            SegmentCount = ReadUInt32BigEndian(reader)
        };

        if (!header.Magic.SequenceEqual(Encoding.ASCII.GetBytes("NKDB")))
            throw new Exception("Invalid magic");

        if (header.Version != 1)
            throw new Exception("Invalid version");

        int lengthByteCount = (header.SegmentSize * header.SegmentCount > 0xFFFFFFFF) ? 5 : 4;

        long ReadOffset()
        {
            byte[] offsetBytes = reader.ReadBytes(lengthByteCount);
            return offsetBytes.Aggregate(0L, (acc, b) => (acc << 8) | b);
        }

        long currentOffset = ReadOffset();
        var segments = new (long Offset, long Length, int Index)[header.SegmentCount];

        for (int i = 0; i < header.SegmentCount; i++)
        {
            long nextOffset = ReadOffset();
            segments[i] = (currentOffset, nextOffset - currentOffset, i);
            currentOffset = nextOffset;
        }

        using var output = new MemoryStream();

        foreach (var (offset, length, index) in segments)
        {
            fs.Seek(offset, SeekOrigin.Begin);
            byte[] segment = reader.ReadBytes((int)length);

            byte[] iv = new byte[16];
            BitConverter.GetBytes(index).CopyTo(iv, 0);
            BitConverter.GetBytes((int)offset).CopyTo(iv, 4);

            byte[] decrypted = DecryptAES_OFB(header.AesKey, iv, segment);

            using var ms = new MemoryStream(decrypted);
            using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
            zlib.CopyTo(output);

        }

        return output.ToArray();
    }

    static uint ReadUInt32BigEndian(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes, 0);
    }

    static byte[] DecryptAES_OFB(byte[] key, byte[] iv, byte[] input)
    {
        /*
		Console.WriteLine(Convert.ToHexString(key));
		Console.WriteLine(Convert.ToHexString(iv));
		*/
        using Aes aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        MemoryStream testVectorStream = new MemoryStream(input);
        OFBStream testOFBStream = new OFBStream(testVectorStream, aes, CryptoStreamMode.Read);
        MemoryStream cipherTextStream = new MemoryStream();
        testOFBStream.CopyTo(cipherTextStream);
        return cipherTextStream.ToArray();
    }
}

struct NikkeDatabaseHeader
{
    public byte[] Magic;
    public uint Version;
    public byte[] AesKey;
    public uint SegmentSize;
    public uint SegmentCount;
}