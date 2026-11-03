using System.Runtime.InteropServices;

namespace UnityEngine
{
    [Serializable]
    [StructLayout(LayoutKind.Sequential, Size = 16, Pack = 8)]
    public struct Hash128 : IComparable, IComparable<Hash128>, IEquatable<Hash128>
    {
        internal ulong u64_0;
        internal ulong u64_1;
        private const ulong kConst = 0xDEADBEEFDEADBEEFUL;

        public bool isValid => u64_0 != 0 || u64_1 != 0;

        public Hash128(uint u32_0, uint u32_1, uint u32_2, uint u32_3)
        {
            u64_0 = (ulong)u32_0 | ((ulong)u32_1 << 32);
            u64_1 = (ulong)u32_2 | ((ulong)u32_3 << 32);
        }

        public Hash128(ulong u64_0, ulong u64_1)
        {
            this.u64_0 = u64_0;
            this.u64_1 = u64_1;
        }

        public int CompareTo(Hash128 rhs)
        {
            if (u64_0 < rhs.u64_0) return -1;
            if (u64_0 > rhs.u64_0) return 1;
            if (u64_1 < rhs.u64_1) return -1;
            if (u64_1 > rhs.u64_1) return 1;
            return 0;
        }

        public int CompareTo(object? obj)
        {
            return obj is Hash128 rhs ? CompareTo(rhs) : 1;
        }

        public override bool Equals(object? obj) => obj is Hash128 other && Equals(other);

        public bool Equals(Hash128 obj) => u64_0 == obj.u64_0 && u64_1 == obj.u64_1;

        public override int GetHashCode()
        {
            return unchecked((int)(u64_0 ^ (u64_0 >> 32) ^ u64_1 ^ (u64_1 >> 32)));
        }

        public static bool operator ==(Hash128 hash1, Hash128 hash2) => hash1.Equals(hash2);
        public static bool operator !=(Hash128 hash1, Hash128 hash2) => !hash1.Equals(hash2);
        public static bool operator <(Hash128 x, Hash128 y) => x.CompareTo(y) < 0;
        public static bool operator >(Hash128 x, Hash128 y) => x.CompareTo(y) > 0;

        public override string ToString() => Hash128ToStringImpl(this);

        public static Hash128 Parse(string hashString)
        {
            Parse_Injected(hashString, out Hash128 result);
            return result;
        }

        private static string Hash128ToStringImpl(Hash128 hash)
        {
            return Hash128ToStringImpl_Injected(ref hash);
        }

        private static string Hash128ToStringImpl_Injected(ref Hash128 hash)
        {
            const string digits = "0123456789abcdef";
            char[] chars = new char[32];
            for (int i = 0; i < 16; i++)
            {
                ulong part = i < 8 ? hash.u64_0 : hash.u64_1;
                int shift = (i & 7) * 8;
                byte value = (byte)(part >> shift);
                chars[i * 2] = digits[value >> 4];
                chars[i * 2 + 1] = digits[value & 15];
            }
            return new string(chars);
        }

        private static void Parse_Injected(string hashString, out Hash128 ret)
        {
            if (hashString == null) 
                throw new ArgumentNullException(nameof(hashString));
            if (hashString.Length != 32) 
                throw new FormatException("A Hash128 must contain 32 hexadecimal characters.");

            ulong first = 0;
            ulong second = 0;
            for (int i = 0; i < 16; i++)
            {
                int hi = HexDigit(hashString[i * 2]);
                int lo = HexDigit(hashString[i * 2 + 1]);
                if ((hi | lo) < 0)
                    throw new FormatException("Hash128 contains a non-hexadecimal character.");

                ulong value = (ulong)((hi << 4) | lo);
                if (i < 8) first |= value << (i * 8);
                else second |= value << ((i - 8) * 8);
            }
            ret = new Hash128(first, second);
        }

        private static int HexDigit(char ch)
        {
            if (ch >= '0' && ch <= '9') return ch - '0';
            if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
            if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
            return -1;
        }

        private static unsafe void ComputeFromPtr(IntPtr data, int start, int count, int elemSize, ref Hash128 hash)
        {
            if (start < 0) 
                throw new ArgumentOutOfRangeException(nameof(start));
            if (count < 0) 
                throw new ArgumentOutOfRangeException(nameof(count));
            if (elemSize < 0) 
                throw new ArgumentOutOfRangeException(nameof(elemSize));

            long offset = checked((long)start * elemSize);
            ulong length = checked((ulong)count * (ulong)elemSize);
            hash.Append((byte*)data.ToPointer() + offset, length);
        }

        public unsafe void Append(void* data, ulong size)
        {
            if (size != 0 && data == null)
                throw new ArgumentNullException(nameof(data));

            ulong first = u64_0;
            ulong second = u64_1;
            SpookyHashV2.Hash(data, size, ref first, ref second);
            u64_0 = first;
            u64_1 = second;
        }
    }

    internal static unsafe class SpookyHashV2
    {
        private const ulong Constant = 0xDEADBEEFDEADBEEFUL;
        private const int BlockSize = 96;
        private const int ShortThreshold = 192;

        private static ulong Rotate(ulong value, int count) => (value << count) | (value >> (64 - count));

        private static ulong Read64(byte* data)
        {
            return (ulong)data[0] | ((ulong)data[1] << 8) |
                   ((ulong)data[2] << 16) | ((ulong)data[3] << 24) |
                   ((ulong)data[4] << 32) | ((ulong)data[5] << 40) |
                   ((ulong)data[6] << 48) | ((ulong)data[7] << 56);
        }

        private static ulong ReadTail(byte* data, int count)
        {
            ulong result = 0;
            for (int i = 0; i < count; i++)
                result |= (ulong)data[i] << (i * 8);
            return result;
        }

        public static void Hash(void* message, ulong length, ref ulong hash1, ref ulong hash2)
        {
            unchecked
            {
                if (length < ShortThreshold)
                {
                    Short((byte*)message, (int)length, ref hash1, ref hash2);
                    return;
                }

                ulong* state = stackalloc ulong[12];
                for (int i = 0; i < 12; i++)
                    state[i] = (i % 3 == 0) ? hash1 : ((i % 3 == 1) ? hash2 : Constant);

                byte* input = (byte*)message;
                ulong wholeBlocks = length / BlockSize;
                ulong* words = stackalloc ulong[12];
                for (ulong i = 0; i < wholeBlocks; i++)
                {
                    for (int j = 0; j < 12; j++)
                        words[j] = Read64(input + j * 8);
                    Mix(words, state);
                    input += BlockSize;
                }

                int remainder = (int)(length % BlockSize);
                byte* tail = stackalloc byte[BlockSize];
                for (int i = 0; i < BlockSize; i++) 
                    tail[i] = 0;
                for (int i = 0; i < remainder; i++) 
                    tail[i] = input[i];
                tail[BlockSize - 1] = (byte)remainder;

                for (int i = 0; i < 12; i++)
                    state[i] += Read64(tail + i * 8);

                EndPartial(state);
                EndPartial(state);
                EndPartial(state);
                hash1 = state[0];
                hash2 = state[1];
            }
        }

        private static void Short(byte* message, int length, ref ulong hash1, ref ulong hash2)
        {
            unchecked
            {
                ulong a = hash1, b = hash2, c = Constant, d = Constant;
                int index = 0;
                int remainder = length % 32;

                if (length > 15)
                {
                    int full = length / 32;
                    for (int i = 0; i < full; i++)
                    {
                        c += Read64(message + index);
                        d += Read64(message + index + 8);
                        ShortMix(ref a, ref b, ref c, ref d);
                        a += Read64(message + index + 16);
                        b += Read64(message + index + 24);
                        index += 32;
                    }

                    if (remainder >= 16)
                    {
                        c += Read64(message + index);
                        d += Read64(message + index + 8);
                        ShortMix(ref a, ref b, ref c, ref d);
                        index += 16;
                        remainder -= 16;
                    }
                }

                d += (ulong)length << 56;
                if (remainder == 0)
                {
                    c += Constant;
                    d += Constant;
                }
                else
                {
                    int cBytes = Math.Min(remainder, 8);
                    c += ReadTail(message + index, cBytes);
                    if (remainder > 8)
                        d += ReadTail(message + index + 8, remainder - 8);
                }

                ShortEnd(ref a, ref b, ref c, ref d);
                hash1 = a;
                hash2 = b;
            }
        }

        private static void ShortMix(ref ulong a, ref ulong b, ref ulong c, ref ulong d)
        {
            unchecked
            {
                c = Rotate(c, 50); c += d; a ^= c;
                d = Rotate(d, 52); d += a; b ^= d;
                a = Rotate(a, 30); a += b; c ^= a;
                b = Rotate(b, 41); b += c; d ^= b;
                c = Rotate(c, 54); c += d; a ^= c;
                d = Rotate(d, 48); d += a; b ^= d;
                a = Rotate(a, 38); a += b; c ^= a;
                b = Rotate(b, 37); b += c; d ^= b;
                c = Rotate(c, 62); c += d; a ^= c;
                d = Rotate(d, 34); d += a; b ^= d;
                a = Rotate(a, 5); a += b; c ^= a;
                b = Rotate(b, 36); b += c; d ^= b;
            }
        }

        private static void ShortEnd(ref ulong a, ref ulong b, ref ulong c, ref ulong d)
        {
            unchecked
            {
                d ^= c; c = Rotate(c, 15); d += c;
                a ^= d; d = Rotate(d, 52); a += d;
                b ^= a; a = Rotate(a, 26); b += a;
                c ^= b; b = Rotate(b, 51); c += b;
                d ^= c; c = Rotate(c, 28); d += c;
                a ^= d; d = Rotate(d, 9); a += d;
                b ^= a; a = Rotate(a, 47); b += a;
                c ^= b; b = Rotate(b, 54); c += b;
                d ^= c; c = Rotate(c, 32); d += c;
                a ^= d; d = Rotate(d, 25); a += d;
                b ^= a; a = Rotate(a, 63); b += a;
            }
        }

        private static void Mix(ulong* data, ulong* s)
        {
            unchecked
            {
                s[0] += data[0]; s[2] ^= s[10]; s[11] ^= s[0]; s[0] = Rotate(s[0], 11); s[11] += s[1];
                s[1] += data[1]; s[3] ^= s[11]; s[0] ^= s[1]; s[1] = Rotate(s[1], 32); s[0] += s[2];
                s[2] += data[2]; s[4] ^= s[0]; s[1] ^= s[2]; s[2] = Rotate(s[2], 43); s[1] += s[3];
                s[3] += data[3]; s[5] ^= s[1]; s[2] ^= s[3]; s[3] = Rotate(s[3], 31); s[2] += s[4];
                s[4] += data[4]; s[6] ^= s[2]; s[3] ^= s[4]; s[4] = Rotate(s[4], 17); s[3] += s[5];
                s[5] += data[5]; s[7] ^= s[3]; s[4] ^= s[5]; s[5] = Rotate(s[5], 28); s[4] += s[6];
                s[6] += data[6]; s[8] ^= s[4]; s[5] ^= s[6]; s[6] = Rotate(s[6], 39); s[5] += s[7];
                s[7] += data[7]; s[9] ^= s[5]; s[6] ^= s[7]; s[7] = Rotate(s[7], 57); s[6] += s[8];
                s[8] += data[8]; s[10] ^= s[6]; s[7] ^= s[8]; s[8] = Rotate(s[8], 55); s[7] += s[9];
                s[9] += data[9]; s[11] ^= s[7]; s[8] ^= s[9]; s[9] = Rotate(s[9], 54); s[8] += s[10];
                s[10] += data[10]; s[0] ^= s[8]; s[9] ^= s[10]; s[10] = Rotate(s[10], 22); s[9] += s[11];
                s[11] += data[11]; s[1] ^= s[9]; s[10] ^= s[11]; s[11] = Rotate(s[11], 46); s[10] += s[0];
            }
        }

        private static void EndPartial(ulong* s)
        {
            unchecked
            {
                s[11] += s[1]; s[2] ^= s[11]; s[1] = Rotate(s[1], 44);
                s[0] += s[2]; s[3] ^= s[0]; s[2] = Rotate(s[2], 15);
                s[1] += s[3]; s[4] ^= s[1]; s[3] = Rotate(s[3], 34);
                s[2] += s[4]; s[5] ^= s[2]; s[4] = Rotate(s[4], 21);
                s[3] += s[5]; s[6] ^= s[3]; s[5] = Rotate(s[5], 38);
                s[4] += s[6]; s[7] ^= s[4]; s[6] = Rotate(s[6], 33);
                s[5] += s[7]; s[8] ^= s[5]; s[7] = Rotate(s[7], 10);
                s[6] += s[8]; s[9] ^= s[6]; s[8] = Rotate(s[8], 13);
                s[7] += s[9]; s[10] ^= s[7]; s[9] = Rotate(s[9], 38);
                s[8] += s[10]; s[11] ^= s[8]; s[10] = Rotate(s[10], 53);
                s[9] += s[11]; s[0] ^= s[9]; s[11] = Rotate(s[11], 42);
                s[10] += s[0]; s[1] ^= s[10]; s[0] = Rotate(s[0], 54);
            }
        }
    }
}
