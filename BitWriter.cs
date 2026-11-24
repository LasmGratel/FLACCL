// Bit writer adapted from CUETools.Codecs (Gregory S. Chudov, LGPL-2.1-or-later).
using System;

namespace FLACCL
{
    internal sealed class BitWriter
    {
        private ushort crc16_m;
        private ulong bit_buf_m;
        private int bit_left_m;
        private readonly byte[] buffer;
        private readonly int buf_start, buf_end;
        private int buf_ptr_m;

        public byte[] Buffer
        {
            get
            {
                return buffer;
            }
        }

        public int Length => buf_ptr_m - buf_start;

        public ushort get_crc16()
        {
            return crc16_m;
        }

        public BitWriter(byte[] buf, int pos, int len)
        {
            buffer = buf;
            buf_start = pos;
            buf_ptr_m = pos;
            buf_end = pos + len;
            bit_left_m = 64;
            bit_buf_m = 0;
            crc16_m = 0;
        }

        public void Reset()
        {
            buf_ptr_m = buf_start;
            bit_left_m = 64;
            bit_buf_m = 0;
            crc16_m = 0;
        }

        public void writebits_signed(int bits, int val)
        {
            writebits(bits, val & ((1 << bits) - 1));
        }

        public void writebits(int bits, int val)
        {
            writebits(bits, (ulong)val);
        }

        public void writebits(int bits, uint val)
        {
            writebits(bits, (ulong)val);
        }

        public void writebits(int bits, ulong val)
        {
            if (bits == 0)
                return;
            if (bits < bit_left_m)
            {
                bit_left_m -= bits;
                bit_buf_m |= val << bit_left_m;
            }
            else
            {
                ulong bb = bit_buf_m | (val >> (bits - bit_left_m));
                if (buffer != null)
                {
                    if (buf_ptr_m + 8 > buf_end)
                    {
                        throw new InvalidOperationException("Encoded FLAC frame exceeds the output buffer.");
                    }

                    crc16_m = (ushort)((crc16_m << 8) ^ Crc16.table[(crc16_m >> 8) ^ (byte)(bb >> 56)]);
                    crc16_m = (ushort)((crc16_m << 8) ^ Crc16.table[(crc16_m >> 8) ^ (byte)(bb >> 48)]);
                    crc16_m = (ushort)((crc16_m << 8) ^ Crc16.table[(crc16_m >> 8) ^ (byte)(bb >> 40)]);
                    crc16_m = (ushort)((crc16_m << 8) ^ Crc16.table[(crc16_m >> 8) ^ (byte)(bb >> 32)]);
                    crc16_m = (ushort)((crc16_m << 8) ^ Crc16.table[(crc16_m >> 8) ^ (byte)(bb >> 24)]);
                    crc16_m = (ushort)((crc16_m << 8) ^ Crc16.table[(crc16_m >> 8) ^ (byte)(bb >> 16)]);
                    crc16_m = (ushort)((crc16_m << 8) ^ Crc16.table[(crc16_m >> 8) ^ (byte)(bb >>  8)]);
                    crc16_m = (ushort)((crc16_m << 8) ^ Crc16.table[(crc16_m >> 8) ^ (byte)(bb      )]);

                    buffer[buf_ptr_m + 7] = (byte)(bb & 0xFF); bb >>= 8;
                    buffer[buf_ptr_m + 6] = (byte)(bb & 0xFF); bb >>= 8;
                    buffer[buf_ptr_m + 5] = (byte)(bb & 0xFF); bb >>= 8;
                    buffer[buf_ptr_m + 4] = (byte)(bb & 0xFF); bb >>= 8;
                    buffer[buf_ptr_m + 3] = (byte)(bb & 0xFF); bb >>= 8;
                    buffer[buf_ptr_m + 2] = (byte)(bb & 0xFF); bb >>= 8;
                    buffer[buf_ptr_m + 1] = (byte)(bb & 0xFF); bb >>= 8;
                    buffer[buf_ptr_m + 0] = (byte)(bb & 0xFF);
                    buf_ptr_m += 8;
                }
                // cannot do this in one shift, because bit_left_m can be 64,
                // 
                bit_left_m += 64 - bits;
                bit_buf_m = bit_left_m == 64 ? 0 : val << bit_left_m;
            }
        }

        /// <summary>
        /// Assumes there's enough space, buffer != null and bits is in range 1..31
        /// </summary>
        /// <param name="bits"></param>
        /// <param name="val"></param>
//        unsafe void writebits_fast(int bits, uint val, ref byte* buf)
//        {
//#if DEBUG
//            if ((buf_ptr + 3) >= buf_end)
//            {
//                eof = true;
//                return;
//            }
//#endif
//            if (bits < bit_left)
//            {
//                bit_buf = (bit_buf << bits) | val;
//                bit_left -= bits;
//            }
//            else
//            {
//                uint bb = (bit_buf << bit_left) | (val >> (bits - bit_left));
//                bit_left += (32 - bits);

//                *(buf++) = (byte)(bb >> 24);
//                *(buf++) = (byte)(bb >> 16);
//                *(buf++) = (byte)(bb >> 8);
//                *(buf++) = (byte)(bb);

//                bit_buf = val;
//            }
//        }

        public void write_utf8(int val)
        {
            write_utf8((uint)val);
        }

        public void write_utf8(uint val)
        {
            if (val < 0x80)
            {
                writebits(8, val);
                return;
            }
            int bytes = (System.Numerics.BitOperations.Log2(val) + 4) / 5;
            int shift = (bytes - 1) * 6;
            writebits(8, (256U - (256U >> bytes)) | (val >> shift));
            while (shift >= 6)
            {
                shift -= 6;
                writebits(8, 0x80 | ((val >> shift) & 0x3F));
            }
        }

        public unsafe void write_rice_block_signed(byte* fixedbuf, int k, int* residual, int count)
        {
            byte* buf = &fixedbuf[buf_ptr_m];
            ulong bit_buf = bit_buf_m;
            int bit_left = bit_left_m;
            ushort crc16 = crc16_m;
            fixed (ushort *crc16_t = Crc16.table)
            for (int i = count; i > 0; i--)
            {
                int vi = *(residual++);
                uint v = (uint)((vi << 1) ^ (vi >> 31));

                // write quotient in unary
                long remaining = (long)(v >> k) + k + 1;
                while (remaining > 64)
                {
                    if (buf >= fixedbuf + buf_end)
                        throw new InvalidOperationException("Encoded FLAC Rice code exceeds the output buffer.");
                    crc16 = (ushort)((crc16 << 8) ^ crc16_t[(crc16 >> 8) ^ (*(buf++) = (byte)(bit_buf >> 56))]);
                    bit_buf <<= 8;
                    remaining -= 8;
                }

                // write remainder in binary using 'k' bits
                int bits = (int)remaining;
                ulong val = (uint)((v & ((1U << k) - 1)) | (1U << k));
                if (bits < bit_left)
                {
                    bit_left -= bits;
                    bit_buf |= val << bit_left;
                }
                else
                {
                    ulong bb = bit_buf | (val >> (bits - bit_left));
                    if (buf + 8 > fixedbuf + buf_end)
                        throw new InvalidOperationException("Encoded FLAC Rice code exceeds the output buffer.");

                    crc16 = (ushort)((crc16 << 8) ^ crc16_t[(crc16 >> 8) ^ (*(buf++) = (byte)(bb >> 56))]);
                    crc16 = (ushort)((crc16 << 8) ^ crc16_t[(crc16 >> 8) ^ (*(buf++) = (byte)(bb >> 48))]);
                    crc16 = (ushort)((crc16 << 8) ^ crc16_t[(crc16 >> 8) ^ (*(buf++) = (byte)(bb >> 40))]);
                    crc16 = (ushort)((crc16 << 8) ^ crc16_t[(crc16 >> 8) ^ (*(buf++) = (byte)(bb >> 32))]);
                    crc16 = (ushort)((crc16 << 8) ^ crc16_t[(crc16 >> 8) ^ (*(buf++) = (byte)(bb >> 24))]);
                    crc16 = (ushort)((crc16 << 8) ^ crc16_t[(crc16 >> 8) ^ (*(buf++) = (byte)(bb >> 16))]);
                    crc16 = (ushort)((crc16 << 8) ^ crc16_t[(crc16 >> 8) ^ (*(buf++) = (byte)(bb >> 8))]);
                    crc16 = (ushort)((crc16 << 8) ^ crc16_t[(crc16 >> 8) ^ (*(buf++) = (byte)(bb))]);

                    bit_left += 64 - bits;
                    bit_buf = (val << bit_left - 1) << 1;
                }
            }
            crc16_m = crc16;
            buf_ptr_m = (int)(buf - fixedbuf);
            bit_buf_m = bit_buf;
            bit_left_m = bit_left;
        }

        public void flush()
        {
            while (bit_left_m < 64)
            {
                if (buf_ptr_m >= buf_end)
                {
                    throw new InvalidOperationException("Encoded FLAC frame exceeds the output buffer.");
                }
                if (buffer != null)
                {
                    byte b = (byte)(bit_buf_m >> 56);
                    crc16_m = (ushort)((crc16_m << 8) ^ Crc16.table[(crc16_m >> 8) ^ b]);
                    buffer[buf_ptr_m] = b;
                }
                buf_ptr_m++;
                bit_buf_m <<= 8;
                bit_left_m += 8;
            }
            bit_left_m = 64;
            bit_buf_m = 0;
        }
    }
}
