// SPDX-License-Identifier: LGPL-2.1-only

using System.Diagnostics;
using System.Globalization;

namespace FLACCL;

internal static class Program
{
    private const string Usage = """
        Usage: FLACCL [options] input.wav|input.flac
               FLACCL --list-devices

          -o, --output PATH    Output FLAC path (required for FLAC input or stdin)
          -0 through -11       Compression level (default: 8)
          --level N            Compression level, same as -N
          --device N           OpenCL device index from --list-devices (default: first GPU)
          -b, --blocksize N    FLAC frame size in samples
          -p, --padding N      Metadata padding in bytes (default: 4096)
          --lax                Allow FLAC settings outside the streamable subset
          --cpu-rice           Run residual and Rice partition calculation on the host
          --no-md5             Omit the STREAMINFO audio checksum
          --no-seektable       Omit the seek table
          -f, --force          Replace an existing output file
          -q, --quiet          Suppress encoding status
          -h, --help           Show this help

        Use "-" for stdin or FLAC stdout.
        Input formats: RIFF/RF64 WAVE or native FLAC, 16 or 24 bits, 1–8 channels.
        """;

    private static int Main(string[] args)
    {
        try
        {
            if (args is ["--help" or "-h" or "-?"])
            {
                Console.WriteLine(Usage);
                return 0;
            }
            if (args is ["--version"])
            {
                Console.WriteLine("FLACCL 1.0");
                return 0;
            }
            if (args is ["--list-devices"])
                return ListDevices();

            var options = Parse(args);
            Encode(options);
            return 0;
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine($"FLACCL: {error.Message}");
            Console.Error.WriteLine("Run FLACCL --help for usage.");
            return 2;
        }
        catch (Exception error) when (error is OpenClException or IOException or UnauthorizedAccessException
            or InvalidDataException or OverflowException or InvalidOperationException)
        {
            Console.Error.WriteLine($"FLACCL: {error.Message}");
            return 1;
        }
    }

    private static int ListDevices()
    {
        var devices = Cl.GetDevices();
        for (int i = 0; i < devices.Count; i++)
        {
            Device device = devices[i];
            string kind = device.IsGpu ? "GPU" : device.IsCpu ? "CPU" : "other";
            Console.WriteLine($"[{i}] {device.Name} ({kind}) | {device.Platform} | {device.Version}");
        }
        if (devices.Count == 0)
        {
            Console.Error.WriteLine("No OpenCL devices with an online compiler were found.");
            return 1;
        }
        return 0;
    }

    private static Device SelectDevice(int? index)
    {
        var devices = Cl.GetDevices();
        if (index is int explicitIndex)
        {
            if ((uint)explicitIndex >= (uint)devices.Count)
                throw new ArgumentException($"OpenCL device {explicitIndex} was not found. Run --list-devices.");
            Device device = devices[explicitIndex];
            if (!device.IsGpu && !device.IsCpu)
                throw new ArgumentException($"OpenCL device {explicitIndex} is neither a GPU nor a CPU.");
            return device;
        }

        return devices.Find(device => device.IsGpu && device.MaxWorkGroupSize >= 128)
            ?? throw new OpenClException("No compatible OpenCL GPU was found. Run --list-devices to see available devices.");
    }

    private static void Encode(Options options)
    {
        string outputPath = options.Output;
        bool stdout = outputPath == "-";
        if (!stdout)
        {
            outputPath = Path.GetFullPath(outputPath);
            StringComparison pathComparison = OperatingSystem.IsLinux()
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            if (options.Input != "-" && string.Equals(Path.GetFullPath(options.Input), outputPath, pathComparison))
                throw new ArgumentException("Input and output paths must be different.");
            if (!options.Force && File.Exists(outputPath))
                throw new IOException($"Output already exists: {outputPath}. Use --force to replace it.");
        }

        using IPcmReader input = OpenInput(options.Input);
        Device device = SelectDevice(options.Device);
        var settings = new EncoderSettings(input.Format, device, options.Level)
        {
            AllowNonSubset = options.Lax,
            BlockSize = options.BlockSize,
            Padding = options.Padding,
            DoMD5 = options.DoMD5,
            DoSeekTable = options.DoSeekTable,
            GpuPartitions = options.GpuPartitions
        };
        settings.Validate();

        if (!options.Quiet)
            Console.Error.WriteLine($"OpenCL: {device.Name} | {input.Format.SampleRate} Hz, {input.Format.BitsPerSample}-bit, {input.Format.ChannelCount} channel(s) | level {settings.Level}");
        var timer = Stopwatch.StartNew();

        if (stdout)
        {
            using Stream output = Console.OpenStandardOutput();
            WriteAudio(input, settings, output);
        }
        else
        {
            string tempPath = Path.Combine(Path.GetDirectoryName(outputPath)!, $".flaccl-{Guid.NewGuid():N}.tmp");
            bool tempCreated = false;
            try
            {
                using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    tempCreated = true;
                    WriteAudio(input, settings, output);
                }
                File.Move(tempPath, outputPath, options.Force);
            }
            finally
            {
                if (tempCreated)
                    File.Delete(tempPath);
            }
        }

        if (!options.Quiet)
            Console.Error.WriteLine($"Encoding completed in {timer.Elapsed.TotalSeconds:F2} s.");
    }

    private static IPcmReader OpenInput(string path)
    {
        Stream source = path == "-" ? new BufferedStream(Console.OpenStandardInput()) : File.OpenRead(path);
        try
        {
            Span<byte> signature = stackalloc byte[4];
            source.ReadExactly(signature);
            return signature.SequenceEqual("fLaC"u8) ? new FlacReader(source) : new WaveReader(source, signature);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static void WriteAudio(IPcmReader input, EncoderSettings settings, Stream output)
    {
        using var encoder = new AudioEncoder(settings, output, input.SampleCount);
        var buffer = new byte[settings.PCM.BlockAlign * 16384];
        while (input.Read(buffer) is int count and > 0)
            encoder.Write(buffer.AsSpan(0, count));
        encoder.Complete();
        output.Flush();
    }

    private static Options Parse(string[] args)
    {
        string? input = null, output = null;
        int? device = null;
        int level = 8, blockSize = 0, padding = 4096;
        bool lax = false, force = false, quiet = false, md5 = true, seekTable = true, gpuPartitions = true;
        bool positionalOnly = false;

        for (int i = 0; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException("An option is missing its value.");
            string arg = args[i];
            if (!positionalOnly && arg.Length > 1 && arg[0] == '-')
            {
                switch (arg)
                {
                    case "--": positionalOnly = true; break;
                    case "-o": case "--output": output = Next(); break;
                    case "--level": level = ReadInt(Next(), arg, 0, 11); break;
                    case "--device": device = ReadInt(Next(), arg, 0, int.MaxValue); break;
                    case "-b": case "--blocksize": blockSize = ReadInt(Next(), arg, 256, 16384); break;
                    case "-p": case "--padding": padding = ReadInt(Next(), arg, 0, 0xFFFFFF); break;
                    case "--lax": lax = true; break;
                    case "--cpu-rice": gpuPartitions = false; break;
                    case "--no-md5": md5 = false; break;
                    case "--no-seektable": seekTable = false; break;
                    case "-f": case "--force": force = true; break;
                    case "-q": case "--quiet": quiet = true; break;
                    default:
                        if (int.TryParse(arg.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int preset)
                            && preset is >= 0 and <= 11)
                            level = preset;
                        else
                            throw new ArgumentException($"Unknown option {arg}.");
                        break;
                }
            }
            else if (input is null && !string.IsNullOrWhiteSpace(arg))
                input = arg;
            else
                throw new ArgumentException("Specify exactly one audio input.");
        }

        if (input is null)
            throw new ArgumentException("Specify a WAVE or FLAC input.");
        if (output is null && (input == "-" || Path.GetExtension(input).Equals(".flac", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Specify -o for FLAC input or stdin.");
        output ??= Path.ChangeExtension(input, ".flac");
        return new(input, output, device, level, blockSize, padding, lax, force, quiet, md5, seekTable, gpuPartitions);
    }

    private static int ReadInt(string value, string option, int minimum, int maximum) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed >= minimum && parsed <= maximum
            ? parsed : throw new ArgumentException($"{option} expects an integer between {minimum} and {maximum}.");

    private sealed record Options(string Input, string Output, int? Device, int Level, int BlockSize, int Padding,
        bool Lax, bool Force, bool Quiet, bool DoMD5, bool DoSeekTable, bool GpuPartitions);
}
