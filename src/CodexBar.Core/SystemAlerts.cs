namespace CodexBar.Core;

/// <summary>One look at the machine: percentages, and memory / disk in bytes.</summary>
public sealed record SystemSample(
    double? Cpu,
    double? Gpu,
    double MemoryLoad,
    ulong MemoryUsed,
    ulong MemoryTotal,
    ulong DiskFree,
    ulong DiskTotal);

public enum SystemAlertKind { Memory, Disk }

public sealed record SystemAlert(SystemAlertKind Kind, double Value);

/// <summary>
/// When the machine is worth a word: memory at 90 % or more on two looks in a row, or the system
/// drive under 5 % (or 5 GB) free. Each says so once, then keeps quiet for <see cref="Quiet"/> —
/// and a busy CPU or GPU is never a warning (a game or a build is supposed to use them).
/// </summary>
public sealed class SystemAlerts
{
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);
    public const double MemoryLimit = 90;
    public const double DiskPercentLimit = 5;
    public const ulong DiskBytesLimit = 5UL * 1024 * 1024 * 1024;

    private readonly Dictionary<SystemAlertKind, DateTimeOffset> _lastSaid = [];
    private int _memoryHigh;

    public SystemAlert? Check(SystemSample sample, DateTimeOffset now)
    {
        _memoryHigh = sample.MemoryLoad >= MemoryLimit ? _memoryHigh + 1 : 0;
        if (_memoryHigh >= 2 && Say(SystemAlertKind.Memory, now))
        {
            return new SystemAlert(SystemAlertKind.Memory, sample.MemoryLoad);
        }

        if (sample.DiskTotal > 0)
        {
            var freePercent = 100.0 * sample.DiskFree / sample.DiskTotal;
            if ((freePercent < DiskPercentLimit || sample.DiskFree < DiskBytesLimit) && Say(SystemAlertKind.Disk, now))
            {
                return new SystemAlert(SystemAlertKind.Disk, freePercent);
            }
        }

        return null;
    }

    private bool Say(SystemAlertKind kind, DateTimeOffset now)
    {
        if (_lastSaid.TryGetValue(kind, out var last) && now - last < Quiet)
        {
            return false;
        }

        _lastSaid[kind] = now;
        return true;
    }

    public static string Bytes(ulong bytes) => bytes >= 1024UL * 1024 * 1024
        ? $"{bytes / (1024.0 * 1024 * 1024):0.#} GB"
        : $"{bytes / (1024.0 * 1024):0} MB";
}
