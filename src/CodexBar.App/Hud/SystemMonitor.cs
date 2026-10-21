using System.IO;
using System.Runtime.InteropServices;
using CodexBar.Core;

namespace CodexBar.App.Hud;

/// <summary>
/// Reads the machine the way Task Manager does: CPU and GPU from the same performance counters
/// (PDH: "% Processor Utility", the busiest GPU engine type), memory from GlobalMemoryStatusEx,
/// and the system drive. Counters need two reads to give a value, so the first sample has none.
/// </summary>
internal sealed class SystemMonitor : IDisposable
{
    private const uint FormatDouble = 0x00000200;
    private const uint NoCap100 = 0x00008000;

    private IntPtr _query;
    private IntPtr _cpu;
    private IntPtr _gpu;
    private bool _primed;

    public SystemMonitor()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
        {
            _query = IntPtr.Zero;
            return;
        }

        if (PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _cpu) != 0)
        {
            _cpu = IntPtr.Zero;
        }

        // Every engine of every process; summed per engine type, the busiest type is "the GPU" (as in Task Manager).
        if (PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpu) != 0)
        {
            _gpu = IntPtr.Zero;
        }
    }

    public SystemSample Sample()
    {
        double? cpu = null, gpu = null;
        if (_query != IntPtr.Zero && PdhCollectQueryData(_query) == 0)
        {
            if (_primed)
            {
                cpu = Single(_cpu) is { } value ? Math.Clamp(value, 0, 100) : null;
                gpu = BusiestEngine(_gpu);
            }

            _primed = true;
        }

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        GlobalMemoryStatusEx(ref memory);
        ulong free = 0, total = 0;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            free = (ulong)drive.AvailableFreeSpace;
            total = (ulong)drive.TotalSize;
        }
        catch (IOException)
        {
        }

        return new SystemSample(cpu, gpu, memory.MemoryLoad, memory.TotalPhys - memory.AvailPhys, memory.TotalPhys, free, total);
    }

    private static double? Single(IntPtr counter)
    {
        if (counter == IntPtr.Zero || PdhGetFormattedCounterValue(counter, FormatDouble | NoCap100, IntPtr.Zero, out var value) != 0 || value.Status != 0)
        {
            return null;
        }

        return value.Double;
    }

    private static double? BusiestEngine(IntPtr counter)
    {
        if (counter == IntPtr.Zero)
        {
            return null;
        }

        uint size = 0;
        PdhGetFormattedCounterArray(counter, FormatDouble | NoCap100, ref size, out var count, IntPtr.Zero);
        if (size == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, FormatDouble | NoCap100, ref size, out count, buffer) != 0)
            {
                return null;
            }

            // Instance names look like "pid_1234_luid_0x…_phys_0_eng_3_engtype_3D".
            var byType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var itemSize = Marshal.SizeOf<CounterItem>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + i * itemSize);
                if (item.Value.Status != 0)
                {
                    continue;
                }

                var name = Marshal.PtrToStringUni(item.Name) ?? string.Empty;
                var type = name[(name.LastIndexOf("engtype_", StringComparison.OrdinalIgnoreCase) is var at and >= 0 ? at : 0)..];
                byType[type] = byType.GetValueOrDefault(type) + item.Value.Double;
            }

            return byType.Count == 0 ? 0 : Math.Clamp(byType.Values.Max(), 0, 100);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Double;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public IntPtr Name;
        public CounterValue Value;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? source, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out CounterValue value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr items);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
