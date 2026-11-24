namespace FLACCL;

internal readonly record struct PcmFormat(int BitsPerSample, int ChannelCount, int SampleRate, uint ChannelMask = 0)
{
    internal int BlockAlign => ChannelCount * (BitsPerSample / 8);
}

internal sealed record EncoderSettings(PcmFormat PCM, Device Device, int Level)
{
    internal bool AllowNonSubset { get; init; }
    internal bool DoMD5 { get; init; } = true;
    internal bool DoSeekTable { get; init; } = true;
    internal bool GpuPartitions { get; init; } = true;
    internal int Padding { get; init; } = 4096;
    internal int BlockSize { get; init; }
    internal int MinFixedOrder => Level switch { 0 => 3, 8 => 0, _ => 2 };
    internal int MaxFixedOrder => Level == 8 ? 4 : 2;
    internal int MaxLPCOrder => Level switch { <= 1 => 7, <= 5 => 8, <= 8 => 12, _ => 32 };
    internal int MinPartitionOrder => 0;
    internal int MaxPartitionOrder => 8;

    internal void Validate()
    {
        if (PCM.BitsPerSample is not (16 or 24) || PCM.ChannelCount is < 1 or > 8)
            throw new InvalidDataException("FLACCL supports 16-bit and 24-bit integer PCM with 1–8 channels.");
        if (PCM.SampleRate is < 1 or > 1048575)
            throw new InvalidDataException("The sample rate is outside the FLAC range.");
        if (Level is < 0 or > 11)
            throw new ArgumentException("Compression level must be between 0 and 11.");
        if (!AllowNonSubset && Level > 8 && PCM.SampleRate <= 48000)
            throw new ArgumentException("Levels 9–11 at 48 kHz or below require --lax.");
        if (!AllowNonSubset && PCM.SampleRate > 655350)
            throw new ArgumentException("Sample rates above 655350 Hz require --lax.");
        if (BlockSize != 0 && BlockSize is < 256 or > 16384)
            throw new ArgumentException("Block size must be between 256 and 16384.");
        if (!AllowNonSubset && PCM.SampleRate <= 48000 && BlockSize > 4608)
            throw new ArgumentException("Block sizes above 4608 at 48 kHz or below require --lax.");
        if (Padding is < 0 or > 0xFFFFFF)
            throw new ArgumentException("Padding must be between 0 and 16777215 bytes.");
        if (!Device.IsCpu && Device.MaxWorkGroupSize < 128)
            throw new OpenClException($"{Device.Name} does not support the required 128-work-item groups.");
    }
}
