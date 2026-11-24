/**
 * SPDX-License-Identifier: LGPL-2.1-or-later
 * CUETools.FLACCL: FLAC audio encoder using OpenCL
 * Copyright (c) 2009-2025 Grigory Chudov
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 2.1 of the License, or (at your option) any later version.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA 02110-1301 USA
 */

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FLACCL
{
    internal sealed class AudioEncoder : IDisposable
    {
        private const long SampleLimit = (1L << 36) - 1;
        long _position;

        public const int MAX_LPC_WINDOWS = 2;

        // number of audio channels
        // valid values are 1 to 8
        int channels, ch_code;

        // audio sample rate in Hz
        int sr_code0, sr_code1;

        // sample size in bits
        // only 16-bit is currently supported
        uint bits_per_sample;
        int bps_code;

        // total stream samples
        // if 0, stream length is unknown
        readonly long sample_count;

        internal FlakeEncodeParams eparams;

        // maximum frame size in bytes
        // this can be used to allocate memory for output
        int max_frame_size;

        int frame_count = 0;
        long frame_pos = 0;

        long first_frame_offset = 0;

        // header bytes
        // allocated by flake_encode_init and freed by flake_encode_close
        byte[] header = null!;

        int samplesInBuffer = 0;

        internal int m_blockSize = 0;

        readonly Crc8 crc8 = new();
        IncrementalHash? md5;

        SeekPoint[]? seek_table;
        int seek_table_offset = -1;
        int nextSeekPoint;

        bool completed;

        CLProgram? openCLProgram;

        FLACCLTask task1 = null!;
        FLACCLTask task2 = null!;

        internal int framesPerTask;

        readonly Stream _IO;
        internal EncoderSettings Settings { get; }
        internal EncoderSettings m_settings => Settings;
        internal const string Vendor = "FLACCL";

        internal AudioEncoder(EncoderSettings settings, Stream output, long sampleCount)
        {
            settings.Validate();
            if (sampleCount < -1 || sampleCount > SampleLimit)
                throw new ArgumentOutOfRangeException(nameof(sampleCount), "PCM exceeds the FLAC sample count limit.");
            Settings = settings;
            _IO = output;
            sample_count = sampleCount;
            channels = Settings.PCM.ChannelCount;
            bits_per_sample = (uint)Settings.PCM.BitsPerSample;
            eparams.flake_set_defaults(settings);
            m_blockSize = Settings.BlockSize != 0
                ? Settings.BlockSize
                : select_blocksize(Settings.PCM.SampleRate, eparams.block_time_ms);
        }

        private void Initialize()
        {
            if (openCLProgram is not null)
                return;

            Device device = Settings.Device;
            int groupSize = device.IsCpu ? 1 : m_blockSize > 8192 ? 256 : 128;
            if (!device.IsCpu && (nuint)groupSize > device.MaxWorkGroupSize)
                throw new OpenClException($"{device.Name} needs a smaller block size. Select 8192 or less with -b.");
            bool gpuPartitions = Settings.GpuPartitions && device.Supports("cl_khr_local_int32_extended_atomics");
            int channelCount = channels == 2 && eparams.do_midside ? 4 : channels;
            long inputFrames = sample_count < 0 ? 256 : Math.Max(1, (sample_count + m_blockSize - 1) / m_blockSize);
            long deviceFrames = (long)device.ComputeUnits * (8 / channels);
            framesPerTask = (int)Math.Clamp(deviceFrames, 1, Math.Min(inputFrames, 256));
            int maxBlockSize = (int)BitOperations.RoundUpToPowerOf2((uint)m_blockSize);
            var defines = new StringBuilder()
                .AppendLine($"#define MAX_ORDER {Settings.MaxLPCOrder}")
                .AppendLine($"#define GROUP_SIZE {groupSize}")
                .AppendLine($"#define GROUP_SIZE_LOG {BitOperations.Log2((uint)groupSize)}")
                .AppendLine($"#define BITS_PER_SAMPLE {bits_per_sample}")
                .AppendLine($"#define MAX_BLOCKSIZE {maxBlockSize}")
                .AppendLine($"#define MAX_CHANNELS {channels}")
                .AppendLine($"#define VENDOR_ID {device.VendorId}");
            if (device.IsCpu)
                defines.AppendLine("#define FLACCL_CPU");
            if (gpuPartitions)
                defines.AppendLine("#define DO_PARTITIONS");
            foreach (string extension in new[] { "cl_khr_fp64", "cl_amd_fp64" })
            {
                if (device.Supports(extension))
                    defines.AppendLine($"#pragma OPENCL EXTENSION {extension} : enable")
                        .AppendLine($"#define HAVE_{extension}");
            }

            openCLProgram = new CLProgram(device, defines.ToString());
            int headerSize = flake_encode_init();
            _IO.Write(header, 0, headerSize);
            if (_IO.CanSeek)
                first_frame_offset = _IO.Position;

            task1 = new FLACCLTask(openCLProgram, channelCount, max_frame_size, this, groupSize, gpuPartitions);
            task2 = new FLACCLTask(openCLProgram, channelCount, max_frame_size, this, groupSize, gpuPartitions);
        }

        internal unsafe void Write(ReadOnlySpan<byte> pcm)
        {
            if (completed)
                throw new InvalidOperationException("Encoding has already completed.");
            int align = Settings.PCM.BlockAlign;
            if (pcm.Length % align != 0)
                throw new ArgumentException("PCM must contain whole samples.", nameof(pcm));
            if (frame_pos + samplesInBuffer + pcm.Length / align > SampleLimit)
                throw new InvalidDataException("PCM exceeds the FLAC sample count limit.");
            Initialize();
            md5?.AppendData(pcm);
            while (!pcm.IsEmpty)
            {
                int capacity = m_blockSize * framesPerTask - samplesInBuffer;
                int count = Math.Min(pcm.Length / align, capacity);
                int bytes = count * align;
                pcm[..bytes].CopyTo(new Span<byte>((byte*)task1.clSamplesBytesPtr + samplesInBuffer * align, bytes));
                samplesInBuffer += count;
                pcm = pcm[bytes..];
                if (samplesInBuffer == m_blockSize * framesPerTask)
                    do_output_frames(framesPerTask);
            }
        }

        private unsafe void do_output_frames(int frameCount)
        {
            send_to_GPU(task1, frameCount, m_blockSize);
            run_GPU_task(task1);
            Drain(task2);
            int consumed = m_blockSize * frameCount;
            samplesInBuffer -= consumed;
            if (samplesInBuffer > 0)
            {
                int align = Settings.PCM.BlockAlign;
                AudioSamples.MemCpy((byte*)task2.clSamplesBytesPtr,
                    (byte*)task1.clSamplesBytesPtr + consumed * align, samplesInBuffer * align);
            }
            (task1, task2) = (task2, task1);
        }

        private void Drain(FLACCLTask task)
        {
            if (task.frameCount == 0)
                return;
            task.openCLCQ.Finish();
            process_result(task);
            write_result(task);
            task.frameCount = 0;
        }

        internal void Complete()
        {
            if (completed)
                return;
            Initialize();
            int fullFrames = samplesInBuffer / m_blockSize;
            if (fullFrames != 0)
                do_output_frames(fullFrames);
            if (samplesInBuffer != 0)
            {
                m_blockSize = samplesInBuffer;
                do_output_frames(1);
            }
            Drain(task2);
            if (sample_count >= 0 && _position != sample_count)
                throw new InvalidDataException($"Expected {sample_count} PCM samples, encoded {_position}.");

            if (_IO.CanSeek)
            {
                long end = _IO.Position;
                if (sample_count < 0)
                {
                    ulong headerFields = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(18, 8));
                    BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(18, 8), headerFields | (ulong)_position);
                    _IO.Position = 18;
                    _IO.Write(header.AsSpan(18, 8));
                }
                if (md5 is not null)
                {
                    _IO.Position = 26;
                    _IO.Write(md5.GetHashAndReset());
                }
                if (seek_table is not null)
                {
                    _IO.Position = seek_table_offset;
                    int size = write_seekpoints(header, 0, 0);
                    _IO.Write(header, 4, size - 4);
                }
                _IO.Position = end;
            }
            completed = true;
        }

        public void Dispose()
        {
            openCLProgram?.Dispose();
            md5?.Dispose();
        }

        unsafe void encode_residual_fixed(int* res, int* smp, int n, int order)
        {
            int i;
            int s0, s1, s2;
            switch (order)
            {
                case 0:
                    AudioSamples.MemCpy(res, smp, n);
                    return;
                case 1:
                    *(res++) = s1 = *(smp++);
                    for (i = n - 1; i > 0; i--)
                    {
                        s0 = *(smp++);
                        *(res++) = s0 - s1;
                        s1 = s0;
                    }
                    return;
                case 2:
                    *(res++) = s2 = *(smp++);
                    *(res++) = s1 = *(smp++);
                    for (i = n - 2; i > 0; i--)
                    {
                        s0 = *(smp++);
                        *(res++) = s0 - 2 * s1 + s2;
                        s2 = s1;
                        s1 = s0;
                    }
                    return;
                case 3:
                    res[0] = smp[0];
                    res[1] = smp[1];
                    res[2] = smp[2];
                    for (i = 3; i < n; i++)
                    {
                        res[i] = smp[i] - 3 * smp[i - 1] + 3 * smp[i - 2] - smp[i - 3];
                    }
                    return;
                case 4:
                    res[0] = smp[0];
                    res[1] = smp[1];
                    res[2] = smp[2];
                    res[3] = smp[3];
                    for (i = 4; i < n; i++)
                    {
                        res[i] = smp[i] - 4 * smp[i - 1] + 6 * smp[i - 2] - 4 * smp[i - 3] + smp[i - 4];
                    }
                    return;
                default:
                    return;
            }
        }

        static unsafe uint calc_optimal_rice_params(int porder, int* parm, ulong* sums, uint n, uint pred_order, ref int method)
        {
            uint part = (1U << porder);
            uint cnt = (n >> porder) - pred_order;
            int maxK = method > 0 ? 30 : FlakeConstants.MAX_RICE_PARAM;
            int k = cnt > 0 ? Math.Min(maxK, BitReader.log2i(sums[0] / cnt)) : 0;
            int realMaxK0 = k;
            ulong all_bits = cnt * ((uint)k + 1U) + (sums[0] >> k);
            parm[0] = k;
            cnt = (n >> porder);
            for (uint i = 1; i < part; i++)
            {
                k = Math.Min(maxK, BitReader.log2i(sums[i] / cnt));
                realMaxK0 = Math.Max(realMaxK0, k);
                all_bits += cnt * ((uint)k + 1U) + (sums[i] >> k);
                parm[i] = k;
            }
            method = realMaxK0 > FlakeConstants.MAX_RICE_PARAM ? 1 : 0;
            return (uint)all_bits + ((4U + (uint)method) * part);
        }

        static unsafe void calc_lower_sums(int pmin, int pmax, ulong* sums)
        {
            for (int i = pmax - 1; i >= pmin; i--)
            {
                for (int j = 0; j < (1 << i); j++)
                {
                    sums[i * FlakeConstants.MAX_PARTITIONS + j] =
                        sums[(i + 1) * FlakeConstants.MAX_PARTITIONS + 2 * j] +
                        sums[(i + 1) * FlakeConstants.MAX_PARTITIONS + 2 * j + 1];
                }
            }
        }

        static unsafe void calc_sums(int pmin, int pmax, uint* data, uint n, uint pred_order, ulong* sums)
        {
            int parts = (1 << pmax);
            uint* res = data + pred_order;
            uint cnt = (n >> pmax) - pred_order;
            ulong sum = 0;
            for (uint j = cnt; j > 0; j--)
                sum += *(res++);
            sums[0] = sum;
            cnt = (n >> pmax);
            for (int i = 1; i < parts; i++)
            {
                sum = 0;
                for (uint j = cnt; j > 0; j--)
                    sum += *(res++);
                sums[i] = sum;
            }
        }

        /// <summary>
        /// Special case when (n >> pmax) == 18
        /// </summary>
        /// <param name="pmin"></param>
        /// <param name="pmax"></param>
        /// <param name="data"></param>
        /// <param name="n"></param>
        /// <param name="pred_order"></param>
        /// <param name="sums"></param>
        static unsafe void calc_sums18(int pmin, int pmax, uint* data, uint n, uint pred_order, ulong* sums)
        {
            int parts = (1 << pmax);
            uint* res = data + pred_order;
            uint cnt = 18 - pred_order;
            ulong sum = 0UL;
            for (uint j = cnt; j > 0; j--)
                sum += *(res++);
            sums[0] = sum;
            for (int i = 1; i < parts; i++)
            {
                sums[i] = 0UL +
                    *(res++) + *(res++) + *(res++) + *(res++) +
                    *(res++) + *(res++) + *(res++) + *(res++) +
                    *(res++) + *(res++) + *(res++) + *(res++) +
                    *(res++) + *(res++) + *(res++) + *(res++) +
                    *(res++) + *(res++);
            }
        }

        /// <summary>
        /// Special case when (n >> pmax) == 32
        /// </summary>
        /// <param name="pmin"></param>
        /// <param name="pmax"></param>
        /// <param name="data"></param>
        /// <param name="n"></param>
        /// <param name="pred_order"></param>
        /// <param name="sums"></param>
        static unsafe void calc_sums32(int pmin, int pmax, uint* data, uint n, uint pred_order, ulong* sums)
        {
            int parts = (1 << pmax);
            uint* res = data + pred_order;
            uint cnt = 32 - pred_order;
            ulong sum = 0UL;
            for (uint j = cnt; j > 0; j--)
                sum += *(res++);
            sums[0] = sum;
            for (int i = 1; i < parts; i++)
            {
                sums[i] = 0UL +
                    *(res++) + *(res++) + *(res++) + *(res++) +
                    *(res++) + *(res++) + *(res++) + *(res++) +
                    *(res++) + *(res++) + *(res++) + *(res++) +
                    *(res++) + *(res++) + *(res++) + *(res++) +
                    *(res++) + *(res++) + *(res++) + *(res++) +
                    *(res++) + *(res++) + *(res++) + *(res++) +
                    *(res++) + *(res++) + *(res++) + *(res++) +
                    *(res++) + *(res++) + *(res++) + *(res++);
            }
        }

        /// <summary>
        /// Special case when (n >> pmax) == 18
        /// </summary>
        /// <param name="pmin"></param>
        /// <param name="pmax"></param>
        /// <param name="data"></param>
        /// <param name="n"></param>
        /// <param name="pred_order"></param>
        /// <param name="sums"></param>
        static unsafe void calc_sums16(int pmin, int pmax, uint* data, uint n, uint pred_order, ulong* sums)
        {
            int parts = (1 << pmax);
            uint* res = data + pred_order;
            uint cnt = 16 - pred_order;
            ulong sum = 0UL;
            for (uint j = cnt; j > 0; j--)
                sum += *(res++);
            sums[0] = sum;
            for (int i = 1; i < parts; i++)
            {
                sums[i] = 0UL +
                    *(res++) + *(res++) + *(res++) + *(res++) +
                    *(res++) + *(res++) + *(res++) + *(res++) +
                    *(res++) + *(res++) + *(res++) + *(res++) +
                    *(res++) + *(res++) + *(res++) + *(res++);
            }
        }

        static unsafe uint calc_rice_params(RiceContext rc, int pmin, int pmax, int* data, uint n, uint pred_order, int max_method)
        {
            uint* udata = stackalloc uint[(int)n];
            ulong* sums = stackalloc ulong[(pmax + 1) * FlakeConstants.MAX_PARTITIONS];
            int* parm = stackalloc int[(pmax + 1) * FlakeConstants.MAX_PARTITIONS];
            //uint* bits = stackalloc uint[FlakeConstants.MAX_PARTITION_ORDER];

            for (uint i = 0; i < n; i++)
                udata[i] = (uint)((data[i] << 1) ^ (data[i] >> 31));

            // sums for highest level
            if ((n >> pmax) == 32)
                calc_sums32(pmin, pmax, udata, n, pred_order, sums + pmax * FlakeConstants.MAX_PARTITIONS);
            else if ((n >> pmax) == 18)
                calc_sums18(pmin, pmax, udata, n, pred_order, sums + pmax * FlakeConstants.MAX_PARTITIONS);
            else if ((n >> pmax) == 16)
                calc_sums16(pmin, pmax, udata, n, pred_order, sums + pmax * FlakeConstants.MAX_PARTITIONS);
            else
                calc_sums(pmin, pmax, udata, n, pred_order, sums + pmax * FlakeConstants.MAX_PARTITIONS);
            // sums for lower levels
            calc_lower_sums(pmin, pmax, sums);

            uint opt_bits = AudioSamples.UINT32_MAX;
            int opt_porder = pmin;
            int opt_method = 0;
            for (int i = pmin; i <= pmax; i++)
            {
                int method = max_method;
                uint bits = calc_optimal_rice_params(i, parm + i * FlakeConstants.MAX_PARTITIONS, sums + i * FlakeConstants.MAX_PARTITIONS, n, pred_order, ref method);
                if (bits <= opt_bits)
                {
                    opt_bits = bits;
                    opt_porder = i;
                    opt_method = method;
                }
            }

            rc.porder = opt_porder;
            rc.coding_method = opt_method;
            fixed (int* rparms = rc.rparams)
                AudioSamples.MemCpy(rparms, parm + opt_porder * FlakeConstants.MAX_PARTITIONS, (1 << opt_porder));

            return opt_bits;
        }

        internal static int get_max_p_order(int max_porder, int n, int order)
        {
            int porder = Math.Min(max_porder, BitReader.log2i(n ^ (n - 1)));
            if (order > 0)
                porder = Math.Min(porder, BitReader.log2i(n / order));
            return porder;
        }

        unsafe void output_frame_header(FlacFrame frame)
        {
            frame.writer.writebits(15, 0x7FFC);
            frame.writer.writebits(1, 0);
            frame.writer.writebits(4, frame.bs_code0);
            frame.writer.writebits(4, sr_code0);
            if (frame.ch_mode == ChannelMode.NotStereo)
                frame.writer.writebits(4, ch_code);
            else
                frame.writer.writebits(4, (int)frame.ch_mode);
            frame.writer.writebits(3, bps_code);
            frame.writer.writebits(1, 0);
            frame.writer.write_utf8(frame.frame_number);

            // custom block size
            if (frame.bs_code1 >= 0)
            {
                if (frame.bs_code1 < 256)
                    frame.writer.writebits(8, frame.bs_code1);
                else
                    frame.writer.writebits(16, frame.bs_code1);
            }

            // custom sample rate
            if (sr_code1 > 0)
            {
                if (sr_code1 < 256)
                    frame.writer.writebits(8, sr_code1);
                else
                    frame.writer.writebits(16, sr_code1);
            }

            // CRC-8 of frame header
            frame.writer.flush();
            byte crc = crc8.ComputeChecksum(frame.writer.Buffer, frame.writer_offset, frame.writer.Length - frame.writer_offset);
            frame.writer.writebits(8, crc);
        }

        unsafe long measure_residual(FlacSubframeInfo sub, int pos, int cnt, int k)
        {
            long q = 0;
            int* r = sub.best.residual + pos;
            int* fin = r + cnt;
            while (r < fin)
            {
                int v = *(r++);
                uint uv = (uint)((v << 1) ^ (v >> 31));
                q += uv >> k;
            }
            return (k + 1) * cnt + q;
        }

        unsafe long measure_residual(FlacFrame frame, FlacSubframeInfo sub)
        {
            // partition order
            int porder = sub.best.rc.porder;
            int psize = frame.blocksize >> porder;
            long size = 6 + ((4 + sub.best.rc.coding_method) << porder);
            size += measure_residual(sub, sub.best.order, psize - sub.best.order, sub.best.rc.rparams[0]);
            // residual
            for (int p = 1; p < (1 << porder); p++)
                size += measure_residual(sub, p * psize, psize, sub.best.rc.rparams[p]);
            return size;
        }

        unsafe void output_residual(FlacFrame frame, FlacSubframeInfo sub)
        {

            // rice-encoded block
            frame.writer.writebits(2, sub.best.rc.coding_method);
            // partition order
            int porder = sub.best.rc.porder;
            frame.writer.writebits(4, porder);

            int psize = frame.blocksize >> porder;
            int res_cnt = psize - sub.best.order;

            // residual
            int j = sub.best.order;
            fixed (byte* fixbuf = frame.writer.Buffer)
                for (int p = 0; p < (1 << porder); p++)
                {
                    int k = sub.best.rc.rparams[p];
                    frame.writer.writebits(4 + sub.best.rc.coding_method, k);
                    if (p == 1) res_cnt = psize;
                    int cnt = Math.Min(res_cnt, frame.blocksize - j);
                    frame.writer.write_rice_block_signed(fixbuf, k, sub.best.residual + j, cnt);
                    j += cnt;
                }
        }

        unsafe void
        output_subframe_constant(FlacFrame frame, FlacSubframeInfo sub)
        {
            frame.writer.writebits_signed(sub.obits, sub.samples[0]);
        }

        unsafe void
        output_subframe_verbatim(FlacFrame frame, FlacSubframeInfo sub)
        {
            int n = frame.blocksize;
            for (int i = 0; i < n; i++)
                frame.writer.writebits_signed(sub.obits, sub.samples[i]);
            // Don't use residual here, because we don't copy samples to residual for verbatim frames.
        }

        unsafe void
        output_subframe_fixed(FLACCLTask task, FlacSubframeInfo sub)
        {
            FlacFrame frame = task.frame;

            // warm-up samples
            for (int i = 0; i < sub.best.order; i++)
                frame.writer.writebits_signed(sub.obits, sub.samples[i]);

            // residual
            output_residual(frame, sub);
        }

        unsafe long
        measure_subframe_lpc(FlacFrame frame, FlacSubframeInfo sub)
        {
            return sub.best.order * sub.obits + 9 + sub.best.order * sub.best.cbits + measure_residual(frame, sub);
        }

        unsafe long
        measure_subframe_fixed(FlacFrame frame, FlacSubframeInfo sub)
        {
            return sub.best.order * sub.obits + measure_residual(frame, sub);
        }

        unsafe long
        measure_subframe(FlacFrame frame, FlacSubframeInfo sub)
        {
            switch (sub.best.type)
            {
                case SubframeType.Constant:
                    return (uint)sub.obits;
                case SubframeType.Verbatim:
                    return (uint)(sub.obits * frame.blocksize);
                case SubframeType.Fixed:
                    return measure_subframe_fixed(frame, sub);
                case SubframeType.LPC:
                    return measure_subframe_lpc(frame, sub);
            }
            throw new InvalidOperationException("not supported subframe type");
        }

        unsafe void
        output_subframe_lpc(FLACCLTask task, FlacSubframeInfo sub)
        {
            FlacFrame frame = task.frame;

            // warm-up samples
            for (int i = 0; i < sub.best.order; i++)
                frame.writer.writebits_signed(sub.obits, sub.samples[i]);

            // LPC coefficients
            frame.writer.writebits(4, sub.best.cbits - 1);
            frame.writer.writebits_signed(5, sub.best.shift);
            for (int i = 0; i < sub.best.order; i++)
                frame.writer.writebits_signed(sub.best.cbits, sub.best.coefs[i]);

            // residual
            output_residual(frame, sub);
        }

        unsafe void output_subframes(FLACCLTask task)
        {
            FlacFrame frame = task.frame;
            for (int ch = 0; ch < channels; ch++)
            {
                FlacSubframeInfo sub = frame.subframes[ch];
                // subframe header
                int type_code = (int)sub.best.type;
                if (sub.best.type == SubframeType.Fixed)
                    type_code |= sub.best.order;
                if (sub.best.type == SubframeType.LPC)
                    type_code |= sub.best.order - 1;
                frame.writer.writebits(1, 0);
                frame.writer.writebits(6, type_code);
                frame.writer.writebits(1, sub.wbits != 0 ? 1 : 0);
                if (sub.wbits > 0)
                    frame.writer.writebits((int)sub.wbits, 1);

                // subframe
                switch (sub.best.type)
                {
                    case SubframeType.Constant:
                        output_subframe_constant(frame, sub);
                        break;
                    case SubframeType.Verbatim:
                        output_subframe_verbatim(frame, sub);
                        break;
                    case SubframeType.Fixed:
                        output_subframe_fixed(task, sub);
                        break;
                    case SubframeType.LPC:
                        output_subframe_lpc(task, sub);
                        break;
                }
            }
        }

        void output_frame_footer(FlacFrame frame)
        {
            frame.writer.flush();
            ushort crc = frame.writer.get_crc16();
            frame.writer.writebits(16, crc);
            frame.writer.flush();
        }

        unsafe delegate void window_function(float* window, int size);

        unsafe void calculate_window(FLACCLTask task, window_function func, WindowFunction flag)
        {
            if ((eparams.window_function & flag) == 0 || task.nWindowFunctions == MAX_LPC_WINDOWS)
                return;

            func(((float*)task.clWindowFunctionsPtr) + task.nWindowFunctions * task.frameSize, task.frameSize);
            //float* pos = window + _windowcount * FLACCLWriter.MAX_BLOCKSIZE * 2;
            //do
            //        break;
            task.nWindowFunctions++;
        }

        unsafe void initializeSubframeTasks(int blocksize, int channelsCount, int nFrames, FLACCLTask task)
        {
            task.channelSize = ((blocksize + 3) & ~3) * nFrames;
            task.frameSize = blocksize;
            task.nWindowFunctions = 0;
            if (task.frameSize > 4)
            {
                calculate_window(task, Lpc.window_welch, WindowFunction.Welch);
                calculate_window(task, Lpc.window_flattop, WindowFunction.Flattop);
                calculate_window(task, (w, wsz) =>
                {
                    Lpc.window_tukey(w, wsz, 0.5);
                }, WindowFunction.Tukey);
                calculate_window(task, Lpc.window_hann, WindowFunction.Hann);
                calculate_window(task, Lpc.window_bartlett, WindowFunction.Bartlett);
                if (task.nWindowFunctions == 0)
                    throw new InvalidOperationException("invalid windowfunction");
                task.openCLCQ.EnqueueWriteBuffer(task.clWindowFunctions, false, 0, sizeof(float) * task.nWindowFunctions * task.frameSize, task.clWindowFunctionsPtr);
            }

            task.nResidualTasks = 0;
            task.nTasksPerWindow = Math.Min(32, eparams.orders_per_window);
            task.nResidualTasksPerChannel = task.nWindowFunctions * task.nTasksPerWindow + (eparams.do_constant ? 1 : 0) + Math.Max(0, 1 + m_settings.MaxFixedOrder - m_settings.MinFixedOrder);
            if (task.nResidualTasksPerChannel > 32)
                throw new InvalidOperationException("too many tasks");
            if (channels == 2 && channelsCount == 4)
                task.nEstimateTasksPerChannel = Math.Min(eparams.orders_per_channel, task.nResidualTasksPerChannel);
            else
                task.nEstimateTasksPerChannel = task.nResidualTasksPerChannel;

            //    task.nResidualTasksPerChannel = (task.nResidualTasksPerChannel + 7) & ~7;
            for (int iFrame = 0; iFrame < nFrames; iFrame++)
            {
                for (int ch = 0; ch < channelsCount; ch++)
                {
                    int* selectedTasks = (int*)task.clSelectedTasksPtr;
                    for (int j = 0; j < task.nEstimateTasksPerChannel; j++)
                    {
                        int k = j;
                        if (j < task.nWindowFunctions * task.nTasksPerWindow && task.nWindowFunctions > 1)
                        {
                            k = (j % task.nWindowFunctions) * task.nTasksPerWindow
                                + (j / task.nWindowFunctions);
                        }
                        selectedTasks[(iFrame * channelsCount + ch) * task.nEstimateTasksPerChannel + j] =
                            (iFrame * channelsCount + ch) * task.nResidualTasksPerChannel + k;
                    }

                    for (int iWindow = 0; iWindow < task.nWindowFunctions; iWindow++)
                    {
                        // LPC tasks
                        for (int order = 0; order < task.nTasksPerWindow; order++)
                        {
                            task.ResidualTasks[task.nResidualTasks].type = (int)SubframeType.LPC;
                            task.ResidualTasks[task.nResidualTasks].channel = ch;
                            task.ResidualTasks[task.nResidualTasks].obits = (int)bits_per_sample + (channels == 2 && ch == 3 ? 1 : 0);
                            task.ResidualTasks[task.nResidualTasks].abits = task.ResidualTasks[task.nResidualTasks].obits;
                            task.ResidualTasks[task.nResidualTasks].blocksize = blocksize;
                            task.ResidualTasks[task.nResidualTasks].residualOrder = order + 1;
                            task.ResidualTasks[task.nResidualTasks].samplesOffs = ch * task.channelSize + iFrame * blocksize;
                            task.ResidualTasks[task.nResidualTasks].residualOffs = task.ResidualTasks[task.nResidualTasks].samplesOffs;
                            task.ResidualTasks[task.nResidualTasks].wbits = 0;
                            task.ResidualTasks[task.nResidualTasks].coding_method = Settings.PCM.BitsPerSample > 16 ? 1 : 0;
                            task.ResidualTasks[task.nResidualTasks].size = task.ResidualTasks[task.nResidualTasks].obits * blocksize;
                            task.nResidualTasks++;
                        }
                    }
                    // Constant frames
                    if (eparams.do_constant)
                    {
                        task.ResidualTasks[task.nResidualTasks].type = (int)SubframeType.Constant;
                        task.ResidualTasks[task.nResidualTasks].channel = ch;
                        task.ResidualTasks[task.nResidualTasks].obits = (int)bits_per_sample + (channels == 2 && ch == 3 ? 1 : 0);
                        task.ResidualTasks[task.nResidualTasks].abits = task.ResidualTasks[task.nResidualTasks].obits;
                        task.ResidualTasks[task.nResidualTasks].blocksize = blocksize;
                        task.ResidualTasks[task.nResidualTasks].samplesOffs = ch * task.channelSize + iFrame * blocksize;
                        task.ResidualTasks[task.nResidualTasks].residualOffs = task.ResidualTasks[task.nResidualTasks].samplesOffs;
                        task.ResidualTasks[task.nResidualTasks].wbits = 0;
                        task.ResidualTasks[task.nResidualTasks].coding_method = Settings.PCM.BitsPerSample > 16 ? 1 : 0;
                        task.ResidualTasks[task.nResidualTasks].size = task.ResidualTasks[task.nResidualTasks].obits * blocksize;
                        task.ResidualTasks[task.nResidualTasks].residualOrder = 1;
                        task.ResidualTasks[task.nResidualTasks].shift = 0;
                        task.ResidualTasks[task.nResidualTasks].coefs[0] = 1;
                        task.nResidualTasks++;
                    }
                    // Fixed prediction
                    for (int order = m_settings.MinFixedOrder; order <= m_settings.MaxFixedOrder; order++)
                    {
                        task.ResidualTasks[task.nResidualTasks].type = (int)SubframeType.Fixed;
                        task.ResidualTasks[task.nResidualTasks].channel = ch;
                        task.ResidualTasks[task.nResidualTasks].obits = (int)bits_per_sample + (channels == 2 && ch == 3 ? 1 : 0);
                        task.ResidualTasks[task.nResidualTasks].abits = task.ResidualTasks[task.nResidualTasks].obits;
                        task.ResidualTasks[task.nResidualTasks].blocksize = blocksize;
                        task.ResidualTasks[task.nResidualTasks].residualOrder = order;
                        task.ResidualTasks[task.nResidualTasks].samplesOffs = ch * task.channelSize + iFrame * blocksize;
                        task.ResidualTasks[task.nResidualTasks].residualOffs = task.ResidualTasks[task.nResidualTasks].samplesOffs;
                        task.ResidualTasks[task.nResidualTasks].wbits = 0;
                        task.ResidualTasks[task.nResidualTasks].coding_method = Settings.PCM.BitsPerSample > 16 ? 1 : 0;
                        task.ResidualTasks[task.nResidualTasks].size = task.ResidualTasks[task.nResidualTasks].obits * blocksize;
                        task.ResidualTasks[task.nResidualTasks].shift = 0;
                        switch (order)
                        {
                            case 0:
                                break;
                            case 1:
                                task.ResidualTasks[task.nResidualTasks].coefs[0] = 1;
                                break;
                            case 2:
                                task.ResidualTasks[task.nResidualTasks].coefs[1] = 2;
                                task.ResidualTasks[task.nResidualTasks].coefs[0] = -1;
                                break;
                            case 3:
                                task.ResidualTasks[task.nResidualTasks].coefs[2] = 3;
                                task.ResidualTasks[task.nResidualTasks].coefs[1] = -3;
                                task.ResidualTasks[task.nResidualTasks].coefs[0] = 1;
                                break;
                            case 4:
                                task.ResidualTasks[task.nResidualTasks].coefs[3] = 4;
                                task.ResidualTasks[task.nResidualTasks].coefs[2] = -6;
                                task.ResidualTasks[task.nResidualTasks].coefs[1] = 4;
                                task.ResidualTasks[task.nResidualTasks].coefs[0] = -1;
                                break;
                        }
                        task.nResidualTasks++;
                    }
                    //// Filler
                    //    task.ResidualTasks[task.nResidualTasks].type = (int)SubframeType.Verbatim;
                    //    task.ResidualTasks[task.nResidualTasks].channel = ch;
                    //    task.ResidualTasks[task.nResidualTasks].obits = (int)bits_per_sample + (channels == 2 && ch == 3 ? 1 : 0);
                    //    task.ResidualTasks[task.nResidualTasks].abits = task.ResidualTasks[task.nResidualTasks].obits;
                    //    task.ResidualTasks[task.nResidualTasks].blocksize = blocksize;
                    //    task.ResidualTasks[task.nResidualTasks].residualOrder = 0;
                    //    task.ResidualTasks[task.nResidualTasks].samplesOffs = ch * task.channelSize + iFrame * blocksize;
                    //    task.ResidualTasks[task.nResidualTasks].residualOffs = task.ResidualTasks[task.nResidualTasks].samplesOffs;
                    //    task.ResidualTasks[task.nResidualTasks].shift = 0;
                    //    task.nResidualTasks++;
                }
            }
            if (sizeof(FLACCLSubframeTask) * task.nResidualTasks > task.residualTasksLen)
                throw new InvalidOperationException("oops");

            task.openCLCQ.EnqueueWriteBuffer(task.clResidualTasks, false, 0, sizeof(FLACCLSubframeTask) * task.nResidualTasks, task.clResidualTasksPtr);
            task.openCLCQ.EnqueueWriteBuffer(task.clSelectedTasks, false, 0, sizeof(int) * (nFrames * channelsCount * task.nEstimateTasksPerChannel), task.clSelectedTasksPtr);

        }

        unsafe void encode_residual(FLACCLTask task, int channelsCount, int iFrame)
        {
            FlacFrame frame = task.frame;

            if (channelsCount == 4 && channels == 2 && frame.blocksize > m_settings.MaxLPCOrder)
            {
                if (task.BestResidualTasks[iFrame * 2].channel == 0 && task.BestResidualTasks[iFrame * 2 + 1].channel == 1)
                    frame.ch_mode = ChannelMode.LeftRight;
                else if (task.BestResidualTasks[iFrame * 2].channel == 0 && task.BestResidualTasks[iFrame * 2 + 1].channel == 3)
                    frame.ch_mode = ChannelMode.LeftSide;
                else if (task.BestResidualTasks[iFrame * 2].channel == 3 && task.BestResidualTasks[iFrame * 2 + 1].channel == 1)
                    frame.ch_mode = ChannelMode.RightSide;
                else if (task.BestResidualTasks[iFrame * 2].channel == 2 && task.BestResidualTasks[iFrame * 2 + 1].channel == 3)
                    frame.ch_mode = ChannelMode.MidSide;
                else
                    throw new InvalidOperationException("internal error: invalid stereo mode");
                frame.SwapSubframes(0, task.BestResidualTasks[iFrame * 2].channel);
                frame.SwapSubframes(1, task.BestResidualTasks[iFrame * 2 + 1].channel);
            }
            else
                frame.ch_mode = channels != 2 ? ChannelMode.NotStereo : ChannelMode.LeftRight;

            int toUnpack = Math.Min(task.frameSize, m_settings.MaxLPCOrder);
            // calculate wbits before unpacking samples.
            for (int ch = 0; ch < channels; ch++)
            {
                int index = ch + iFrame * channels;
                frame.subframes[ch].best.type = SubframeType.Verbatim;
                frame.subframes[ch].best.size = (uint)(frame.subframes[ch].obits * frame.blocksize);
                frame.subframes[ch].wbits = 0;
                if (frame.blocksize > Math.Max(4, m_settings.MaxLPCOrder))
                {
                    if (task.BestResidualTasks[index].size < 0)
                        throw new InvalidOperationException("internal error");

                    if (frame.subframes[ch].best.size > task.BestResidualTasks[index].size &&
                        (SubframeType)task.BestResidualTasks[index].type != SubframeType.Verbatim)
                    {
                        frame.subframes[ch].best.residual = ((int*)task.clResidualPtr) + task.BestResidualTasks[index].residualOffs;
                        frame.subframes[ch].best.type = (SubframeType)task.BestResidualTasks[index].type;
                        frame.subframes[ch].best.size = (uint)task.BestResidualTasks[index].size;
                        frame.subframes[ch].best.order = task.BestResidualTasks[index].residualOrder;
                        frame.subframes[ch].best.cbits = task.BestResidualTasks[index].cbits;
                        frame.subframes[ch].best.shift = task.BestResidualTasks[index].shift;
                        frame.subframes[ch].obits -= task.BestResidualTasks[index].wbits;
                        frame.subframes[ch].wbits = task.BestResidualTasks[index].wbits;
                        for (int i = 0; i < task.BestResidualTasks[index].residualOrder; i++)
                            frame.subframes[ch].best.coefs[i] = task.BestResidualTasks[index].coefs[task.BestResidualTasks[index].residualOrder - 1 - i];
                        frame.subframes[ch].best.rc.porder = task.BestResidualTasks[index].porder;
                        frame.subframes[ch].best.rc.coding_method = task.BestResidualTasks[index].coding_method;
                        if (task.UseGPUOnly)
                        {
                            if (frame.subframes[ch].best.type == SubframeType.Fixed || frame.subframes[ch].best.type == SubframeType.LPC)
                            {
                                int* riceParams = ((int*)task.clBestRiceParamsPtr) + (index << task.max_porder);
                                fixed (int* dstParams = frame.subframes[ch].best.rc.rparams)
                                    AudioSamples.MemCpy(dstParams, riceParams, (1 << frame.subframes[ch].best.rc.porder));
                            }
                            long real_size = measure_subframe(frame, frame.subframes[ch]);
                            if (real_size != task.frame.subframes[ch].best.size)
                                throw new InvalidOperationException("size reported incorrectly");
                        }
                    }
                }
                if (task.frame.subframes[ch].best.type == SubframeType.Verbatim)
                    toUnpack = task.frameSize;
                if (task.frame.subframes[ch].best.type == SubframeType.LPC && !task.UseGPUOnly)
                    toUnpack = task.frameSize;
                if (task.frame.subframes[ch].best.type == SubframeType.Fixed && !task.UseGPUOnly)
                    toUnpack = task.frameSize;
            }
            unpack_samples(task, toUnpack);

            for (int ch = 0; ch < channels; ch++)
            {
                switch (task.frame.subframes[ch].best.type)
                {
                    case SubframeType.Constant:
                        break;
                    case SubframeType.Verbatim:
                        break;
                    case SubframeType.Fixed:
                        if (!task.UseGPUOnly)
                        {
                            encode_residual_fixed(task.frame.subframes[ch].best.residual, task.frame.subframes[ch].samples,
                                task.frame.blocksize, task.frame.subframes[ch].best.order);

                            int pmin = get_max_p_order(m_settings.MinPartitionOrder, task.frame.blocksize, task.frame.subframes[ch].best.order);
                            int pmax = get_max_p_order(m_settings.MaxPartitionOrder, task.frame.blocksize, task.frame.subframes[ch].best.order);
                            calc_rice_params(task.frame.subframes[ch].best.rc, pmin, pmax, task.frame.subframes[ch].best.residual, (uint)task.frame.blocksize, (uint)task.frame.subframes[ch].best.order, Settings.PCM.BitsPerSample > 16 ? 1 : 0);
                        }
                        break;
                    case SubframeType.LPC:
                        if (!task.UseGPUOnly)
                        {
                            int pmin = get_max_p_order(m_settings.MinPartitionOrder, task.frame.blocksize, task.frame.subframes[ch].best.order);
                            int pmax = get_max_p_order(m_settings.MaxPartitionOrder, task.frame.blocksize, task.frame.subframes[ch].best.order);
                            fixed (int* coefs = task.frame.subframes[ch].best.coefs)
                            {
                                if (!Lpc.EncodeResidual(task.frame.subframes[ch].best.residual, task.frame.subframes[ch].samples,
                                    task.frame.blocksize, task.frame.subframes[ch].best.order, coefs, task.frame.subframes[ch].best.shift))
                                {
                                    task.frame.subframes[ch].best.type = SubframeType.Verbatim;
                                    break;
                                }
                            }

                            calc_rice_params(task.frame.subframes[ch].best.rc, pmin, pmax, task.frame.subframes[ch].best.residual, (uint)task.frame.blocksize, (uint)task.frame.subframes[ch].best.order, Settings.PCM.BitsPerSample > 16 ? 1 : 0);
                        }
                        break;
                }
                if (!task.UseGPUOnly)
                {
                    long size = measure_subframe(task.frame, task.frame.subframes[ch]);
                    if (size > task.frame.subframes[ch].obits * task.frame.blocksize)
                    {
                        task.frame.subframes[ch].best.type = SubframeType.Verbatim;
                        task.frame.subframes[ch].best.size = (uint)(task.frame.subframes[ch].obits * task.frame.blocksize);
                    }
                }
            }
        }

        /// <summary>
        /// Copy channel-interleaved input samples into separate subframes
        /// </summary>
        /// <param name="task"></param>
        /// <param name="doMidside"></param>
        unsafe void unpack_samples_16(FLACCLTask task, byte* srcptr, int count)
        {
            short* src = (short*)srcptr;

            switch (task.frame.ch_mode)
            {
                case ChannelMode.NotStereo:
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int* s = task.frame.subframes[ch].samples;
                        int wbits = (int)task.frame.subframes[ch].wbits;
                        for (int i = 0; i < count; i++)
                            s[i] = src[i * channels + ch] >> wbits;
                    }
                    break;
                case ChannelMode.LeftRight:
                    {
                        int* left = task.frame.subframes[0].samples;
                        int* right = task.frame.subframes[1].samples;
                        int lwbits = (int)task.frame.subframes[0].wbits;
                        int rwbits = (int)task.frame.subframes[1].wbits;
                        for (int i = 0; i < count; i++)
                        {
                            int l = *(src++);
                            int r = *(src++);
                            left[i] = l >> lwbits;
                            right[i] = r >> rwbits;
                        }
                        break;
                    }
                case ChannelMode.LeftSide:
                    {
                        int* left = task.frame.subframes[0].samples;
                        int* right = task.frame.subframes[1].samples;
                        int lwbits = (int)task.frame.subframes[0].wbits;
                        int rwbits = (int)task.frame.subframes[1].wbits;
                        for (int i = 0; i < count; i++)
                        {
                            int l = *(src++);
                            int r = *(src++);
                            left[i] = l >> lwbits;
                            right[i] = (l - r) >> rwbits;
                        }
                        break;
                    }
                case ChannelMode.RightSide:
                    {
                        int* left = task.frame.subframes[0].samples;
                        int* right = task.frame.subframes[1].samples;
                        int lwbits = (int)task.frame.subframes[0].wbits;
                        int rwbits = (int)task.frame.subframes[1].wbits;
                        for (int i = 0; i < count; i++)
                        {
                            int l = *(src++);
                            int r = *(src++);
                            left[i] = (l - r) >> lwbits;
                            right[i] = r >> rwbits;
                        }
                        break;
                    }
                case ChannelMode.MidSide:
                    {
                        int* left = task.frame.subframes[0].samples;
                        int* right = task.frame.subframes[1].samples;
                        int lwbits = (int)task.frame.subframes[0].wbits;
                        int rwbits = (int)task.frame.subframes[1].wbits;
                        for (int i = 0; i < count; i++)
                        {
                            int l = *(src++);
                            int r = *(src++);
                            left[i] = (l + r) >> (1 + lwbits);
                            right[i] = (l - r) >> rwbits;
                        }
                        break;
                    }
            }
        }

        /// <summary>
        /// Copy channel-interleaved input samples into separate subframes
        /// </summary>
        /// <param name="task"></param>
        /// <param name="doMidside"></param>
        unsafe void unpack_samples_24(FLACCLTask task, byte* srcptr, int count)
        {
            switch (task.frame.ch_mode)
            {
                case ChannelMode.NotStereo:
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int* s = task.frame.subframes[ch].samples;
                        int wbits = (int)task.frame.subframes[ch].wbits;
                        byte* src = &srcptr[ch * 3];
                        int ba = Settings.PCM.BlockAlign;
                        for (int i = 0; i < count; i++)
                        {
                            s[i] = (((int)src[0] << 8) + ((int)src[1] << 16) + ((int)src[2] << 24)) >> (8 + wbits);
                            src += ba;
                        }
                    }
                    break;
                case ChannelMode.LeftRight:
                    {
                        int* left = task.frame.subframes[0].samples;
                        int* right = task.frame.subframes[1].samples;
                        int lwbits = (int)task.frame.subframes[0].wbits;
                        int rwbits = (int)task.frame.subframes[1].wbits;
                        for (int i = 0; i < count; i++)
                        {
                            int l = (((int)*(srcptr++) << 8) + ((int)*(srcptr++) << 16) + ((int)*(srcptr++) << 24)) >> 8;
                            int r = (((int)*(srcptr++) << 8) + ((int)*(srcptr++) << 16) + ((int)*(srcptr++) << 24)) >> 8;
                            left[i] = l >> lwbits;
                            right[i] = r >> rwbits;
                        }
                        break;
                    }
                case ChannelMode.LeftSide:
                    {
                        int* left = task.frame.subframes[0].samples;
                        int* right = task.frame.subframes[1].samples;
                        int lwbits = (int)task.frame.subframes[0].wbits;
                        int rwbits = (int)task.frame.subframes[1].wbits;
                        for (int i = 0; i < count; i++)
                        {
                            int l = (((int)*(srcptr++) << 8) + ((int)*(srcptr++) << 16) + ((int)*(srcptr++) << 24)) >> 8;
                            int r = (((int)*(srcptr++) << 8) + ((int)*(srcptr++) << 16) + ((int)*(srcptr++) << 24)) >> 8;
                            left[i] = l >> lwbits;
                            right[i] = (l - r) >> rwbits;
                        }
                        break;
                    }
                case ChannelMode.RightSide:
                    {
                        int* left = task.frame.subframes[0].samples;
                        int* right = task.frame.subframes[1].samples;
                        int lwbits = (int)task.frame.subframes[0].wbits;
                        int rwbits = (int)task.frame.subframes[1].wbits;
                        for (int i = 0; i < count; i++)
                        {
                            int l = (((int)*(srcptr++) << 8) + ((int)*(srcptr++) << 16) + ((int)*(srcptr++) << 24)) >> 8;
                            int r = (((int)*(srcptr++) << 8) + ((int)*(srcptr++) << 16) + ((int)*(srcptr++) << 24)) >> 8;
                            left[i] = (l - r) >> lwbits;
                            right[i] = r >> rwbits;
                        }
                        break;
                    }
                case ChannelMode.MidSide:
                    {
                        int* left = task.frame.subframes[0].samples;
                        int* right = task.frame.subframes[1].samples;
                        int lwbits = (int)task.frame.subframes[0].wbits;
                        int rwbits = (int)task.frame.subframes[1].wbits;
                        for (int i = 0; i < count; i++)
                        {
                            int l = (((int)*(srcptr++) << 8) + ((int)*(srcptr++) << 16) + ((int)*(srcptr++) << 24)) >> 8;
                            int r = (((int)*(srcptr++) << 8) + ((int)*(srcptr++) << 16) + ((int)*(srcptr++) << 24)) >> 8;
                            left[i] = (l + r) >> (1 + lwbits);
                            right[i] = (l - r) >> rwbits;
                        }
                        break;
                    }
            }
        }

        /// <summary>
        /// Copy channel-interleaved input samples into separate subframes
        /// </summary>
        /// <param name="task"></param>
        /// <param name="doMidside"></param>
        unsafe void unpack_samples(FLACCLTask task, int count)
        {
            int iFrame = task.frame.frame_number;
            byte* srcptr = ((byte*)task.clSamplesBytesPtr) + iFrame * task.frameSize * Settings.PCM.BlockAlign;
            if (Settings.PCM.BitsPerSample == 16)
                unpack_samples_16(task, srcptr, count);
            else if (Settings.PCM.BitsPerSample == 24)
                unpack_samples_24(task, srcptr, count);
            else
                throw new InvalidOperationException("Invalid BPS");
        }

        unsafe int encode_frame(bool doMidside, int channelCount, int iFrame, FLACCLTask task, int current_frame_number)
        {
            task.frame.InitSize(task.frameSize);
            task.frame.frame_number = iFrame;
            task.frame.ch_mode = ChannelMode.NotStereo;

            fixed (int* smp = task.samplesBuffer)
            {
                for (int ch = 0; ch < channelCount; ch++)
                    task.frame.subframes[ch].Init(
                        smp + ch * task.channelSize + iFrame * task.frameSize,
                        ((int*)task.clResidualPtr) + ch * task.channelSize + iFrame * task.frameSize,
                        Settings.PCM.BitsPerSample + (doMidside && ch == 3 ? 1 : 0), 0);

                encode_residual(task, channelCount, iFrame);

                task.frame.frame_number = current_frame_number;
                task.frame.writer_offset = task.frame.writer.Length;

                output_frame_header(task.frame);
                output_subframes(task);
                output_frame_footer(task.frame);
                if (task.frame.writer.Length - task.frame.writer_offset >= max_frame_size)
                    throw new InvalidOperationException("buffer overflow");

                return task.frame.writer.Length - task.frame.writer_offset;
            }
        }

        unsafe void send_to_GPU(FLACCLTask task, int nFrames, int blocksize)
        {
            bool doMidside = channels == 2 && eparams.do_midside;
            int channelsCount = doMidside ? 2 * channels : channels;
            if (blocksize != task.frameSize)
                task.nResidualTasks = 0;
            task.frameCount = nFrames;
            task.frameSize = blocksize;
            task.frameNumber = frame_count;
            task.framePos = frame_pos;
            frame_count += nFrames;
            frame_pos += nFrames * blocksize;
            task.openCLCQ.EnqueueWriteBuffer(task.clSamplesBytes, false, 0, Settings.PCM.BlockAlign * blocksize * nFrames, task.clSamplesBytesPtr);
        }

        unsafe void run_GPU_task(FLACCLTask task)
        {
            bool doMidside = channels == 2 && eparams.do_midside;
            int channelsCount = doMidside ? 2 * channels : channels;

            if (task.nResidualTasks == 0)
                initializeSubframeTasks(task.frameSize, channelsCount, framesPerTask, task);

            if (task.frameSize > Settings.MaxLPCOrder)
                task.EnqueueKernels();
            task.openCLCQ.Flush();
        }

        unsafe void process_result(FLACCLTask task)
        {
            bool doMidside = channels == 2 && eparams.do_midside;
            int channelCount = doMidside ? 2 * channels : channels;

            long iSample = 0;
            int iByte = 0;
            long batchOffset = seek_table is null ? 0 : _IO.Position - first_frame_offset;
            task.frame.writer.Reset();
            task.frame.writer_offset = 0;
            for (int iFrame = 0; iFrame < task.frameCount; iFrame++)
            {
                int fn = task.frameNumber + iFrame;
                int fs = encode_frame(doMidside, channelCount, iFrame, task, fn);

                if (seek_table != null && _IO.CanSeek)
                {
                    long frameStart = task.framePos + iSample;
                    if (nextSeekPoint < seek_table.Length && seek_table[nextSeekPoint].number < frameStart + task.frameSize)
                    {
                        ref SeekPoint point = ref seek_table[nextSeekPoint++];
                        point.number = frameStart;
                        point.offset = batchOffset + iByte;
                        point.framesize = task.frameSize;
                    }
                }

                iSample += task.frameSize;
                iByte += fs;
            }
            task.outputSize = iByte;
            if (iByte != task.frame.writer.Length)
                throw new InvalidOperationException("invalid length");
        }

        unsafe void write_result(FLACCLTask task)
        {
            int iSample = task.frameSize * task.frameCount;

            _IO.Write(task.outputBuffer, 0, task.outputSize);
            _position += iSample;
        }

        int select_blocksize(int samplerate, int time_ms)
        {
            int blocksize = FlakeConstants.flac_blocksizes[1];
            int target = (samplerate * time_ms) / 1000;

            for (int i = 8; i < FlakeConstants.flac_blocksizes.Length; i++)
                if (target >= FlakeConstants.flac_blocksizes[i] && FlakeConstants.flac_blocksizes[i] > blocksize)
                {
                    blocksize = FlakeConstants.flac_blocksizes[i];
                }
            return blocksize;
        }

        void write_streaminfo(byte[] header, int pos, int last)
        {
            Array.Clear(header, pos, 38);
            BitWriter bitwriter = new BitWriter(header, pos, 38);

            // metadata header
            bitwriter.writebits(1, last);
            bitwriter.writebits(7, (int)MetadataType.StreamInfo);
            bitwriter.writebits(24, 34);

            bitwriter.writebits(16, m_blockSize);

            bitwriter.writebits(16, m_blockSize);
            bitwriter.writebits(24, 0);
            bitwriter.writebits(24, max_frame_size);
            bitwriter.writebits(20, m_settings.PCM.SampleRate);
            bitwriter.writebits(3, channels - 1);
            bitwriter.writebits(5, bits_per_sample - 1);

            // total samples
            bitwriter.writebits(36, (ulong)Math.Max(0, sample_count));
            bitwriter.flush();
        }

        /**
         * Write vorbis comment metadata block to byte array.
         * Just writes the vendor string for now.
         */
        int write_vorbis_comment(byte[] comment, int pos, int last)
        {
            BitWriter bitwriter = new BitWriter(comment, pos, 4);
            Span<byte> metadata = comment.AsSpan(pos);
            int vendorLength = Encoding.ASCII.GetBytes(Vendor, metadata[8..]);
            BinaryPrimitives.WriteInt32LittleEndian(metadata[4..], vendorLength);
            int offset = 8 + vendorLength;
            uint mask = Settings.PCM.ChannelMask;
            BinaryPrimitives.WriteInt32LittleEndian(metadata[offset..], mask == 0 ? 0 : 1);
            offset += 4;
            if (mask != 0)
            {
                int length = Encoding.ASCII.GetBytes($"WAVEFORMATEXTENSIBLE_CHANNEL_MASK=0x{mask:X}", metadata[(offset + 4)..]);
                BinaryPrimitives.WriteInt32LittleEndian(metadata[offset..], length);
                offset += 4 + length;
            }
            bitwriter.writebits(1, last);
            bitwriter.writebits(7, (int)MetadataType.VorbisComment);
            bitwriter.writebits(24, offset - 4);
            bitwriter.flush();
            return offset;
        }

        int write_seekpoints(byte[] header, int pos, int last)
        {
            seek_table_offset = pos + 4;

            SeekPoint[] points = seek_table ?? throw new InvalidOperationException("No seek table was initialized.");
            BitWriter bitwriter = new BitWriter(header, pos, 4 + 18 * points.Length);

            // metadata header
            bitwriter.writebits(1, last);
            bitwriter.writebits(7, (int)MetadataType.Seektable);
            bitwriter.writebits(24, 18 * points.Length);
            foreach (SeekPoint point in points)
            {
                bitwriter.writebits(64, (ulong)point.number);
                bitwriter.writebits(64, (ulong)point.offset);
                bitwriter.writebits(16, point.framesize);
            }
            bitwriter.flush();
            return 4 + 18 * points.Length;
        }

        /**
         * Write padding metadata block to byte array.
         */
        int
        write_padding(byte[] padding, int pos, int last, long padlen)
        {
            BitWriter bitwriter = new BitWriter(padding, pos, 4);

            // metadata header
            bitwriter.writebits(1, last);
            bitwriter.writebits(7, (int)MetadataType.Padding);
            bitwriter.writebits(24, (int)padlen);
            bitwriter.flush();
            return (int)padlen + 4;
        }

        int write_headers()
        {
            int header_size = 0;
            int last = 0;

            // stream marker
            header[0] = 0x66;
            header[1] = 0x4C;
            header[2] = 0x61;
            header[3] = 0x43;
            header_size += 4;

            // streaminfo
            write_streaminfo(header, header_size, last);
            header_size += 38;

            // seek table
            if (_IO.CanSeek && seek_table != null)
                header_size += write_seekpoints(header, header_size, last);

            // vorbis comment
            if (m_settings.Padding == 0) last = 1;
            header_size += write_vorbis_comment(header, header_size, last);

            // padding
            if (m_settings.Padding > 0)
            {
                last = 1;
                header_size += write_padding(header, header_size, last, m_settings.Padding);
            }

            return header_size;
        }

        int flake_encode_init()
        {
            int i, header_len;

            ch_code = channels - 1;

            // find samplerate in table
            for (i = 1; i < 12; i++)
            {
                if (m_settings.PCM.SampleRate == FlakeConstants.flac_samplerates[i])
                {
                    sr_code0 = i;
                    break;
                }
            }

            // if not in table, samplerate is non-standard
            if (i == 12)
            {
                int rate = Settings.PCM.SampleRate;
                if (rate % 1000 == 0 && rate / 1000 <= 255)
                    (sr_code0, sr_code1) = (12, rate / 1000);
                else if (rate <= 65535)
                    (sr_code0, sr_code1) = (13, rate);
                else if (rate % 10 == 0 && rate / 10 <= 65535)
                    (sr_code0, sr_code1) = (14, rate / 10);
                else if (!Settings.AllowNonSubset)
                    throw new InvalidDataException("This sample rate requires --lax.");
            }

            for (i = 1; i < 8; i++)
            {
                if (bits_per_sample == FlakeConstants.flac_bitdepths[i])
                {
                    bps_code = i;
                    break;
                }
            }
            if (i == 8)
                throw new InvalidOperationException("non-standard bps");

            // set maximum encoded frame size (if larger, re-encodes in verbatim mode)
            if (channels == 2)
                max_frame_size = 32 + ((m_blockSize * (int)(bits_per_sample + bits_per_sample + 1) + 7) >> 3);
            else
                max_frame_size = 32 + ((m_blockSize * channels * (int)bits_per_sample + 7) >> 3);

            if (_IO.CanSeek && eparams.do_seektable && sample_count > 0)
            {
                long seek_points_distance = Math.Max(m_blockSize, Math.Max(m_settings.PCM.SampleRate * 10L, (sample_count + 16383) / 16384));
                int num_seek_points = checked((int)((sample_count + seek_points_distance - 1) / seek_points_distance));
                seek_table = new SeekPoint[num_seek_points];
                for (int sp = 0; sp < num_seek_points; sp++)
                {
                    seek_table[sp].framesize = 0;
                    seek_table[sp].offset = 0;
                    seek_table[sp].number = sp * seek_points_distance;
                }
            }

            // output header bytes
            header = new byte[m_settings.Padding + 1024 + (seek_table == null ? 0 : seek_table.Length * 18)];
            header_len = write_headers();

            // initialize CRC & MD5
            if (_IO.CanSeek && m_settings.DoMD5)
                md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

            return header_len;
        }
    }

    struct FlakeEncodeParams
    {
        // stereo decorrelation method
        // set by user prior to calling flake_encode_init
        // if set to less than 0, it is chosen based on compression.
        // valid values are 0 to 2
        // 0 = independent L+R channels
        // 1 = mid-side encoding
        public bool do_midside;

        // block time in milliseconds
        // set by the user prior to calling flake_encode_init
        // used to calculate block_size based on sample rate
        // can also be changed by user before encoding a frame
        public int block_time_ms;

        public int orders_per_window;

        public int orders_per_channel;

        // whether to use variable block sizes
        // set by user prior to calling flake_encode_init
        // 0 = fixed block size
        // 1 = variable block size

        // whether to try various lpc_precisions
        // 0 - use only one precision
        // 1 - try two precisions
        public int lpc_max_precision_search;

        public int lpc_min_precision_search;

        public bool do_wasted;

        public bool do_constant;


        public WindowFunction window_function;

        public bool do_seektable;

        public int flake_set_defaults(EncoderSettings settings)
        {
            int lvl = settings.Level;
            // default to level 5 params
            window_function = WindowFunction.Flattop | WindowFunction.Tukey;
            do_midside = true;
            block_time_ms = 100;
            lpc_min_precision_search = 0;
            lpc_max_precision_search = 0;
            orders_per_channel = 32;
            do_seektable = settings.DoSeekTable;
            do_wasted = true;
            do_constant = true;

            // differences from level 7
            switch (lvl)
            {
                case 0:
                    do_constant = false;
                    do_wasted = false;
                    do_midside = false;
                    window_function = WindowFunction.Bartlett;
                    orders_per_window = 1;
                    break;
                case 1:
                    do_constant = false;
                    do_wasted = false;
                    do_midside = false;
                    window_function = WindowFunction.Bartlett;
                    orders_per_window = 1;
                    break;
                case 2:
                    do_constant = false;
                    do_midside = false;
                    window_function = WindowFunction.Bartlett;
                    orders_per_window = 1;
                    break;
                case 3:
                    do_constant = false;
                    orders_per_window = 1;
                    orders_per_channel = 1;
                    break;
                case 4:
                    do_constant = false;
                    orders_per_window = 2;
                    orders_per_channel = 2;
                    break;
                case 5:
                    do_constant = false;
                    orders_per_window = 4;
                    orders_per_channel = 4;
                    break;
                case 6:
                    do_constant = false;
                    orders_per_window = 2;
                    orders_per_channel = 2;
                    break;
                case 7:
                    do_constant = false;
                    orders_per_window = 4;
                    orders_per_channel = 4;
                    break;
                case 8:
                    orders_per_window = 8;
                    orders_per_channel = 8;
                    break;
                case 9:
                    orders_per_window = 4;
                    orders_per_channel = 4;
                    break;
                case 10:
                    orders_per_window = 7;
                    break;
                case 11:
                    orders_per_window = 11;
                    break;
            }
            return 0;
        }
    }

#pragma warning disable CS0649 // These fields share the layout of the OpenCL task structure.
    [StructLayout(LayoutKind.Sequential)]
    unsafe struct FLACCLSubframeTask
    {
        public int residualOrder;
        public int samplesOffs;
        public int shift;
        public int cbits;
        public int size;
        public int type;
        public int obits;
        public int blocksize;
        public int coding_method;
        public int channel;
        public int residualOffs;
        public int wbits;
        public int abits;
        public int porder;
        public int headerLen;
        public int encodingOffset;
        public fixed int coefs[32];
    }
#pragma warning restore CS0649

    internal class FLACCLTask
    {
        CLProgram openCLProgram;
        public CommandQueue openCLCQ;
        public Kernel clStereoDecorr;
        public Kernel clChannelDecorr2;
        public Kernel clChannelDecorrX;
        public Kernel clFindWastedBits;
        public Kernel clComputeAutocor;
        public Kernel clComputeLPC;
        public Kernel clQuantizeLPC;
        public Kernel clSelectStereoTasks;
        public Kernel clEstimateResidual;
        public Kernel clChooseBestMethod;
        public Kernel clEncodeResidual = null!;
        public Kernel clCalcPartition = null!;
        public Kernel clCalcPartition16 = null!;
        public Kernel clCalcPartition32 = null!;
        public Kernel clSumPartition = null!;
        public Kernel clFindRiceParameter = null!;
        public Kernel clFindPartitionOrder = null!;
        public Mem clSamplesBytes;
        public Mem clSamples;
        public Mem clLPCData;
        public Mem clResidual;
        public Mem clPartitions = null!;
        public Mem clRiceParams = null!;
        public Mem clBestRiceParams;
        public Mem clAutocorOutput;
        public Mem clSelectedTasks;
        public Mem clSelectedTasksSecondEstimate;
        public Mem clSelectedTasksBestMethod;
        public Mem clResidualTasks;
        public Mem clBestResidualTasks;
        public Mem clWindowFunctions;

        public Mem clSamplesBytesPinned;
        public Mem clResidualPinned;
        public Mem clBestRiceParamsPinned;
        public Mem clResidualTasksPinned;
        public Mem clBestResidualTasksPinned;
        public Mem clWindowFunctionsPinned;
        public Mem clSelectedTasksPinned;

        public IntPtr clSamplesBytesPtr;
        public IntPtr clResidualPtr;
        public IntPtr clBestRiceParamsPtr;
        public IntPtr clResidualTasksPtr;
        public IntPtr clBestResidualTasksPtr;
        public IntPtr clWindowFunctionsPtr;
        public IntPtr clSelectedTasksPtr;

        public int[] samplesBuffer;
        public byte[] outputBuffer;
        public int outputSize = 0;
        public int channelSize = 0;
        public int frameSize = 0;
        public int frameCount = 0;
        public int frameNumber = 0;
        public long framePos = 0;
        public FlacFrame frame;
        public int residualTasksLen;
        public int bestResidualTasksLen;
        public int nResidualTasks = 0;
        public int nResidualTasksPerChannel = 0;
        public int nEstimateTasksPerChannel = 0;
        public int nTasksPerWindow = 0;
        public int nWindowFunctions = 0;
        public int max_porder = 0;

        public int groupSize = 128;
        public int channels, channelsCount;
        public AudioEncoder writer;
        public bool UseGPUOnly = false;

        unsafe public FLACCLTask(CLProgram _openCLProgram, int channelsCount, int max_frame_size, AudioEncoder writer, int groupSize, bool gpuOnly)
        {
            this.UseGPUOnly = gpuOnly;
            this.groupSize = groupSize;
            this.channels = writer.Settings.PCM.ChannelCount;
            this.channelsCount = channelsCount;
            this.writer = writer;
            openCLProgram = _openCLProgram;
            openCLCQ = openCLProgram.CreateCommandQueue();

            int MAX_ORDER = this.writer.m_settings.MaxLPCOrder;
            int MAX_FRAMES = this.writer.framesPerTask;
            int MAX_CHANNELSIZE = MAX_FRAMES * ((writer.m_blockSize + 3) & ~3);
            residualTasksLen = sizeof(FLACCLSubframeTask) * 32 * channelsCount * MAX_FRAMES;
            bestResidualTasksLen = sizeof(FLACCLSubframeTask) * channels * MAX_FRAMES;
            int samplesBytesLen = writer.Settings.PCM.BlockAlign * MAX_CHANNELSIZE;
            int samplesBufferLen =  sizeof(int) * MAX_CHANNELSIZE * channelsCount;
            int residualBufferLen = sizeof(int) * MAX_CHANNELSIZE * channels; // need to adjust residualOffset?
            int partitionsLen = sizeof(int) * ((writer.Settings.PCM.BitsPerSample > 16 ? 31 : 15) * 2 << 8) * channels * MAX_FRAMES;
            int riceParamsLen = sizeof(int) * (4 << 8) * channels * MAX_FRAMES;
            int autocorLen = sizeof(float) * (MAX_ORDER + 1) * AudioEncoder.MAX_LPC_WINDOWS * channelsCount * MAX_FRAMES;
            int lpcDataLen = autocorLen * 32;
            int wndLen = sizeof(float) * MAX_CHANNELSIZE /** 2*/ * AudioEncoder.MAX_LPC_WINDOWS;
            int selectedLen = sizeof(int) * 32 * channelsCount * MAX_FRAMES;

            clSamplesBytesPinned = openCLProgram.CreateBuffer(MemFlags.READ_WRITE | MemFlags.ALLOC_HOST_PTR, samplesBytesLen);
            clResidualPinned = openCLProgram.CreateBuffer(MemFlags.READ_WRITE | MemFlags.ALLOC_HOST_PTR, residualBufferLen);
            clBestRiceParamsPinned = openCLProgram.CreateBuffer(MemFlags.READ_WRITE | MemFlags.ALLOC_HOST_PTR, riceParamsLen / 4);
            clResidualTasksPinned = openCLProgram.CreateBuffer(MemFlags.READ_WRITE | MemFlags.ALLOC_HOST_PTR, residualTasksLen);
            clBestResidualTasksPinned = openCLProgram.CreateBuffer(MemFlags.READ_WRITE | MemFlags.ALLOC_HOST_PTR, bestResidualTasksLen);
            clWindowFunctionsPinned = openCLProgram.CreateBuffer(MemFlags.READ_WRITE | MemFlags.ALLOC_HOST_PTR, wndLen);
            clSelectedTasksPinned = openCLProgram.CreateBuffer(MemFlags.READ_WRITE | MemFlags.ALLOC_HOST_PTR, selectedLen);

            clSamplesBytesPtr = openCLCQ.MapStagingBuffer(clSamplesBytesPinned, samplesBytesLen);
            clResidualPtr = openCLCQ.MapStagingBuffer(clResidualPinned, residualBufferLen);
            clBestRiceParamsPtr = openCLCQ.MapStagingBuffer(clBestRiceParamsPinned, riceParamsLen / 4);
            clResidualTasksPtr = openCLCQ.MapStagingBuffer(clResidualTasksPinned, residualTasksLen);
            clBestResidualTasksPtr = openCLCQ.MapStagingBuffer(clBestResidualTasksPinned, bestResidualTasksLen);
            clWindowFunctionsPtr = openCLCQ.MapStagingBuffer(clWindowFunctionsPinned, wndLen);
            clSelectedTasksPtr = openCLCQ.MapStagingBuffer(clSelectedTasksPinned, selectedLen);

            openCLCQ.Finish();

            clSamplesBytes = openCLProgram.CreateBuffer(MemFlags.READ_WRITE, samplesBytesLen);
            clResidual = openCLProgram.CreateBuffer(MemFlags.READ_WRITE, residualBufferLen);
            clBestRiceParams = openCLProgram.CreateBuffer(MemFlags.READ_WRITE, riceParamsLen / 4);
            clResidualTasks = openCLProgram.CreateBuffer(MemFlags.READ_WRITE, residualTasksLen);
            clBestResidualTasks = openCLProgram.CreateBuffer(MemFlags.READ_WRITE, bestResidualTasksLen);
            clWindowFunctions = openCLProgram.CreateBuffer(MemFlags.READ_WRITE, wndLen);
            clSelectedTasks = openCLProgram.CreateBuffer(MemFlags.READ_WRITE, selectedLen);

            clSamples = openCLProgram.CreateBuffer(MemFlags.READ_WRITE, samplesBufferLen);
            clLPCData = openCLProgram.CreateBuffer(MemFlags.READ_WRITE, lpcDataLen);
            clAutocorOutput = openCLProgram.CreateBuffer(MemFlags.READ_WRITE, autocorLen);
            clSelectedTasksSecondEstimate = openCLProgram.CreateBuffer(MemFlags.READ_WRITE, selectedLen);
            clSelectedTasksBestMethod = openCLProgram.CreateBuffer(MemFlags.READ_WRITE, selectedLen);
            if (UseGPUOnly)
            {
                clPartitions = openCLProgram.CreateBuffer(MemFlags.READ_WRITE, partitionsLen);
                clRiceParams = openCLProgram.CreateBuffer(MemFlags.READ_WRITE, riceParamsLen);
            }

            clComputeAutocor = openCLProgram.CreateKernel("clComputeAutocor");
            clStereoDecorr = openCLProgram.CreateKernel("clStereoDecorr");
            clChannelDecorr2 = openCLProgram.CreateKernel("clChannelDecorr2");
            clChannelDecorrX = openCLProgram.CreateKernel("clChannelDecorrX");
            clFindWastedBits = openCLProgram.CreateKernel("clFindWastedBits");
            clComputeLPC = openCLProgram.CreateKernel("clComputeLPC");
            clQuantizeLPC = openCLProgram.CreateKernel("clQuantizeLPC");
            clSelectStereoTasks = openCLProgram.CreateKernel("clSelectStereoTasks");
            clEstimateResidual = openCLProgram.CreateKernel("clEstimateResidual");
            clChooseBestMethod = openCLProgram.CreateKernel("clChooseBestMethod");
            if (UseGPUOnly)
            {
                clEncodeResidual = openCLProgram.CreateKernel("clEncodeResidual");
                if (!openCLCQ.Device.IsCpu)
                {
                    clCalcPartition = openCLProgram.CreateKernel("clCalcPartition");
                    clCalcPartition16 = openCLProgram.CreateKernel("clCalcPartition16");
                    clCalcPartition32 = openCLProgram.CreateKernel("clCalcPartition32");
                }
                clSumPartition = openCLProgram.CreateKernel("clSumPartition");
                clFindRiceParameter = openCLProgram.CreateKernel("clFindRiceParameter");
                clFindPartitionOrder = openCLProgram.CreateKernel("clFindPartitionOrder");

            }

            samplesBuffer = new int[MAX_CHANNELSIZE * channelsCount];
            outputBuffer = new byte[max_frame_size * MAX_FRAMES + 1];
            frame = new FlacFrame(channelsCount, outputBuffer);


        }

        public unsafe FLACCLSubframeTask* ResidualTasks
        {
            get
            {
                return (FLACCLSubframeTask*)clResidualTasksPtr;
            }
        }

        public unsafe FLACCLSubframeTask* BestResidualTasks
        {
            get
            {
                return (FLACCLSubframeTask*)clBestResidualTasksPtr;
            }
        }

        internal unsafe void EnqueueKernels()
        {
            var eparams = writer.eparams;
            var settings = writer.Settings;

            this.max_porder = AudioEncoder.get_max_p_order(settings.MaxPartitionOrder, frameSize, settings.MaxLPCOrder);
            while ((frameSize >> max_porder) < 16 && max_porder > 0)
                this.max_porder--;

            // issue work to the GPU
            if (channels == 2)
            {
                Kernel clChannelDecorr = channelsCount == 4 ? clStereoDecorr : clChannelDecorr2;
                bool packed16 = writer.Settings.PCM.BitsPerSample == 16;
                int channelSize1 = packed16 ? channelSize / 4 : channelSize;
                int activeSamples = frameSize * frameCount;
                int workItems = packed16 ? (activeSamples + 3) / 4 : activeSamples;
                clChannelDecorr.SetArgs(
                    clSamples,
                    clSamplesBytes,
                    channelSize1);

                openCLCQ.EnqueueNDRangeKernel(
                    clChannelDecorr,
                    0,
                    workItems);
            }
            else
            {
                // clSamplesBytes length = blockSize * nFrames * blockalign
                // clSamples length = 4 * blockSize * nFrames * channels
                clChannelDecorrX.SetArgs(
                    clSamples,
                    clSamplesBytes,
                    channelSize);

                openCLCQ.EnqueueNDRangeKernel(
                    clChannelDecorrX,
                    0,
                    frameSize * frameCount);
            }

            if (eparams.do_wasted)
            {
                clFindWastedBits.SetArgs(
                    clResidualTasks,
                    clSamples,
                    nResidualTasksPerChannel);

                openCLCQ.EnqueueNDRangeKernel(
                    clFindWastedBits,
                    groupSize,
                    frameCount * channelsCount);
            }

            clComputeAutocor.SetArgs(
                clAutocorOutput,
                clSamples,
                clWindowFunctions,
                clResidualTasks,
                nResidualTasksPerChannel);

            openCLCQ.EnqueueNDRangeKernel(
                clComputeAutocor,
                groupSize, 1,
                channelsCount * frameCount,
                nWindowFunctions);

            clComputeLPC.SetArgs(
                clAutocorOutput,
                clLPCData,
                nWindowFunctions);

            //    clComputeLPC,
            //    64, 1,
            //    nWindowFunctions,
            //    frameCount);
            openCLCQ.EnqueueNDRangeKernel(
                clComputeLPC,
                Math.Min(groupSize, 32), 1,
                nWindowFunctions,
                channelsCount * frameCount);

            clQuantizeLPC.SetArgs(
                clResidualTasks,
                clLPCData,
                nResidualTasksPerChannel,
                nTasksPerWindow,
                eparams.lpc_min_precision_search,
                eparams.lpc_max_precision_search - eparams.lpc_min_precision_search);

            openCLCQ.EnqueueNDRangeKernel(
                clQuantizeLPC,
                Math.Min(groupSize, 32), 1,
                nWindowFunctions,
                channelsCount * frameCount);

            if (channels == 2 && channelsCount == 4)
            {
                clEstimateResidual.SetArgs(
                    clSamples,
                    clSelectedTasks,
                    clResidualTasks);

                openCLCQ.EnqueueNDRangeKernel(
                    clEstimateResidual,
                    groupSize,
                    nEstimateTasksPerChannel * channelsCount * frameCount); // 1 per channel, 4 channels

                int tasksToSecondEstimate = nResidualTasksPerChannel - nEstimateTasksPerChannel;

                clSelectStereoTasks.SetArgs(
                    clResidualTasks,
                    clSelectedTasks,
                    clSelectedTasksSecondEstimate,
                    clSelectedTasksBestMethod,
                    nTasksPerWindow,
                    nWindowFunctions,
                    tasksToSecondEstimate,
                    nResidualTasksPerChannel,
                    nEstimateTasksPerChannel);

                openCLCQ.EnqueueNDRangeKernel(
                    clSelectStereoTasks,
                    0, frameCount);

                if (tasksToSecondEstimate > 0)
                {
                    clEstimateResidual.SetArgs(
                        clSamples,
                        clSelectedTasksSecondEstimate,
                        clResidualTasks);

                    openCLCQ.EnqueueNDRangeKernel(
                        clEstimateResidual,
                        groupSize,
                        tasksToSecondEstimate * channels * frameCount);
                }

                clChooseBestMethod.SetArgs(
                    clBestResidualTasks,
                    clResidualTasks,
                    clSelectedTasksBestMethod,
                    nResidualTasksPerChannel);

                openCLCQ.EnqueueNDRangeKernel(
                    clChooseBestMethod,
                    0, channels * frameCount);
            }
            else
            {
                clEstimateResidual.SetArgs(
                    clSamples,
                    clSelectedTasks,
                    clResidualTasks);

                openCLCQ.EnqueueNDRangeKernel(
                    clEstimateResidual,
                    groupSize,
                    nResidualTasksPerChannel * channelsCount * frameCount);

                clChooseBestMethod.SetArgs(
                    clBestResidualTasks,
                    clResidualTasks,
                    clSelectedTasks,
                    nResidualTasksPerChannel);

                openCLCQ.EnqueueNDRangeKernel(
                    clChooseBestMethod,
                    0, channels * frameCount);
            }

            if (UseGPUOnly)
            {
                clEncodeResidual.SetArgs(
                    clPartitions,
                    clResidual,
                    clSamples,
                    clBestResidualTasks,
                    max_porder,
                    frameSize >> max_porder);

                openCLCQ.EnqueueNDRangeKernel(
                    clEncodeResidual,
                    groupSize, channels * frameCount);

                if (!openCLCQ.Device.IsCpu)
                {
                    if (frameSize >> max_porder == 16)
                    {
                        clCalcPartition16.SetArgs(
                            clPartitions,
                            clResidual,
                            clBestResidualTasks,
                            max_porder);

                        openCLCQ.EnqueueNDRangeKernel(
                            clCalcPartition16,
                            groupSize, channels * frameCount);
                    }
                    else if (frameSize >> max_porder == 32)
                    {
                        clCalcPartition32.SetArgs(
                            clPartitions,
                            clResidual,
                            clBestResidualTasks,
                            max_porder);

                        openCLCQ.EnqueueNDRangeKernel(
                            clCalcPartition32,
                            groupSize, channels * frameCount);
                    }
                    else
                    {
                        clCalcPartition.SetArgs(
                            clPartitions,
                            clResidual,
                            clBestResidualTasks,
                            max_porder,
                            frameSize >> max_porder);

                        openCLCQ.EnqueueNDRangeKernel(
                            clCalcPartition,
                            groupSize, 1,
                            1 + ((1 << max_porder) - 1) / (groupSize / 16),
                            channels * frameCount);
                    }
                }

                if (max_porder > 0)
                {
                    clSumPartition.SetArgs(
                        clPartitions,
                        max_porder);

                    int maxK = writer.Settings.PCM.BitsPerSample > 16 ? 30 : FlakeConstants.MAX_RICE_PARAM;
                    if (openCLCQ.Device.IsCpu)
                        openCLCQ.EnqueueNDRangeKernel(
                            clSumPartition,
                            1, 1, 1,
                            channels * frameCount);
                    else
                        openCLCQ.EnqueueNDRangeKernel(
                            clSumPartition,
                            128, 1,
                            (maxK + 1),
                            channels * frameCount);
                }

                clFindRiceParameter.SetArgs(
                    clBestResidualTasks,
                    clRiceParams,
                    clPartitions,
                    max_porder);

                openCLCQ.EnqueueNDRangeKernel(
                    clFindRiceParameter,
                    groupSize, channels * frameCount);

                clFindPartitionOrder.SetArgs(
                    clResidual,
                    clBestRiceParams,
                    clBestResidualTasks,
                    clRiceParams,
                    max_porder);

                openCLCQ.EnqueueNDRangeKernel(
                    clFindPartitionOrder,
                    groupSize,
                    channels * frameCount);

                openCLCQ.EnqueueReadBuffer(clBestRiceParams, false, 0,
                    sizeof(int) * (1 << max_porder) * channels * frameCount, clBestRiceParamsPtr);
                openCLCQ.EnqueueReadBuffer(clResidual, false, 0,
                    sizeof(int) * channelSize * channels, clResidualPtr);
            }
            openCLCQ.EnqueueReadBuffer(clBestResidualTasks, false, 0, sizeof(FLACCLSubframeTask) * channels * frameCount, clBestResidualTasksPtr);
        }
    }

}
