using Shiftup.Patch.Runtime;
using System.Text;
using UnityEngine;

class Program
{
    static void Main()
    {
        // Example path
        string inputRoot = @"E:\NIKKE\Unity\com_proximabeta_NIKKE\com.shiftup.patch\saus";

        string chunkRoot = Path.Combine(inputRoot, "chunk");
        string rawRoot = Path.Combine(inputRoot, "raw");

        ChunkStore store = ChunkStore.Open(
            Path.Combine(chunkRoot, "store.cdb"), 
            Path.Combine(chunkRoot, "store.cdb.idx")
        );
        
        List<Hash128> allHashes = store.GetAllHashes();
        ChunkCache cache = new ChunkCache(allHashes.Count);

        Console.WriteLine($"Opened store with {allHashes.Count} entries.");

        using CatalogDb catalog = CatalogDb.Open(
            Path.Combine(inputRoot, "catalog.ndb"), 
            Path.Combine(inputRoot, "catalog.ndb.nds"), 
            false
        );

        foreach (FilesRawType rawType in catalog.GetAllFileRawTypes())
        {
            // The hash should be Hash128, you can get it by doing HashUtility.ComputeFileHash128
            // Thanks to this parser you can make an asset downloader, example catalog would be
            // https://cloud.nikke-kr.com/prdenv/151-b2f934cc2b/StandaloneWindows64/pck/saus/67bb29a/catalog.ndb
            string rawFileName = $"{Convert.ToHexString(rawType.Hash).ToLower()}{rawType.Extention}";
            string rawFilePath = Path.Combine(rawRoot, rawFileName);
            if (File.Exists(rawFilePath))
            {
                string outputPath = Path.Combine(Environment.CurrentDirectory, "Extracted_Raw", rawType.Key);
                byte[] data = File.ReadAllBytes(rawFilePath);
                if (data[0..4].SequenceEqual(Encoding.ASCII.GetBytes("NKDB")))
                {
                    Console.WriteLine($"Decrypting raw file: {rawType.Key} -> {outputPath}");
                    data = CatalogDecryptor.DecryptFile(rawFilePath);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? throw new InvalidOperationException("Failed to get directory name."));
                File.WriteAllBytes(outputPath, data);
                Console.WriteLine($"Extracted raw file: {rawType.Key} -> {outputPath}");
            }
        }

        string[] allKeys = catalog.GetAllKeys();

        foreach (string key in allKeys)
        {
            //Console.WriteLine($"Extracting {key}...");
            try
            {
                ExtractKey(catalog, store, cache, key);
            }
            catch (Exception ex)
            {
                if (ex is FileNotFoundException) // chunk doesnt exist, skip
                    continue;
                Console.WriteLine($"Failed to extract {key}: {ex.Message}");
            }
        }
        
        foreach (string key in allKeys)
        {
            string outputPath = Path.Combine(Environment.CurrentDirectory, "Extracted", Path.GetFileName(key));
            if (!File.Exists(outputPath))
                Console.WriteLine($"Missing extracted file: {outputPath}");
        }
    }

    static void ExtractKey(CatalogDb catalog, ChunkStore store, ChunkCache cache, string key)
    {
        ChunkInfo[] chunkMap = catalog.GetChunkMapForKey(key);
        if (chunkMap.Length == 0)
            throw new FileNotFoundException($"Chunk key '{key}' was not found in the catalog.");

        HashSet<UnityEngine.Hash128> availableHashes = new HashSet<UnityEngine.Hash128>(store.GetAllHashes());
        foreach (ChunkInfo chunk in chunkMap)
        {
            if (!availableHashes.Contains(chunk.Hash))
                throw new FileNotFoundException($"Chunk {chunk.Hash} is missing from store.cdb.");
        }

        using ChunkStream stream = new ChunkStream(key, chunkMap, store, cache);

        string outputRoot = Path.Combine(Environment.CurrentDirectory, "Extracted");
        Directory.CreateDirectory(outputRoot);
        string outputPath = Path.Combine(outputRoot, Path.GetFileName(key));

        using (FileStream output = File.Create(outputPath))
            stream.CopyTo(output);

        Console.WriteLine($"Extracted {key}: {chunkMap.Length} chunks, {stream.Length} bytes -> {outputPath}");
    }
}