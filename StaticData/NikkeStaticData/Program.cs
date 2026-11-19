using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NikkeStaticData;

public static class FileDecryptor
{
	static HttpClient client = new HttpClient();
	private static readonly string url = "https://global-lobby.nikke-kr.com/v1/get-static-data-pack-info-mpk";

	static async Task Main(string[] args)
	{
		Console.WriteLine($"Fetching url...");
		byte[] encodedBytes = await GetProtoBytes();
		ResStaticDataPackInfo resStaticDataPackInfo = ResStaticDataPackInfo.ParseFrom(encodedBytes);
		resStaticDataPackInfo.Validate();

		Console.WriteLine($"Got response, latest version is {resStaticDataPackInfo.Version}");

		Console.WriteLine($"Downloading StaticData.pack...");
		byte[] fileBytes = await GetStaticDataBytes(resStaticDataPackInfo.Url);

		// The response advertises a size and SHA-256 for the pack; neither was ever
		// checked. An incomplete download or a wrong file therefore surfaced much
		// later as an unrelated crypto or 'invalid zip' error.
		if (resStaticDataPackInfo.Size > 0 && fileBytes.LongLength != resStaticDataPackInfo.Size)
			throw new InvalidDataException(
				$"Downloaded StaticData.pack is {fileBytes.LongLength} bytes, " +
				$"but the response advertised {resStaticDataPackInfo.Size}.");

		if (resStaticDataPackInfo.Sha256Sum.Length > 0)
		{
			byte[] actualHash = SHA256.HashData(fileBytes);
			if (!actualHash.AsSpan().SequenceEqual(resStaticDataPackInfo.Sha256Sum))
				throw new InvalidDataException(
					"Downloaded StaticData.pack does not match the advertised SHA-256: " +
					$"expected={Convert.ToHexString(resStaticDataPackInfo.Sha256Sum)}, " +
					$"actual={Convert.ToHexString(actualHash)}.");
		}

		byte[] password = Convert.FromBase64String(
			"y8Icb/P1B/UFusrUmCiEH/DROMdh39bmZJqFEz4aagxoDivE33L4xlXkexQ2GDun0SCBItGpGIRlEwvtowDl2Q=="
		);

		byte[] key1 = new Rfc2898DeriveBytes(password, resStaticDataPackInfo.Salt1, 10000, HashAlgorithmName.SHA256).GetBytes(32);
		byte[] key2 = new Rfc2898DeriveBytes(password, resStaticDataPackInfo.Salt2, 10000, HashAlgorithmName.SHA256).GetBytes(32);

		Console.WriteLine($"Got keys, decrypting (part 1)...");
		byte[] part1 = DecryptData(fileBytes, key2);
		byte[] innerEncrypted;
		using (var part1Stream = new MemoryStream(part1))
		using (var zip = new ZipArchive(part1Stream, ZipArchiveMode.Read))
		{
			var entry = zip.Entries.First(e => e.Name == "data"); // or whatever the file is named
			using var entryStream = entry.Open();
			using var ms = new MemoryStream();
			entryStream.CopyTo(ms);
			innerEncrypted = ms.ToArray();
		}

		Console.WriteLine($"Got keys, decrypting (part 2)...");
		byte[] finalZipBytes = DecryptData(innerEncrypted, key1, ctr: true);

		Console.WriteLine($"Decrypted! Saving...");
		File.WriteAllBytes($"StaticData.zip", finalZipBytes);
		Console.WriteLine($"Done.");
	}

	static async Task<byte[]> GetStaticDataBytes(string url)
	{
		using var httpClient = new HttpClient();
		var fileBytes = await httpClient.GetByteArrayAsync(url).ConfigureAwait(false);
		return fileBytes;
	}

	static async Task<byte[]> GetProtoBytes()
	{
		var handler = new SocketsHttpHandler
		{
			ConnectCallback = async (context, cancellationToken) =>
			{
				var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
				try
				{
					await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);

					var sslStream = new SslStream(new NetworkStream(socket, ownsSocket: true));

					// When using HTTP/2, you must also keep in mind to set options like ApplicationProtocols
					await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
					{
						TargetHost = context.DnsEndPoint.Host,
						EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls11

					}, cancellationToken);

					return sslStream;
				}
				catch
				{
					socket.Dispose();
					throw;
				}
			}
		};

		HttpClient client = new(handler);
		client.DefaultRequestHeaders.Add("Accept", "application/octet-stream+protobuf");
		client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate");

		byte[] postData = Array.Empty<byte>();
		var content = new ByteArrayContent(postData);
		content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream+protobuf");

		HttpResponseMessage response = await client.PostAsync(url, content);
		response.EnsureSuccessStatusCode();

		byte[] responseData = await response.Content.ReadAsByteArrayAsync();
		return responseData;
	}

	private static byte[] DecryptData(byte[] encryptedData, byte[] key, bool ctr = false)
	{
		byte[] aesKey = key.Take(16).ToArray();
		byte[] iv = key.Skip(key.Length - 16).ToArray();

		if (!ctr)
		{
			using var aes = Aes.Create();
			aes.KeySize = 128;
			aes.BlockSize = 128;
			aes.Key = aesKey;
			aes.IV = iv;
			aes.Mode = CipherMode.CBC;
			aes.Padding = PaddingMode.None;

			using var ms = new MemoryStream();
			using var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write);
			cs.Write(encryptedData, 0, encryptedData.Length);
			cs.FlushFinalBlock();
			return ms.ToArray();
		}
		else
		{
			using var aes = Aes.Create();
			aes.KeySize = 128;
			aes.BlockSize = 128;
			aes.Mode = CipherMode.ECB;
			aes.Padding = PaddingMode.None;
			aes.Key = aesKey;

			using var encryptor = aes.CreateEncryptor();
			using var ms = new MemoryStream();

			byte[] counter = (byte[])iv.Clone();
			byte[] buffer = new byte[16];
			byte[] keystreamBlock = new byte[16];

			for (int i = 0; i < encryptedData.Length; i += 16)
			{
				encryptor.TransformBlock(counter, 0, 16, keystreamBlock, 0);

				int blockSize = Math.Min(16, encryptedData.Length - i);
				for (int j = 0; j < blockSize; j++)
				{
					buffer[j] = (byte)(encryptedData[i + j] ^ keystreamBlock[j]);
				}

				ms.Write(buffer, 0, blockSize);
				IncrementCounter(counter);
			}

			return ms.ToArray();
		}
	}

	private static void IncrementCounter(byte[] counter)
	{
		for (int i = counter.Length - 1; i >= 0; i--)
		{
			if (++counter[i] != 0)
				break;
		}
	}
}

