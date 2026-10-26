using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace GhostHunterPC.Services;

public sealed record CpuTimes(ulong Idle, ulong Kernel, ulong User);
public sealed record MonitorMemorySnapshot(ulong TotalBytes, ulong AvailableBytes);
public sealed record MonitorDriveSnapshot(string Name, ulong? TotalBytes, ulong? AvailableBytes, string? Error);
public sealed record MonitoredProcessSnapshot(int? ProcessId, string Name, long? MemoryBytes, string? Error);

public sealed record SystemMonitorSnapshot(
    DateTimeOffset CapturedAt,
    double? CpuPercent,
    ulong? TotalMemoryBytes,
    ulong? AvailableMemoryBytes,
    IReadOnlyList<MonitorDriveSnapshot> Drives,
    IReadOnlyList<MonitoredProcessSnapshot> Processes,
    IReadOnlyList<string> Errors);

public interface IMonitoredProcess : IDisposable
{
    int Id { get; }
    string Name { get; }
    long WorkingSet64 { get; }
}

public interface ISystemMonitorDataSource
{
    CpuTimes ReadCpuTimes();
    MonitorMemorySnapshot ReadMemory();
    IEnumerable<MonitorDriveSnapshot> ReadDrives();
    IEnumerable<IMonitoredProcess> GetProcesses();
}

public sealed class SystemMonitorService(ISystemMonitorDataSource? dataSource = null)
{
    private readonly ISystemMonitorDataSource _dataSource = dataSource ?? new WindowsSystemMonitorDataSource();
    private CpuTimes? _previousCpuTimes;

    public SystemMonitorSnapshot ReadSnapshot(CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        double? cpuPercent = null;
        MonitorMemorySnapshot? memory = null;
        var drives = new List<MonitorDriveSnapshot>();
        var processes = new List<MonitoredProcessSnapshot>();

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var currentCpu = _dataSource.ReadCpuTimes();
            if (_previousCpuTimes is CpuTimes previous)
            {
                var idleDelta = Delta(currentCpu.Idle, previous.Idle);
                var kernelDelta = Delta(currentCpu.Kernel, previous.Kernel);
                var userDelta = Delta(currentCpu.User, previous.User);
                var totalDelta = kernelDelta + userDelta;
                if (totalDelta > 0)
                {
                    cpuPercent = Math.Clamp(100d * (totalDelta - idleDelta) / totalDelta, 0d, 100d);
                }
            }

            _previousCpuTimes = currentCpu;
        }
        catch (Exception exception) when (IsExpectedReadException(exception))
        {
            errors.Add($"CPU: {exception.Message}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            memory = _dataSource.ReadMemory();
            if (memory.TotalBytes == 0 || memory.AvailableBytes > memory.TotalBytes)
            {
                throw new InvalidDataException("Memory counters were inconsistent.");
            }
        }
        catch (Exception exception) when (IsExpectedReadException(exception))
        {
            errors.Add($"RAM: {exception.Message}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            foreach (var drive in _dataSource.ReadDrives())
            {
                cancellationToken.ThrowIfCancellationRequested();
                drives.Add(drive);
                if (!string.IsNullOrWhiteSpace(drive.Error))
                {
                    errors.Add($"{drive.Name}: {drive.Error}");
                }
            }
        }
        catch (Exception exception) when (IsExpectedReadException(exception))
        {
            errors.Add($"Disks: {exception.Message}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            foreach (var process in _dataSource.GetProcesses())
            {
                cancellationToken.ThrowIfCancellationRequested();
                int? processId = null;
                string name = "Unknown process";
                long? memoryBytes = null;
                string? processError = null;
                try
                {
                    processId = process.Id;
                    name = process.Name;
                    memoryBytes = process.WorkingSet64;
                }
                catch (Exception exception) when (IsExpectedReadException(exception))
                {
                    processError = exception.Message;
                    errors.Add($"Process {processId?.ToString() ?? "unknown"}: {exception.Message}");
                }
                finally
                {
                    process.Dispose();
                }

                processes.Add(new MonitoredProcessSnapshot(processId, name, memoryBytes, processError));
            }
        }
        catch (Exception exception) when (IsExpectedReadException(exception))
        {
            errors.Add($"Process list: {exception.Message}");
        }

        return new SystemMonitorSnapshot(
            DateTimeOffset.Now,
            cpuPercent,
            memory?.TotalBytes,
            memory?.AvailableBytes,
            drives,
            processes.OrderByDescending(process => process.MemoryBytes ?? -1).ThenBy(process => process.Name).ToArray(),
            errors);
    }

    private static ulong Delta(ulong current, ulong previous) => current >= previous ? current - previous : 0;

    private static bool IsExpectedReadException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or InvalidOperationException
            or Win32Exception
            or NotSupportedException
            or OverflowException;
}

public sealed class WindowsSystemMonitorDataSource : ISystemMonitorDataSource
{
    public CpuTimes ReadCpuTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new CpuTimes(ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
    }

    public MonitorMemorySnapshot ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new MonitorMemorySnapshot(status.TotalPhysical, status.AvailablePhysical);
    }

    public IEnumerable<MonitorDriveSnapshot> ReadDrives()
    {
        var result = new List<MonitorDriveSnapshot>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed)
            {
                continue;
            }

            try
            {
                if (!drive.IsReady)
                {
                    result.Add(new MonitorDriveSnapshot(drive.Name, null, null, "Drive is not ready."));
                    continue;
                }

                var total = drive.TotalSize;
                var available = drive.AvailableFreeSpace;
                if (total <= 0 || available < 0 || available > total)
                {
                    result.Add(new MonitorDriveSnapshot(drive.Name, null, null, "Drive reported inconsistent capacity values."));
                    continue;
                }

                result.Add(new MonitorDriveSnapshot(drive.Name, (ulong)total, (ulong)available, null));
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or Win32Exception)
            {
                result.Add(new MonitorDriveSnapshot(drive.Name, null, null, exception.Message));
            }
        }

        return result;
    }

    public IEnumerable<IMonitoredProcess> GetProcesses() =>
        Process.GetProcesses().Select(process => new WindowsMonitoredProcess(process)).ToArray();

    private static ulong ToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME fileTime) =>
        ((ulong)(uint)fileTime.dwHighDateTime << 32) | (uint)fileTime.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out System.Runtime.InteropServices.ComTypes.FILETIME idleTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private sealed class WindowsMonitoredProcess(Process process) : IMonitoredProcess
    {
        public int Id => process.Id;
        public string Name => process.ProcessName;
        public long WorkingSet64 => process.WorkingSet64;
        public void Dispose() => process.Dispose();
    }
}
