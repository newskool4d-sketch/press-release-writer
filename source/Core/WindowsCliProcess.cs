using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PressRelease;

// Register the suspended process before it can start children. The job owns those children
// even if the original CLI launcher exits before they finish.
internal sealed class WindowsCliProcess : IDisposable
{
    readonly SafeFileHandle job;
    public Process Process { get; }
    public StreamWriter Input { get; }
    public StreamReader Output { get; }
    public StreamReader Error { get; }

    WindowsCliProcess(SafeFileHandle job, Process process, Pipe input, Pipe output, Pipe error)
    {
        this.job = job;
        Process = process;
        Input = new StreamWriter(input.OpenParent(FileAccess.Write), new UTF8Encoding(false)) { AutoFlush = true };
        Output = new StreamReader(output.OpenParent(FileAccess.Read), Encoding.UTF8);
        Error = new StreamReader(error.OpenParent(FileAccess.Read), Encoding.UTF8);
    }

    public void Stop() => job.Dispose();

    public void Dispose()
    {
        Stop();
        Input.Dispose();
        Output.Dispose();
        Error.Dispose();
        Process.Dispose();
    }

    public static WindowsCliProcess Start(ProcessStartInfo info)
    {
        using var input = new Pipe(parentWrites: true);
        using var output = new Pipe(parentWrites: false);
        using var error = new Pipe(parentWrites: false);
        using var attributes = new HandleList(input.Child, output.Child, error.Child);
        var startup = new StartupInfoEx
        {
            Size = Marshal.SizeOf<StartupInfoEx>(),
            Flags = 0x100, // STARTF_USESTDHANDLES
            Input = input.Child,
            Output = output.Child,
            Error = error.Child,
            AttributeList = attributes.Pointer
        };
        var command = new StringBuilder(Quote(info.FileName));
        if (info.ArgumentList.Count > 0)
            foreach (var arg in info.ArgumentList) command.Append(' ').Append(Quote(arg));
        else if (info.Arguments.Length > 0) command.Append(' ').Append(info.Arguments);
        var environment = string.Join('\0', info.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0";
        var job = CreateJobObject(IntPtr.Zero, null);
        Process? process = null;
        try
        {
            if (job.IsInvalid) throw new Win32Exception();
            var limits = new ExtendedLimits { LimitFlags = 0x2000 }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception();
            const uint flags = 0x00000004 | 0x00000400 | 0x00080000 | 0x08000000; // suspended, Unicode environment, extended startup, no window
            if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, true, flags, environment,
                    string.IsNullOrEmpty(info.WorkingDirectory) ? null : info.WorkingDirectory, ref startup, out var started))
                throw new Win32Exception();
            using var root = new SafeFileHandle(started.Process, ownsHandle: true);
            using var thread = new SafeFileHandle(started.Thread, ownsHandle: true);
            if (!AssignProcessToJobObject(job, root))
            {
                var failure = new Win32Exception();
                TerminateProcess(root, 1);
                throw failure;
            }
            process = Process.GetProcessById(started.Id);
            _ = process.SafeHandle; // Open the stable handle while the process is suspended.
            if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception();
            return new WindowsCliProcess(job, process, input, output, error);
        }
        catch
        {
            job.Dispose();
            process?.Dispose();
            throw;
        }
    }

    static string Quote(string value)
    {
        // Windows argv uses doubled backslashes before a quote and before the closing delimiter.
        var quoted = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    sealed class Pipe : IDisposable
    {
        SafeFileHandle? parent;
        readonly SafeFileHandle child;
        public IntPtr Child => child.DangerousGetHandle();

        public Pipe(bool parentWrites)
        {
            var security = new PipeSecurity { Size = Marshal.SizeOf<PipeSecurity>(), Inherit = 1 };
            if (!CreatePipe(out var read, out var write, ref security, 0)) throw new Win32Exception();
            (parent, child) = parentWrites ? (write, read) : (read, write);
            if (SetHandleInformation(parent, 1, 0)) return;
            var failure = new Win32Exception();
            Dispose();
            throw failure;
        }

        public FileStream OpenParent(FileAccess access)
        {
            var stream = new FileStream(parent!, access);
            parent = null;
            return stream;
        }

        public void Dispose()
        {
            child.Dispose();
            parent?.Dispose();
        }
    }

    sealed class HandleList : IDisposable
    {
        public IntPtr Pointer { get; }
        readonly IntPtr handles;

        public HandleList(params IntPtr[] children)
        {
            nuint bytes = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            if (bytes == 0) throw new Win32Exception();
            Pointer = Marshal.AllocHGlobal(checked((int)bytes));
            if (!InitializeProcThreadAttributeList(Pointer, 1, 0, ref bytes))
            {
                var failure = new Win32Exception();
                Marshal.FreeHGlobal(Pointer);
                throw failure;
            }
            try
            {
                handles = Marshal.AllocHGlobal(children.Length * IntPtr.Size);
                Marshal.Copy(children, 0, handles, children.Length);
                if (!UpdateProcThreadAttribute(Pointer, 0, 0x20002, handles, (nuint)(children.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception();
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            DeleteProcThreadAttributeList(Pointer);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(Pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PipeSecurity { public int Size; public IntPtr Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfoEx
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, Width, Height, CharsX, CharsY, Fill, Flags;
        public short Show, ReservedSize;
        public IntPtr ReservedData, Input, Output, Error, AttributeList;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInfo { public IntPtr Process, Thread; public int Id, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint Priority, Scheduling;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref PipeSecurity security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")]
    static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string? application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
        bool inheritHandles, uint flags, string environment, string? workingDirectory, ref StartupInfoEx startup, out ProcessInfo process);
}
