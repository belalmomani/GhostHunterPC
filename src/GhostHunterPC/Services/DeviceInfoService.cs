using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GhostHunterPC.Services;

public sealed class DeviceInfoService
{
    private const string WindowsVersionRegistryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string ProcessorRegistryPath = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private readonly IDeviceDriveProvider _driveProvider;

    public DeviceInfoService(IDeviceDriveProvider? driveProvider = null)
    {
        _driveProvider = driveProvider ?? new SystemDeviceDriveProvider();
    }

    public DeviceInfoSnapshot Read()
    {
        var errors = new List<DeviceInfoSection>();

        var (operatingSystem, operatingSystemVersion) = TryRead(
            DeviceInfoSection.OperatingSystem,
            errors,
            ReadOperatingSystem,
            (null, null));
        var (processorName, physicalCores) = TryRead(
            DeviceInfoSection.Processor,
            errors,
            ReadProcessor,
            (null, (int?)null));
        var (totalMemory, availableMemory) = TryRead(
            DeviceInfoSection.Memory,
            errors,
            ReadMemory,
            ((ulong?)null, (ulong?)null));
        var drives = TryRead(
            DeviceInfoSection.Drives,
            errors,
            () => ReadDrives(errors),
            Array.Empty<DriveSnapshot>());
        var uptime = TryRead(
            DeviceInfoSection.Uptime,
            errors,
            () => TimeSpan.FromMilliseconds(Environment.TickCount64),
            (TimeSpan?)null);

        return new DeviceInfoSnapshot
        {
            OperatingSystem = operatingSystem,
            OperatingSystemVersion = operatingSystemVersion,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessorName = processorName,
            PhysicalCores = physicalCores,
            LogicalProcessors = Environment.ProcessorCount,
            TotalMemoryBytes = totalMemory,
            AvailableMemoryBytes = availableMemory,
            Drives = drives,
            Uptime = uptime,
            Errors = errors
        };
    }

    private static (string? Name, string? Version) ReadOperatingSystem()
    {
        using var key = Registry.LocalMachine.OpenSubKey(WindowsVersionRegistryPath, writable: false);
        var name = key?.GetValue("ProductName") as string;
        var displayVersion = key?.GetValue("DisplayVersion") as string;
        var build = key?.GetValue("CurrentBuildNumber") as string;
        if (int.TryParse(build, NumberStyles.None, CultureInfo.InvariantCulture, out var buildNumber)
            && buildNumber >= 22000)
        {
            name = name?.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
        }

        var version = string.Join(" ", new[] { displayVersion, build }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return (string.IsNullOrWhiteSpace(name) ? null : name,
            string.IsNullOrWhiteSpace(version) ? null : version);
    }

    private static (string? Name, int? PhysicalCores) ReadProcessor()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ProcessorRegistryPath, writable: false);
        var name = key?.GetValue("ProcessorNameString") as string;
        var coresValue = key?.GetValue("NumberOfCores");
        int? cores = coresValue is int value && value > 0 ? value : null;

        return (string.IsNullOrWhiteSpace(name) ? null : name.Trim(), cores);
    }

    private static (ulong? Total, ulong? Available) ReadMemory()
    {
        var status = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return (status.TotalPhysical, status.AvailablePhysical);
    }

    private IReadOnlyList<DriveSnapshot> ReadDrives(ICollection<DeviceInfoSection> errors)
    {
        var drives = new List<DriveSnapshot>();
        foreach (var drive in _driveProvider.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed)
                {
                    continue;
                }

                if (!drive.IsReady)
                {
                    MarkDriveInfoUnavailable(errors);
                    continue;
                }

                var totalSize = drive.TotalSize;
                var availableFreeSpace = drive.AvailableFreeSpace;
                if (totalSize <= 0 || availableFreeSpace < 0 || availableFreeSpace > totalSize)
                {
                    MarkDriveInfoUnavailable(errors);
                    continue;
                }

                drives.Add(new DriveSnapshot(drive.Name, (ulong)totalSize, (ulong)availableFreeSpace));
            }
            catch (Exception exception) when (IsExpectedReadException(exception))
            {
                MarkDriveInfoUnavailable(errors);
            }
        }

        return drives;
    }

    private static void MarkDriveInfoUnavailable(ICollection<DeviceInfoSection> errors)
    {
        if (!errors.Contains(DeviceInfoSection.Drives))
        {
            errors.Add(DeviceInfoSection.Drives);
        }
    }

    private static T TryRead<T>(
        DeviceInfoSection section,
        ICollection<DeviceInfoSection> errors,
        Func<T> read,
        T unavailable)
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (IsExpectedReadException(exception))
        {
            errors.Add(section);
            return unavailable;
        }
    }

    private static bool IsExpectedReadException(Exception exception) =>
        exception is IOException
        or UnauthorizedAccessException
        or System.Security.SecurityException
        or InvalidOperationException
        or Win32Exception
        or OverflowException;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
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

    private sealed class SystemDeviceDriveProvider : IDeviceDriveProvider
    {
        public IEnumerable<IDeviceDrive> GetDrives() =>
            DriveInfo.GetDrives().Select(drive => new SystemDeviceDrive(drive));
    }

    private sealed class SystemDeviceDrive : IDeviceDrive
    {
        private readonly DriveInfo _drive;

        public SystemDeviceDrive(DriveInfo drive)
        {
            _drive = drive;
        }

        public string Name => _drive.Name;
        public DriveType DriveType => _drive.DriveType;
        public bool IsReady => _drive.IsReady;
        public long TotalSize => _drive.TotalSize;
        public long AvailableFreeSpace => _drive.AvailableFreeSpace;
    }
}
