// SPDX-License-Identifier: LGPL-2.1-only

using System.Numerics;

namespace FLACCL;

internal static class FlakeConstants
{
    internal const int MAX_RICE_PARAM = 14;
    internal const int MAX_PARTITIONS = 256;
    internal static readonly int[] flac_samplerates =
        { 0, 88200, 176400, 192000, 8000, 16000, 22050, 24000, 32000, 44100, 48000, 96000 };
    internal static readonly int[] flac_blocksizes =
        { 0, 192, 576, 1152, 2304, 4608, 0, 0, 256, 512, 1024, 2048, 4096, 8192, 16384 };
    internal static readonly int[] flac_bitdepths = { 0, 8, 12, 0, 16, 20, 24, 32 };
}

internal enum MetadataType { StreamInfo, Padding, Seektable = 3, VorbisComment }
internal enum ChannelMode { NotStereo, LeftRight, LeftSide = 8, RightSide, MidSide }
internal enum SubframeType { Constant, Verbatim, Fixed = 8, LPC = 32 }

[Flags]
internal enum WindowFunction { Welch = 1, Tukey = 2, Hann = 4, Flattop = 8, Bartlett = 16 }

internal struct SeekPoint
{
    internal long number, offset;
    internal int framesize;
}

internal sealed class FlacFrame
{
    internal int blocksize, bs_code0, bs_code1, frame_number, writer_offset;
    internal ChannelMode ch_mode;
    internal readonly FlacSubframeInfo[] subframes;
    internal readonly BitWriter writer;

    internal FlacFrame(int channels, byte[] output)
    {
        subframes = new FlacSubframeInfo[channels];
        for (int ch = 0; ch < channels; ch++)
            subframes[ch] = new FlacSubframeInfo();
        writer = new BitWriter(output, 0, output.Length);
    }

    internal void InitSize(int size)
    {
        blocksize = size;
        int code = Array.IndexOf(FlakeConstants.flac_blocksizes, size);
        bs_code0 = code >= 0 ? code : size <= 256 ? 6 : 7;
        bs_code1 = code >= 0 ? -1 : size - 1;
    }

    internal void SwapSubframes(int a, int b) =>
        (subframes[a], subframes[b]) = (subframes[b], subframes[a]);
}

internal sealed unsafe class FlacSubframeInfo
{
    internal readonly FlacSubframe best = new();
    internal int obits, wbits;
    internal int* samples;

    internal void Init(int* samples, int* residual, int bits, int wastedBits)
    {
        this.samples = samples;
        best.residual = residual;
        obits = bits - wastedBits;
        wbits = wastedBits;
    }
}

internal sealed unsafe class FlacSubframe
{
    internal SubframeType type;
    internal int order, shift, cbits;
    internal int* residual;
    internal uint size;
    internal readonly RiceContext rc = new();
    internal readonly int[] coefs = new int[32];
}

internal sealed class RiceContext
{
    internal int porder, coding_method;
    internal readonly int[] rparams = new int[FlakeConstants.MAX_PARTITIONS];
}

internal static unsafe class AudioSamples
{
    internal const uint UINT32_MAX = uint.MaxValue;

    internal static void MemCpy(byte* destination, byte* source, int count) =>
        new ReadOnlySpan<byte>(source, count).CopyTo(new Span<byte>(destination, count));

    internal static void MemCpy(int* destination, int* source, int count) =>
        new ReadOnlySpan<int>(source, count).CopyTo(new Span<int>(destination, count));
}

internal static class BitReader
{
    internal static int log2i(int value) => BitOperations.Log2((uint)value);
    internal static int log2i(ulong value) => BitOperations.Log2(value);
}

internal sealed class Crc8
{
    private static readonly byte[] Table = MakeTable();

    private static byte[] MakeTable()
    {
        var table = new byte[256];
        for (int i = 0; i < table.Length; i++)
        {
            int crc = i;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc << 1) ^ ((crc & 128) != 0 ? 0x07 : 0);
            table[i] = (byte)crc;
        }
        return table;
    }

    internal byte ComputeChecksum(byte[] bytes, int offset, int length)
    {
        byte crc = 0;
        foreach (byte value in bytes.AsSpan(offset, length))
            crc = Table[crc ^ value];
        return crc;
    }

    internal static byte Update(byte crc, byte value) => Table[crc ^ value];
}

internal static class Crc16
{
    internal static readonly ushort[] table = MakeTable();

    private static ushort[] MakeTable()
    {
        var values = new ushort[256];
        for (int i = 0; i < values.Length; i++)
        {
            int crc = i << 8;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc << 1) ^ ((crc & 32768) != 0 ? 0x8005 : 0);
            values[i] = (ushort)crc;
        }
        return values;
    }
}

internal static unsafe class Lpc
{
    internal static void window_welch(float* window, int length)
    {
        for (int i = 0; i < length; i++)
        {
            double position = i * 2.0 / (length - 1) - 1.0;
            window[i] = (float)(1.0 - position * position);
        }
    }

    internal static void window_bartlett(float* window, int length)
    {
        window_welch(window, length);
        for (int i = 0; i < length; i++)
            window[i] *= window[i];
    }

    internal static void window_flattop(float* window, int length)
    {
        for (int i = 0; i < length; i++)
        {
            double angle = 2.0 * Math.PI * i / (length - 1);
            window[i] = (float)(1.0 - 1.93 * Math.Cos(angle) + 1.29 * Math.Cos(2 * angle)
                - 0.388 * Math.Cos(3 * angle) + 0.0322 * Math.Cos(4 * angle));
        }
    }

    internal static void window_tukey(float* window, int length, double fraction)
    {
        int taper = (int)(fraction * length / 2);
        for (int i = 0; i < length; i++)
        {
            int distance = Math.Min(i, length - i - 1) + 1;
            window[i] = distance < taper ? (float)(0.5 - 0.5 * Math.Cos(Math.PI * distance / taper)) : 1f;
        }
    }

    internal static void window_hann(float* window, int length)
    {
        for (int i = 0; i < length; i++)
            window[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (length - 1)));
    }

    internal static bool EncodeResidual(int* residual, int* samples, int count, int order, int* coefficients, int shift)
    {
        for (int i = order; i < count; i++)
        {
            long prediction = 0;
            for (int j = 0; j < order; j++)
                prediction += (long)coefficients[j] * samples[i - j - 1];
            long value = samples[i] - (prediction >> shift);
            if (value <= int.MinValue || value > int.MaxValue)
                return false;
            residual[i] = (int)value;
        }
        return true;
    }
}
