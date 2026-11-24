// SPDX-License-Identifier: LGPL-2.1-only

using System.Buffers.Binary;
using System.Numerics;

namespace FLACCL;

internal interface IPcmReader : IDisposable
{
    PcmFormat Format { get; }
    long SampleCount { get; }
    int Read(Span<byte> destination);
}

internal sealed class WaveReader : IPcmReader
{
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private readonly Stream source;
    private long remaining;

    public PcmFormat Format { get; }
    public long SampleCount { get; }

    internal WaveReader(Stream source, ReadOnlySpan<byte> signature)
    {
        this.source = source;
        Span<byte> header = stackalloc byte[12];
        signature.CopyTo(header);
        source.ReadExactly(header[4..]);
        bool rf64 = header[..4].SequenceEqual("RF64"u8);
        if ((!rf64 && !header[..4].SequenceEqual("RIFF"u8)) || !header[8..].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Expected a RIFF or RF64 WAVE file.");

        ulong containerRemaining = rf64 ? ulong.MaxValue : U32(header[4..]);
        if (containerRemaining < 4)
            throw new InvalidDataException("Invalid WAVE container size.");
        containerRemaining -= 4;
        ulong? rf64DataLength = null;
        PcmFormat? format = null;
        Span<byte> formatBytes = stackalloc byte[40];
        Span<byte> sizeBytes = stackalloc byte[28];
        while (containerRemaining >= 8)
        {
            source.ReadExactly(header[..8]);
            containerRemaining -= 8;
            uint size32 = U32(header[4..]);
            ulong size = size32;
            bool audio = header[..4].SequenceEqual("data"u8);
            if (audio && rf64 && size32 == uint.MaxValue)
                size = rf64DataLength ?? throw new InvalidDataException("RF64 data precedes its ds64 chunk.");
            ulong paddedSize = checked(size + (size & 1));
            if ((audio ? size : paddedSize) > containerRemaining)
                throw new InvalidDataException("WAVE chunk exceeds the container size.");

            if (audio)
            {
                Format = format ?? throw new InvalidDataException("WAVE data precedes its format chunk.");
                if (size > long.MaxValue || size % (uint)Format.BlockAlign != 0)
                    throw new InvalidDataException("WAVE data length must contain whole PCM samples.");
                if (source.CanSeek && size > (ulong)(source.Length - source.Position))
                    throw new EndOfStreamException("WAVE data is truncated.");
                remaining = (long)size;
                SampleCount = remaining / Format.BlockAlign;
                return;
            }

            if (rf64 && header[..4].SequenceEqual("ds64"u8))
            {
                if (size < 28 || rf64DataLength is not null)
                    throw new InvalidDataException("Invalid RF64 ds64 chunk.");
                source.ReadExactly(sizeBytes);
                ulong riffSize = BinaryPrimitives.ReadUInt64LittleEndian(sizeBytes);
                if (riffSize < 12 + paddedSize)
                    throw new InvalidDataException("Invalid RF64 container size.");
                containerRemaining = riffSize - 12;
                rf64DataLength = BinaryPrimitives.ReadUInt64LittleEndian(sizeBytes[8..]);
                Skip(paddedSize - 28);
            }
            else if (header[..4].SequenceEqual("fmt "u8))
            {
                if (size < 16 || format is not null)
                    throw new InvalidDataException("Invalid WAVE format chunk.");
                int read = (int)Math.Min(size, (ulong)formatBytes.Length);
                source.ReadExactly(formatBytes[..read]);
                format = ParseFormat(formatBytes[..read]);
                Skip(paddedSize - (uint)read);
            }
            else
                Skip(paddedSize);
            containerRemaining -= paddedSize;
        }
        throw new InvalidDataException("WAVE file has no data chunk.");
    }

    public int Read(Span<byte> destination)
    {
        int count = (int)Math.Min(remaining, destination.Length - destination.Length % Format.BlockAlign);
        source.ReadExactly(destination[..count]);
        remaining -= count;
        return count;
    }

    public void Dispose() => source.Dispose();

    private static PcmFormat ParseFormat(ReadOnlySpan<byte> bytes)
    {
        int tag = U16(bytes);
        int channels = U16(bytes[2..]);
        uint rate = U32(bytes[4..]);
        int align = U16(bytes[12..]);
        int bits = U16(bytes[14..]);
        uint mask = 0;
        if (tag == 0xFFFE)
        {
            if (bytes.Length < 40 || U16(bytes[16..]) < 22 ||
                U16(bytes[18..]) != bits || new Guid(bytes[24..40]) != PcmSubFormat)
                throw new InvalidDataException("Expected WAVE extensible integer PCM with matching container and sample widths.");
            mask = U32(bytes[20..]);
            if (mask != 0 && BitOperations.PopCount(mask) != channels)
                throw new InvalidDataException("WAVE channel mask does not match the channel count.");
        }
        else if (tag != 1)
            throw new InvalidDataException("Expected uncompressed integer PCM in the WAVE file.");

        if (bits is not (16 or 24) || channels is < 1 or > 8 || align != channels * (bits / 8)
            || rate is < 1 or > 1048575)
            throw new InvalidDataException("Expected 16-bit or 24-bit WAVE PCM with 1–8 channels and a FLAC-compatible sample rate.");
        return new PcmFormat(bits, channels, (int)rate, mask);
    }

    private void Skip(ulong bytes)
    {
        if (source.CanSeek)
        {
            if (bytes > (ulong)(source.Length - source.Position))
                throw new EndOfStreamException("WAVE chunk is truncated.");
            source.Seek((long)bytes, SeekOrigin.Current);
            return;
        }
        Span<byte> scratch = stackalloc byte[4096];
        while (bytes != 0)
        {
            int count = (int)Math.Min(bytes, (ulong)scratch.Length);
            source.ReadExactly(scratch[..count]);
            bytes -= (uint)count;
        }
    }

    private static uint U32(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt32LittleEndian(value);
    private static ushort U16(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt16LittleEndian(value);
}
