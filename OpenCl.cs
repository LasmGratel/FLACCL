// SPDX-License-Identifier: LGPL-2.1-only

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace FLACCL;

internal sealed class OpenClException : Exception
{
    internal OpenClException(string message) : base(message) { }
}

internal sealed record Device(nint Id, string Platform, string Name, string Version,
    ulong Type, uint VendorId, uint ComputeUnits, nuint MaxWorkGroupSize, string Extensions)
{
    internal const ulong Cpu = 1 << 1;
    internal const ulong Gpu = 1 << 2;
    internal bool IsCpu => (Type & Cpu) != 0;
    internal bool IsGpu => (Type & Gpu) != 0;
    internal bool Supports(string extension) => Extensions.Split(' ').Contains(extension);
}

[Flags]
internal enum MemFlags : ulong
{
    READ_WRITE = 1,
    ALLOC_HOST_PTR = 1 << 4
}

internal sealed record Mem(nint Handle);

internal sealed unsafe class Kernel
{
    internal nint Handle { get; }

    internal Kernel(nint handle) => Handle = handle;

    internal void SetArgs(params object[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            int status;
            if (args[i] is int value)
                status = Cl.clSetKernelArg(Handle, (uint)i, sizeof(int), &value);
            else if (args[i] is Mem buffer)
            {
                nint address = buffer.Handle;
                status = Cl.clSetKernelArg(Handle, (uint)i, (nuint)sizeof(nint), &address);
            }
            else
                throw new ArgumentException("Invalid OpenCL kernel argument.");
            Cl.Check(status, "clSetKernelArg");
        }
    }
}

internal sealed unsafe class CommandQueue
{
    private readonly nint handle;
    private readonly List<(Mem Buffer, nint Pointer)> mappings = new();
    internal Device Device { get; }

    internal CommandQueue(nint handle, Device device)
    {
        this.handle = handle;
        Device = device;
    }

    internal nint MapStagingBuffer(Mem buffer, int size)
    {
        const uint blocking = 1;
        const ulong readAndWrite = 3;
        void* pointer = Cl.clEnqueueMapBuffer(handle, buffer.Handle, blocking, readAndWrite,
            0, (nuint)size, 0, null, null, out int status);
        Cl.Check(status, "clEnqueueMapBuffer");
        mappings.Add((buffer, (nint)pointer));
        return (nint)pointer;
    }

    internal void EnqueueWriteBuffer(Mem buffer, bool blocking, int offset, int size, nint address) =>
        Cl.Check(Cl.clEnqueueWriteBuffer(handle, buffer.Handle, blocking ? 1u : 0u,
            (nuint)offset, (nuint)size, (void*)address, 0, null, null), "clEnqueueWriteBuffer");

    internal void EnqueueReadBuffer(Mem buffer, bool blocking, int offset, int size, nint address) =>
        Cl.Check(Cl.clEnqueueReadBuffer(handle, buffer.Handle, blocking ? 1u : 0u,
            (nuint)offset, (nuint)size, (void*)address, 0, null, null), "clEnqueueReadBuffer");

    internal void EnqueueNDRangeKernel(Kernel kernel, int localSize, int groupCount)
    {
        var local = (nuint)localSize;
        var global = (nuint)groupCount * (localSize == 0 ? 1u : local);
        Cl.Check(Cl.clEnqueueNDRangeKernel(handle, kernel.Handle, 1, null, &global,
            localSize == 0 ? null : &local, 0, null, null), "clEnqueueNDRangeKernel");
    }

    internal void EnqueueNDRangeKernel(Kernel kernel, int localX, int localY, int groupsX, int groupsY)
    {
        nuint* local = stackalloc nuint[] { (nuint)localX, (nuint)localY };
        nuint* global = stackalloc nuint[] { (nuint)localX * (nuint)groupsX, (nuint)localY * (nuint)groupsY };
        Cl.Check(Cl.clEnqueueNDRangeKernel(handle, kernel.Handle, 2, null, global, local,
            0, null, null), "clEnqueueNDRangeKernel");
    }

    internal void Finish() => Cl.Check(Cl.clFinish(handle), "clFinish");
    internal void Flush() => Cl.Check(Cl.clFlush(handle), "clFlush");

    internal void UnmapStagingBuffers()
    {
        Cl.clFinish(handle);
        foreach (var (buffer, pointer) in mappings)
            Cl.clEnqueueUnmapMemObject(handle, buffer.Handle, (void*)pointer, 0, null, null);
        Cl.clFinish(handle);
    }
}

internal sealed unsafe class CLProgram : IDisposable
{
    private readonly List<CommandQueue> queues = new();
    private readonly List<nint> queueHandles = new();
    private readonly List<nint> buffers = new();
    private readonly List<nint> kernels = new();
    private nint context;
    private nint program;
    private readonly Device device;

    internal CLProgram(Device device, string defines)
    {
        this.device = device;
        using Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream("flac.cl")
            ?? throw new InvalidOperationException("The embedded OpenCL kernel is missing.");
        using var reader = new StreamReader(source);
        byte[] programBytes = Encoding.UTF8.GetBytes(defines + reader.ReadToEnd());
        nint id = device.Id;

        try
        {
            context = Cl.clCreateContext(null, 1, &id, 0, 0, out int status);
            Cl.Check(status, "clCreateContext");
            fixed (byte* bytes = programBytes)
            {
                byte* code = bytes;
                var length = (nuint)programBytes.Length;
                program = Cl.clCreateProgramWithSource(context, 1, &code, &length, out status);
                Cl.Check(status, "clCreateProgramWithSource");
            }

            int buildStatus = Cl.clBuildProgram(program, 1, &id, "-cl-std=CL1.2", 0, 0);
            if (buildStatus != 0)
            {
                string log = Cl.ReadString((size, destination, actual) =>
                    Cl.clGetProgramBuildInfo(program, device.Id, 0x1183, size, destination, actual));
                throw new OpenClException($"OpenCL compilation failed on {device.Name} ({buildStatus}).\n{log}");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal CommandQueue CreateCommandQueue()
    {
        nint handle = Cl.clCreateCommandQueue(context, device.Id, 0, out int status);
        Cl.Check(status, "clCreateCommandQueue");
        queueHandles.Add(handle);
        var queue = new CommandQueue(handle, device);
        queues.Add(queue);
        return queue;
    }

    internal Mem CreateBuffer(MemFlags flags, int bytes)
    {
        nint handle = Cl.clCreateBuffer(context, (ulong)flags, (nuint)bytes, null, out int status);
        Cl.Check(status, "clCreateBuffer");
        buffers.Add(handle);
        return new Mem(handle);
    }

    internal Kernel CreateKernel(string name)
    {
        nint handle = Cl.clCreateKernel(program, name, out int status);
        Cl.Check(status, $"clCreateKernel ({name})");
        kernels.Add(handle);
        return new Kernel(handle);
    }

    public void Dispose()
    {
        foreach (CommandQueue queue in queues)
            queue.UnmapStagingBuffers();
        foreach (nint handle in kernels)
            Cl.clReleaseKernel(handle);
        foreach (nint handle in buffers)
            Cl.clReleaseMemObject(handle);
        foreach (nint handle in queueHandles)
            Cl.clReleaseCommandQueue(handle);
        if (program != 0)
            Cl.clReleaseProgram(program);
        if (context != 0)
            Cl.clReleaseContext(context);

        queues.Clear();
        kernels.Clear();
        buffers.Clear();
        queueHandles.Clear();
        program = context = 0;
    }
}

internal static unsafe partial class Cl
{
    private const string Library = "FLACCL.OpenCL";
    private static readonly Lazy<nint> OpenClHandle = new(LoadOpenCl);

    static Cl() => NativeLibrary.SetDllImportResolver(typeof(Cl).Assembly, ResolveLibrary);

    private static nint ResolveLibrary(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        name == Library ? OpenClHandle.Value : 0;

    private static nint LoadOpenCl()
    {
        if (OperatingSystem.IsWindows())
            return NativeLibrary.Load("OpenCL.dll", typeof(Cl).Assembly, null);

        if (OperatingSystem.IsMacOS())
            return NativeLibrary.Load("/System/Library/Frameworks/OpenCL.framework/OpenCL");

        if (OperatingSystem.IsLinux())
        {
            if (NativeLibrary.TryLoad("libOpenCL.so.1", out nint handle) ||
                NativeLibrary.TryLoad("libOpenCL.so", out handle))
                return handle;

            throw new DllNotFoundException("Could not load libOpenCL.so.1 or libOpenCL.so. Install the OpenCL ICD loader and a compatible device driver.");
        }

        throw new DllNotFoundException("FLACCL supports OpenCL on Windows, Linux, and macOS.");
    }

    internal static List<Device> GetDevices()
    {
        try
        {
            return EnumerateDevices();
        }
        catch (DllNotFoundException error)
        {
            throw new OpenClException($"Unable to load the OpenCL runtime. {error.Message}");
        }
        catch (EntryPointNotFoundException)
        {
            throw new OpenClException("The OpenCL loader is missing a required OpenCL entry point.");
        }
    }

    private static List<Device> EnumerateDevices()
    {
        var devices = new List<Device>();
        int status = clGetPlatformIDs(0, null, out uint platformCount);
        if (status == -1001)
            return devices;
        Check(status, "clGetPlatformIDs");
        if (platformCount == 0)
            return devices;
        var platforms = new nint[platformCount];
        fixed (nint* ids = platforms)
            Check(clGetPlatformIDs(platformCount, ids, out _), "clGetPlatformIDs");

        foreach (nint platform in platforms)
        {
            const ulong allDevices = 0xFFFFFFFF;
            status = clGetDeviceIDs(platform, allDevices, 0, null, out uint count);
            if (status == -1)
                continue;
            Check(status, "clGetDeviceIDs");
            if (count == 0)
                continue;
            var deviceIds = new nint[count];
            fixed (nint* ids = deviceIds)
                Check(clGetDeviceIDs(platform, allDevices, count, ids, out _), "clGetDeviceIDs");

            string platformName = ReadString((size, destination, actual) =>
                clGetPlatformInfo(platform, 0x0902, size, destination, actual));
            foreach (nint device in deviceIds)
            {
                if (DeviceValue<uint>(device, 0x1027) == 0 || DeviceValue<uint>(device, 0x1028) == 0)
                    continue;
                devices.Add(new Device(device, platformName, DeviceString(device, 0x102B),
                    DeviceString(device, 0x102F), DeviceValue<ulong>(device, 0x1000),
                    DeviceValue<uint>(device, 0x1001), DeviceValue<uint>(device, 0x1002),
                    DeviceValue<nuint>(device, 0x1004), DeviceString(device, 0x1030)));
            }
        }
        return devices;
    }

    internal static void Check(int status, string operation)
    {
        if (status != 0)
            throw new OpenClException($"{operation} failed (OpenCL error {status}).");
    }

    internal delegate int InfoQuery(nuint size, void* destination, nuint* actual);

    internal static string ReadString(InfoQuery query)
    {
        nuint length;
        Check(query(0, null, &length), "OpenCL info query");
        if (length == 0)
            return string.Empty;
        var data = new byte[checked((int)length)];
        fixed (byte* bytes = data)
            Check(query(length, bytes, null), "OpenCL info query");
        return Encoding.UTF8.GetString(data).TrimEnd('\0');
    }

    private static string DeviceString(nint device, uint property) =>
        ReadString((size, destination, actual) => clGetDeviceInfo(device, property, size, destination, actual));

    private static T DeviceValue<T>(nint device, uint property) where T : unmanaged
    {
        T value;
        Check(clGetDeviceInfo(device, property, (nuint)sizeof(T), &value, null), "clGetDeviceInfo");
        return value;
    }

#pragma warning disable IDE1006
    [LibraryImport(Library)] private static partial int clGetPlatformIDs(uint count, nint* ids, out uint actual);
    [LibraryImport(Library)] private static partial int clGetPlatformInfo(nint platform, uint property, nuint size, void* data, nuint* actual);
    [LibraryImport(Library)] private static partial int clGetDeviceIDs(nint platform, ulong type, uint count, nint* ids, out uint actual);
    [LibraryImport(Library)] private static partial int clGetDeviceInfo(nint device, uint property, nuint size, void* data, nuint* actual);
    [LibraryImport(Library)] internal static partial nint clCreateContext(nint* properties, uint count, nint* devices, nint notify, nint userData, out int status);
    [LibraryImport(Library)] internal static partial nint clCreateCommandQueue(nint context, nint device, ulong properties, out int status);
    [LibraryImport(Library)] internal static partial nint clCreateBuffer(nint context, ulong flags, nuint size, void* host, out int status);
    [LibraryImport(Library)] internal static partial nint clCreateProgramWithSource(nint context, uint count, byte** sources, nuint* lengths, out int status);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial int clBuildProgram(nint program, uint count, nint* devices, string options, nint notify, nint userData);
    [LibraryImport(Library)] internal static partial int clGetProgramBuildInfo(nint program, nint device, uint property, nuint size, void* data, nuint* actual);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint clCreateKernel(nint program, string name, out int status);
    [LibraryImport(Library)] internal static partial int clSetKernelArg(nint kernel, uint index, nuint size, void* value);
    [LibraryImport(Library)] internal static partial void* clEnqueueMapBuffer(nint queue, nint buffer, uint blocking, ulong flags, nuint offset, nuint size, uint count, nint* wait, nint* result, out int status);
    [LibraryImport(Library)] internal static partial int clEnqueueUnmapMemObject(nint queue, nint buffer, void* address, uint count, nint* wait, nint* result);
    [LibraryImport(Library)] internal static partial int clEnqueueWriteBuffer(nint queue, nint buffer, uint blocking, nuint offset, nuint size, void* address, uint count, nint* wait, nint* result);
    [LibraryImport(Library)] internal static partial int clEnqueueReadBuffer(nint queue, nint buffer, uint blocking, nuint offset, nuint size, void* address, uint count, nint* wait, nint* result);
    [LibraryImport(Library)] internal static partial int clEnqueueNDRangeKernel(nint queue, nint kernel, uint dimensions, nuint* offset, nuint* global, nuint* local, uint count, nint* wait, nint* result);
    [LibraryImport(Library)] internal static partial int clFlush(nint queue);
    [LibraryImport(Library)] internal static partial int clFinish(nint queue);
    [LibraryImport(Library)] internal static partial int clReleaseKernel(nint kernel);
    [LibraryImport(Library)] internal static partial int clReleaseMemObject(nint buffer);
    [LibraryImport(Library)] internal static partial int clReleaseCommandQueue(nint queue);
    [LibraryImport(Library)] internal static partial int clReleaseProgram(nint program);
    [LibraryImport(Library)] internal static partial int clReleaseContext(nint context);
#pragma warning restore IDE1006
}
