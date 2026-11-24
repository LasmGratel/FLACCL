# FLACCL

FLACCL is a standalone, OpenCL-accelerated lossless audio encoder that converts WAV to FLAC and re-encodes existing FLAC files. It uses **.NET 10, C# 14, and NativeAOT** to produce a single executable per target platform. The executable embeds the OpenCL kernel, and command-line options supply all encoder settings. The CUETools suite and a JSON configuration file are unnecessary.

- [Quick start](#quick-start)
- [Using FLACCL with foobar2000](#using-flaccl-with-foobar2000)
- [Input and output](#input-and-output)
- [Command-line options](#command-line-options)
- [Building from source](#building-from-source)
- [Troubleshooting](#troubleshooting)
- [Credits](#credits)
- [License](#license)

## Quick start

Use the executable for your operating system and architecture. With an OpenCL driver installed, list the available devices and encode a WAV file.

**Windows PowerShell**

```powershell
.\FLACCL.exe --list-devices
.\FLACCL.exe -8 -o "output.flac" "input.wav"
```

**Linux and macOS**

```sh
./FLACCL --list-devices
./FLACCL -8 -o "output.flac" "input.wav"
```

FLACCL defaults to compression level `8` and selects the first compatible OpenCL GPU. Use `--device N` to select a specific device by its index in the list. You can also select an OpenCL CPU device explicitly if your driver provides one.

The selected OpenCL device analyzes predictors and evaluates candidates. It also computes residuals and Rice partitions when it supports the required OpenCL extension. Use `--cpu-rice` to run those two stages on the host CPU.

## Using FLACCL with foobar2000

These steps apply to foobar2000 for Windows with the **Converter** component installed. Put `FLACCL.exe` in a permanent location such as `C:\Tools\FLACCL\FLACCL.exe` and run it with `--list-devices` to check that your OpenCL device is available.

1. Select tracks in a playlist, right-click, and choose **Convert → …** to open **Converter Setup**.
2. Open **Output format**, click **Add New**, and select **Custom** in the **Encoder** list.
3. Enter the following settings.

   | Field | Value |
   | --- | --- |
   | **Encoder file** | Browse to `FLACCL.exe` |
   | **Extension** | `flac` |
   | **Parameters** | `-8 -o %d %s` |
   | **Format is** | **lossless (or hybrid)** |
   | **Highest BPS mode supported** | `24` |
   | **Encoder name** | `FLACCL` |
   | **Settings** | `Level 8` |

4. Click **OK** and select the new **FLACCL** output format. Leave **Output bit depth** on **Auto** for 16-bit or 24-bit sources. If foobar2000 selects a different bit depth, set it to **16** or **24** explicitly.
5. Return to **Converter Setup** and choose an output location under **Destination**. To copy tags and cover art, enable **Transfer metadata (tags)** and **Transfer attached pictures** under **Other**. Click **Convert** to start or **Save Preset** to reuse these settings later.

The `%s` placeholder tells foobar2000 to create a temporary WAV with a known data length. The `%d` placeholder supplies the destination FLAC path. To pin the preset to a device, use `--device 0 -8 -o %d %s` and replace `0` with the index reported by `--list-devices`.

## Input and output

| Item | Supported formats |
| --- | --- |
| WAV input | RIFF and RF64, including WAVEFORMATEXTENSIBLE with matching container and sample widths, using 16-bit or 24-bit integer PCM |
| FLAC input | Native FLAC with 16-bit or 24-bit samples |
| Channels | 1–8 |
| Output | Native FLAC with the input sample rate, bit depth, channel count, and channel mask when available |

If you omit `-o` for a WAV file, FLACCL writes a file with the same base name and a `.flac` extension next to the input. For FLAC re-encoding, specify a different output path. FLACCL generates new stream metadata when it re-encodes. The calling application can copy tags and cover art.

```powershell
.\FLACCL.exe "input.wav"
.\FLACCL.exe -8 -o "recompressed.flac" "input.flac"
.\FLACCL.exe --device 0 --lax -10 -o "level10.flac" "input.wav"
```

Use `-` for standard input or standard output. When reading from standard input, always specify `-o`. A WAV input stream needs valid RIFF or RF64 data lengths. A FLAC input stream can have an unknown total sample count.

```sh
./FLACCL -8 -o output.flac - < input.wav
./FLACCL -8 -o - input.wav > output.flac
```

For file output, FLACCL writes the audio MD5 in STREAMINFO by default and creates a seek table when it knows the input has a positive sample count. It omits both on standard output because the encoder cannot seek back to update the header. Use `--force` to replace an existing output file.

## Command-line options

| Option | Description |
| --- | --- |
| `--list-devices` | List available OpenCL devices, their indices, and their platforms |
| `--device N` | Select an OpenCL device by index. The default is the first compatible GPU |
| `-0` through `-11`, `--level N` | Set the compression level. The default is `8` |
| `-o PATH`, `--output PATH` | Set the output path. Use `-` for standard output |
| `-b N`, `--blocksize N` | Set the number of samples per FLAC frame, from `256` to `16384`. The default depends on the sample rate |
| `-p N`, `--padding N` | Set the metadata padding in bytes. The default is `4096`. Set it to `0` to omit padding |
| `--lax` | Allow settings outside the FLAC Streamable Subset |
| `--cpu-rice` | Compute residuals and Rice partitions on the host CPU |
| `--no-md5` | Omit the STREAMINFO audio MD5 |
| `--no-seektable` | Omit the seek table |
| `-f`, `--force` | Replace an existing output file |
| `-q`, `--quiet` | Suppress encoding status messages. Errors remain visible |
| `-h`, `--help` | Show command-line help |
| `--version` | Show the version |

At sample rates of 48 kHz or below, levels `9–11` and explicit block sizes above `4608` require `--lax`. Compression levels change the encoding search and may affect file size. Decoded PCM remains identical.

## Building from source

Install the .NET 10 SDK and your platform's build tools. Run the matching publish command from the project directory on the target operating system. If you omit `-r`, the SDK selects the current platform's runtime identifier.

| Platform | Runtime identifier | NativeAOT build tools | OpenCL runtime |
| --- | --- | --- | --- |
| Windows x64 | `win-x64` | Visual Studio **Desktop development with C++** workload and Windows SDK | Driver-provided `OpenCL.dll` |
| Linux x64 and Arm64 | `linux-x64`, `linux-arm64` | Clang and zlib development packages | OpenCL ICD loader such as Ubuntu's `ocl-icd-libopencl1`, plus a device driver |
| macOS Apple silicon and Intel | `osx-arm64`, `osx-x64` | Xcode Command Line Tools | System `OpenCL.framework` |

```text
dotnet publish -c Release -r win-x64
dotnet publish -c Release -r linux-x64
dotnet publish -c Release -r linux-arm64
dotnet publish -c Release -r osx-arm64
dotnet publish -c Release -r osx-x64
```

The executable is under `bin/Release/net10.0/<RID>/publish/`. It is named `FLACCL.exe` on Windows and `FLACCL` on Linux and macOS. The NativeAOT build runs without a separate .NET installation. [.NET Native AOT deployment documentation](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)

Apple has deprecated OpenCL. Device availability on macOS depends on the hardware and OS version. Run `--list-devices` to see what your system exposes.

## Troubleshooting

- **OpenCL fails to load or no GPU appears.** Run `--list-devices`. On Linux, check that both the OpenCL ICD loader and a device driver are installed. If the list contains only a CPU device, select it with `--device N`.
- **The OpenCL kernel fails to compile.** FLACCL prints the driver's build log. Try `--cpu-rice` to move residual and Rice partition calculations to the host while keeping OpenCL predictor analysis.
- **foobar2000 reports a WAV format or bit-depth error.** Check that **Parameters** includes `%s` and set **Highest BPS mode supported** to **24**. In **Output bit depth**, explicitly select **16** or **24** if needed.
- **The output file already exists.** Choose a new path or use `--force` to replace the file.

## Credits

Thanks to **Grigory S. Chudov**, credited as **Gregory S. Chudov** in some source files, and the **CUETools contributors**. This project adapts the CUETools.FLACCL encoding logic and OpenCL kernel and reuses FLAC bit-writing code from CUETools.Codecs. [CUETools project](https://github.com/gchudov/cuetools.net) · [Upstream FLACCL source](https://github.com/gchudov/cuetools.net/tree/master/CUETools.Codecs.FLACCL) · [Upstream FLACCL documentation](https://cue.tools/wiki/FLACCL)

## License

FLACCL is distributed under the GNU Lesser General Public License, version 2.1 (LGPL-2.1-only). [Full license text](LICENSE)

The CUETools-derived files retain their original copyright and LGPL-2.1-or-later notices. The source for each binary release is available in the GitHub source archive for the same tag. See [Building from source](#building-from-source) for rebuild instructions.
