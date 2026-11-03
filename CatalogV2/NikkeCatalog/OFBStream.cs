using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

public class OFBStream : Stream
{
	private const int BLOCKS = 16;
	private const int EOS = 0;

	private Stream parent;
	private CryptoStream cbcStream;
	private CryptoStreamMode mode;
	private byte[] keyStreamBuffer;
	private int keyStreamBufferOffset;
	private byte[] readWriteBuffer;

	public OFBStream(Stream parent, SymmetricAlgorithm algo, CryptoStreamMode mode)
	{
		if (algo.Mode != CipherMode.CBC)
			algo.Mode = CipherMode.CBC;
		if (algo.Padding != PaddingMode.None)
			algo.Padding = PaddingMode.None;
		this.parent = parent;
		this.cbcStream = new CryptoStream(new ZeroStream(), algo.CreateEncryptor(), CryptoStreamMode.Read);
		this.mode = mode;
		keyStreamBuffer = new byte[algo.BlockSize * BLOCKS];
		readWriteBuffer = new byte[keyStreamBuffer.Length];
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		if (!CanRead)
		{
			throw new NotSupportedException();
		}

		int toRead = Math.Min(count, readWriteBuffer.Length);
		int read = parent.Read(readWriteBuffer, 0, toRead);
		if (read == EOS)
			return EOS;

		for (int i = 0; i < read; i++)
		{
			// NOTE could be optimized (branches for each byte)
			if (keyStreamBufferOffset % keyStreamBuffer.Length == 0)
			{
				FillKeyStreamBuffer();
				keyStreamBufferOffset = 0;
			}

			buffer[offset + i] = (byte)(readWriteBuffer[i]
				^ keyStreamBuffer[keyStreamBufferOffset++]);
		}

		return read;
	}

	public override void Write(byte[] buffer, int offset, int count)
	{
		if (!CanWrite)
		{
			throw new NotSupportedException();
		}

		int readWriteBufferOffset = 0;
		for (int i = 0; i < count; i++)
		{
			if (keyStreamBufferOffset % keyStreamBuffer.Length == 0)
			{
				FillKeyStreamBuffer();
				keyStreamBufferOffset = 0;
			}

			if (readWriteBufferOffset % readWriteBuffer.Length == 0)
			{
				parent.Write(readWriteBuffer, 0, readWriteBufferOffset);
				readWriteBufferOffset = 0;
			}

			readWriteBuffer[readWriteBufferOffset++] = (byte)(buffer[offset + i]
				^ keyStreamBuffer[keyStreamBufferOffset++]);
		}

		parent.Write(readWriteBuffer, 0, readWriteBufferOffset);
	}

	private void FillKeyStreamBuffer()
	{
		int read = cbcStream.Read(keyStreamBuffer, 0, keyStreamBuffer.Length);
		// NOTE undocumented feature
		// only works if keyStreamBuffer.Length % blockSize == 0
		if (read != keyStreamBuffer.Length)
			throw new InvalidOperationException("Implementation error: could not read all bytes from CBC stream");
	}

	public override bool CanRead
	{
		get { return mode == CryptoStreamMode.Read; }
	}

	public override bool CanWrite
	{
		get { return mode == CryptoStreamMode.Write; }
	}

	public override void Flush()
	{
		// should never have to be flushed, implementation empty
	}

	public override bool CanSeek
	{
		get { return false; }
	}

	public override long Seek(long offset, System.IO.SeekOrigin origin)
	{
		throw new NotSupportedException();
	}

	public override long Position
	{
		get { throw new NotSupportedException(); }
		set { throw new NotSupportedException(); }
	}

	public override long Length
	{
		get { throw new NotSupportedException(); }
	}

	public override void SetLength(long value)
	{
		throw new NotSupportedException();
	}

}