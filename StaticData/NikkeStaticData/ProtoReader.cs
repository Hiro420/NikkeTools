using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NikkeStaticData;

/// <summary>
/// Minimal protobuf wire-format reader for the pack-info response body.
///
/// Every read is bounds-checked against <see cref="buffer"/>: the input arrives
/// over HTTP, so a truncated or hostile body is an expected operational
/// condition. A malformed input raises <see cref="InvalidDataException"/>, which
/// lets the caller distinguish "the server sent something unexpected" from
/// "this program is broken".
/// </summary>
public class ProtoReader
{
	private const int MaxVarint32Bytes = 5;
	private const int MaxVarint64Bytes = 10;

	private readonly byte[] buffer;
	private int position;

	public ProtoReader(byte[] data)
	{
		buffer = data ?? throw new ArgumentNullException(nameof(data));
		position = 0;
	}

	public bool HasMoreData => position < buffer.Length;

	/// <summary>Bytes not yet consumed.</summary>
	public int Remaining => buffer.Length - position;

	private void RequireRemaining(int count, string what)
	{
		// count < 0 cannot happen after the callers' own checks, but keeping the
		// guard here means no future caller can turn a negative count into an
		// advance backwards through the buffer.
		if (count < 0 || count > buffer.Length - position)
		{
			throw new InvalidDataException(
				$"{what}: need {count} byte(s) at offset {position}, " +
				$"only {buffer.Length - position} remain (buffer length {buffer.Length}).");
		}
	}

	public uint ReadVarint()
	{
		uint result = 0;
		int shift = 0;

		for (int i = 0; i < MaxVarint32Bytes; i++)
		{
			RequireRemaining(1, "Truncated varint");
			byte b = buffer[position++];

			// The fifth byte contributes bits 28..34; only 28..31 exist in a uint.
			if (i == MaxVarint32Bytes - 1 && b > 0x0F)
				throw new InvalidDataException(
					$"Varint at offset {position - 1} overflows 32 bits (trailing byte 0x{b:X2}).");

			result |= (uint)(b & 0x7F) << shift;

			if ((b & 0x80) == 0)
				return result;

			shift += 7;
		}

		throw new InvalidDataException(
			$"Varint at offset {position - MaxVarint32Bytes} is longer than {MaxVarint32Bytes} bytes.");
	}

	public long ReadVarint64()
	{
		long result = 0;
		int shift = 0;

		for (int i = 0; i < MaxVarint64Bytes; i++)
		{
			RequireRemaining(1, "Truncated varint");
			byte b = buffer[position++];

			// The tenth byte contributes bits 63..69; only bit 63 exists in a long.
			if (i == MaxVarint64Bytes - 1 && b > 0x01)
				throw new InvalidDataException(
					$"Varint at offset {position - 1} overflows 64 bits (trailing byte 0x{b:X2}).");

			result |= (long)(b & 0x7F) << shift;

			if ((b & 0x80) == 0)
				return result;

			shift += 7;
		}

		throw new InvalidDataException(
			$"Varint at offset {position - MaxVarint64Bytes} is longer than {MaxVarint64Bytes} bytes.");
	}

	public byte[] ReadLengthDelimitedBytes()
	{
		// Read as uint and range-check before casting: 0xFFFFFFFF would otherwise
		// become -1 and reach the allocation as a negative size.
		uint declared = ReadVarint();
		if (declared > int.MaxValue)
			throw new InvalidDataException(
				$"Length-delimited field declares {declared} bytes, which exceeds int.MaxValue.");

		int length = (int)declared;
		RequireRemaining(length, "Length-delimited field");

		byte[] result = new byte[length];
		Array.Copy(buffer, position, result, 0, length);
		position += length;
		return result;
	}

	public string ReadLengthDelimitedString()
	{
		var bytes = ReadLengthDelimitedBytes();
		return Encoding.UTF8.GetString(bytes);
	}

	public void SkipField(int wireType)
	{
		switch (wireType)
		{
			case 0: // varint
				ReadVarint();
				break;
			case 1: // 64-bit
				RequireRemaining(8, "64-bit field");
				position += 8;
				break;
			case 2: // length-delimited
				uint declared = ReadVarint();
				if (declared > int.MaxValue)
					throw new InvalidDataException(
						$"Skipped length-delimited field declares {declared} bytes, which exceeds int.MaxValue.");
				RequireRemaining((int)declared, "Skipped length-delimited field");
				position += (int)declared;
				break;
			case 5: // 32-bit
				RequireRemaining(4, "32-bit field");
				position += 4;
				break;
			default:
				throw new NotSupportedException($"Unsupported wire type {wireType}");
		}
	}
}
