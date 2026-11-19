using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NikkeStaticData;

public class ResStaticDataPackInfo : IProtoParsable<ResStaticDataPackInfo>
{
	public string Url { get; set; } = "";
	public long Size { get; set; }
	public byte[] Sha256Sum { get; set; } = Array.Empty<byte>();
	public byte[] Salt1 { get; set; } = Array.Empty<byte>();
	public byte[] Salt2 { get; set; } = Array.Empty<byte>();
	public string Version { get; set; } = "";

	public static ResStaticDataPackInfo ParseFrom(byte[] data)
	{
		var reader = new ProtoReader(data);
		var result = new ResStaticDataPackInfo();

		while (reader.HasMoreData)
		{
			uint tag = reader.ReadVarint();
			int fieldNumber = (int)(tag >> 3);
			int wireType = (int)(tag & 0x7);

			switch (fieldNumber)
			{
				case 1: // string url
					result.Url = reader.ReadLengthDelimitedString();
					break;
				case 2: // int64 size
					result.Size = reader.ReadVarint64();
					break;
				case 3: // bytes sha256_sum
					result.Sha256Sum = reader.ReadLengthDelimitedBytes();
					break;
				case 4: // bytes salt1
					result.Salt1 = reader.ReadLengthDelimitedBytes();
					break;
				case 5: // bytes salt2
					result.Salt2 = reader.ReadLengthDelimitedBytes();
					break;
				case 6: // string version
					result.Version = reader.ReadLengthDelimitedString();
					break;
				default:
					reader.SkipField(wireType);
					break;
			}
		}

		return result;
	}

	/// <summary>
	/// Fields the caller cannot proceed without. A proto3 message decodes a
	/// missing field to its default, so without this check a truncated or
	/// version-skewed response silently becomes an object with an empty URL and
	/// empty salts -- and the failure then surfaces far away as a bogus
	/// UriFormatException or 'invalid zip' after a wrong key is derived.
	/// </summary>
	public void Validate()
	{
		if (string.IsNullOrEmpty(Url))
			throw new InvalidDataException("StaticData pack info is missing 'url' (field 1).");

		if (Salt1.Length == 0)
			throw new InvalidDataException("StaticData pack info is missing 'salt1' (field 4).");

		if (Salt2.Length == 0)
			throw new InvalidDataException("StaticData pack info is missing 'salt2' (field 5).");
	}
}
