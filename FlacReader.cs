using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace FLACCL;

internal sealed class FlacReader : IPcmReader
{
    private readonly Stream source;
    private readonly FrameBits bits;
    private readonly byte[] expectedHash;
    private readonly IncrementalHash? hash;
    private readonly long[][] channels;
    private readonly byte[] pcm;
    private int position, length;
    private long decoded;
    private bool finished;

    public PcmFormat Format { get; }
    public long SampleCount { get; }

    internal FlacReader(Stream source)
    {
        this.source = source;
        Span<byte> header = stackalloc byte[4];
        source.ReadExactly(header);
        if ((header[0] & 127) != 0 || MetadataSize(header) != 34)
            throw new InvalidDataException("FLAC must start with STREAMINFO.");

        Span<byte> info = stackalloc byte[34];
        source.ReadExactly(info);
        ulong fields = BinaryPrimitives.ReadUInt64BigEndian(info[10..]);
        int rate = (int)(fields >> 44);
        int channelCount = (int)(fields >> 41 & 7) + 1;
        int sampleBits = (int)(fields >> 36 & 31) + 1;
        if (rate == 0 || sampleBits is not (16 or 24))
            throw new InvalidDataException("FLAC input must use 16 or 24 bits and a valid sample rate.");
        long count = (long)(fields & ((1UL << 36) - 1));
        SampleCount = count == 0 ? -1 : count;
        expectedHash = info[18..34].ToArray();

        uint mask = 0;
        while ((header[0] & 128) == 0)
        {
            source.ReadExactly(header);
            int size = MetadataSize(header);
            if ((header[0] & 127) == 4)
            {
                byte[] comments = new byte[size];
                source.ReadExactly(comments);
                mask = ReadChannelMask(comments, channelCount);
            }
            else
                Skip(size);
        }

        Format = new PcmFormat(sampleBits, channelCount, rate, mask);
        channels = new long[channelCount][];
        for (int channel = 0; channel < channelCount; channel++)
            channels[channel] = new long[65535];
        pcm = new byte[65535 * Format.BlockAlign];
        bits = new FrameBits(source);
        if (expectedHash.Any(value => value != 0))
            hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    }

    public int Read(Span<byte> destination)
    {
        int capacity = destination.Length - destination.Length % Format.BlockAlign;
        int written = 0;
        while (written < capacity)
        {
            if (position == length && !ReadFrame())
                break;
            int count = Math.Min(capacity - written, length - position);
            pcm.AsSpan(position, count).CopyTo(destination[written..]);
            position += count;
            written += count;
        }
        return written;
    }

    private bool ReadFrame()
    {
        if (finished)
            return false;
        if (!bits.Start())
        {
            if (SampleCount >= 0 && SampleCount != decoded)
                throw new InvalidDataException("FLAC sample count differs from STREAMINFO.");
            if (hash is not null && !hash.GetHashAndReset().AsSpan().SequenceEqual(expectedHash))
                throw new InvalidDataException("FLAC audio differs from its STREAMINFO MD5.");
            finished = true;
            return false;
        }
        if (bits.Read(15) != 0x7FFC)
            throw new InvalidDataException("Invalid FLAC frame sync.");
        _ = bits.Read(1);
        int blockCode = (int)bits.Read(4);
        int rateCode = (int)bits.Read(4);
        int stereo = (int)bits.Read(4);
        int depthCode = (int)bits.Read(3);
        int channelCount = stereo < 8 ? stereo + 1 : 2;
        if (stereo > 10 || channelCount != Format.ChannelCount || bits.Read(1) != 0)
            throw new InvalidDataException("Invalid FLAC frame channel assignment.");
        if (depthCode != 0 && FlakeConstants.flac_bitdepths[depthCode] != Format.BitsPerSample)
            throw new InvalidDataException("FLAC frame bit depth differs from STREAMINFO.");
        bits.SkipFrameNumber();

        int samples = blockCode switch
        {
            0 => throw new InvalidDataException("Reserved FLAC block size code."),
            6 => (int)bits.Read(8) + 1,
            7 => (int)bits.Read(16) + 1,
            15 => 32768,
            _ => FlakeConstants.flac_blocksizes[blockCode]
        };
        int rate = rateCode switch
        {
            0 => Format.SampleRate,
            12 => (int)bits.Read(8) * 1000,
            13 => (int)bits.Read(16),
            14 => (int)bits.Read(16) * 10,
            15 => 0,
            _ => FlakeConstants.flac_samplerates[rateCode]
        };
        if (samples > 65535 || rate != Format.SampleRate)
            throw new InvalidDataException("Invalid FLAC frame sample rate or block size.");
        bits.EndHeader();

        for (int channel = 0; channel < channelCount; channel++)
        {
            bool isSide = (stereo is 8 or 10 && channel == 1) || (stereo == 9 && channel == 0);
            ReadSubframe(channels[channel].AsSpan(0, samples), Format.BitsPerSample + (isSide ? 1 : 0));
        }
        bits.EndFrame();

        if (stereo >= 8)
        {
            Span<long> left = channels[0].AsSpan(0, samples);
            Span<long> right = channels[1].AsSpan(0, samples);
            for (int i = 0; i < samples; i++)
            {
                switch (stereo)
                {
                    case 8: right[i] = left[i] - right[i]; break;
                    case 9: left[i] += right[i]; break;
                    case 10:
                        long side = right[i];
                        long mid = (left[i] << 1) | (side & 1);
                        left[i] = (mid + side) >> 1;
                        right[i] = (mid - side) >> 1;
                        break;
                }
            }
        }

        int bytesPerSample = Format.BitsPerSample / 8;
        long limit = 1L << (Format.BitsPerSample - 1);
        int offset = 0;
        for (int sample = 0; sample < samples; sample++)
        {
            foreach (long[] channel in channels)
            {
                long value = channel[sample];
                if (value < -limit || value >= limit)
                    throw new InvalidDataException("Decoded FLAC sample is outside its declared bit depth.");
                pcm[offset++] = (byte)value;
                pcm[offset++] = (byte)(value >> 8);
                if (bytesPerSample == 3)
                    pcm[offset++] = (byte)(value >> 16);
            }
        }
        decoded += samples;
        if (SampleCount >= 0 && decoded > SampleCount)
            throw new InvalidDataException("FLAC contains more samples than STREAMINFO declares.");
        hash?.AppendData(pcm, 0, offset);
        (position, length) = (0, offset);
        return true;
    }

    private void ReadSubframe(Span<long> output, int width)
    {
        if (bits.Read(1) != 0)
            throw new InvalidDataException("Invalid FLAC subframe padding.");
        int type = (int)bits.Read(6);
        int wasted = bits.Read(1) == 0 ? 0 : checked((int)bits.Unary((uint)(width - 2)) + 1);
        width -= wasted;

        if (type == 0)
            output.Fill(bits.Signed(width));
        else if (type == 1)
        {
            for (int i = 0; i < output.Length; i++)
                output[i] = bits.Signed(width);
        }
        else
        {
            Span<int> coefficients = stackalloc int[32];
            bool lpc = type >= 32;
            int order = lpc ? type - 31 : type - 8;
            if ((!lpc && (order is < 0 or > 4)) || order >= output.Length)
                throw new InvalidDataException("Invalid FLAC predictor order.");
            for (int i = 0; i < order; i++)
                output[i] = bits.Signed(width);

            int shift = 0;
            if (lpc)
            {
                int precision = (int)bits.Read(4) + 1;
                shift = bits.Signed(5);
                if (precision == 16 || shift < 0)
                    throw new InvalidDataException("Invalid FLAC LPC precision or shift.");
                for (int i = 0; i < order; i++)
                    coefficients[i] = bits.Signed(precision);
            }
            else
            {
                ReadOnlySpan<int> fixedCoefficients = order switch
                {
                    1 => [1], 2 => [2, -1], 3 => [3, -3, 1], 4 => [4, -6, 4, -1], _ => []
                };
                fixedCoefficients.CopyTo(coefficients);
            }

            int method = (int)bits.Read(2);
            int partitionOrder = (int)bits.Read(4);
            int partitions = 1 << partitionOrder;
            int partitionSize = output.Length / partitions;
            if (method > 1 || output.Length % partitions != 0 || partitionSize < order || partitionSize == 0)
                throw new InvalidDataException("Invalid FLAC Rice partition.");
            int index = order;
            int escape = (1 << (4 + method)) - 1;
            long limit = 1L << (width - 1);
            for (int partition = 0; partition < partitions; partition++)
            {
                int parameter = (int)bits.Read(4 + method);
                bool raw = parameter == escape;
                int rawBits = raw ? (int)bits.Read(5) : 0;
                int end = (partition + 1) * partitionSize;
                for (; index < end; index++)
                {
                    long residual = raw ? bits.Signed(rawBits) : bits.Rice(parameter);
                    long sum = 0;
                    for (int j = 0; j < order; j++)
                        sum += coefficients[j] * output[index - j - 1];
                    long sample = residual + (sum >> shift);
                    if (sample < -limit || sample >= limit)
                        throw new InvalidDataException("FLAC predictor produced a sample outside its subframe width.");
                    output[index] = sample;
                }
            }
        }
        if (wasted != 0)
        {
            for (int i = 0; i < output.Length; i++)
                output[i] <<= wasted;
        }
    }

    private static uint ReadChannelMask(ReadOnlySpan<byte> comments, int channels)
    {
        _ = ReadComment(ref comments);
        uint count = ReadCommentLength(ref comments);
        for (uint i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = ReadComment(ref comments);
            const string name = "WAVEFORMATEXTENSIBLE_CHANNEL_MASK=0x";
            if (entry.Length > name.Length && Encoding.ASCII.GetString(entry[..name.Length]).Equals(name, StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(Encoding.ASCII.GetString(entry[name.Length..]), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint mask)
                && BitOperations.PopCount(mask) == channels)
                return mask;
        }
        return 0;
    }

    private static uint ReadCommentLength(ref ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
            throw new InvalidDataException("Truncated FLAC Vorbis comment.");
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        bytes = bytes[4..];
        return length;
    }

    private static ReadOnlySpan<byte> ReadComment(ref ReadOnlySpan<byte> bytes)
    {
        uint length = ReadCommentLength(ref bytes);
        if (length > bytes.Length)
            throw new InvalidDataException("Invalid FLAC Vorbis comment length.");
        ReadOnlySpan<byte> field = bytes[..(int)length];
        bytes = bytes[(int)length..];
        return field;
    }

    private void Skip(int bytes)
    {
        Span<byte> scratch = stackalloc byte[4096];
        while (bytes > 0)
        {
            int count = Math.Min(bytes, scratch.Length);
            source.ReadExactly(scratch[..count]);
            bytes -= count;
        }
    }

    private static int MetadataSize(ReadOnlySpan<byte> header) => (header[1] << 16) | (header[2] << 8) | header[3];

    public void Dispose()
    {
        hash?.Dispose();
        source.Dispose();
    }

    private sealed class FrameBits(Stream stream)
    {
        private uint buffer;
        private int available;
        private byte crc8;
        private ushort crc16;

        internal bool Start()
        {
            int value = stream.ReadByte();
            if (value < 0)
                return false;
            crc8 = 0;
            crc16 = 0;
            Load((byte)value);
            return true;
        }

        private void Load(byte value)
        {
            buffer = value;
            available = 8;
            crc8 = Crc8.Update(crc8, value);
            crc16 = (ushort)((crc16 << 8) ^ Crc16.table[(crc16 >> 8) ^ value]);
        }

        internal uint Read(int count)
        {
            uint value = 0;
            while (count != 0)
            {
                if (available == 0)
                {
                    int next = stream.ReadByte();
                    if (next < 0)
                        throw new EndOfStreamException("Truncated FLAC frame.");
                    Load((byte)next);
                }
                int take = Math.Min(count, available);
                available -= take;
                value = (value << take) | ((buffer >> available) & ((1u << take) - 1));
                count -= take;
            }
            return value;
        }

        internal int Signed(int count)
        {
            if (count == 0)
                return 0;
            return ((int)Read(count) << (32 - count)) >> (32 - count);
        }

        internal uint Unary(uint maximum)
        {
            uint count = 0;
            while (Read(1) == 0)
            {
                if (count == maximum)
                    throw new InvalidDataException("Invalid FLAC unary code.");
                count++;
            }
            return count;
        }

        internal long Rice(int parameter)
        {
            uint folded = (Unary(uint.MaxValue >> parameter) << parameter) | Read(parameter);
            return (long)(folded >> 1) ^ -(long)(folded & 1);
        }

        internal void SkipFrameNumber()
        {
            byte first = (byte)Read(8);
            if (first < 128)
                return;
            int length = BitOperations.LeadingZeroCount((uint)(byte)~first) - 24;
            if (length is < 2 or > 7)
                throw new InvalidDataException("Invalid FLAC frame number.");
            for (int i = 1; i < length; i++)
            {
                if ((Read(8) & 0xC0) != 0x80)
                    throw new InvalidDataException("Invalid FLAC frame number continuation.");
            }
        }

        internal void EndHeader()
        {
            _ = Read(8);
            if (crc8 != 0)
                throw new InvalidDataException("FLAC frame header CRC-8 mismatch.");
        }

        internal void EndFrame()
        {
            if (Read(available) != 0)
                throw new InvalidDataException("Invalid FLAC frame padding.");
            _ = Read(16);
            if (crc16 != 0)
                throw new InvalidDataException("FLAC frame CRC-16 mismatch.");
        }
    }
}
