using System.IO;

namespace GhostHunterPC.Services;

public interface IDeviceDriveProvider
{
    IEnumerable<IDeviceDrive> GetDrives();
}

public interface IDeviceDrive
{
    string Name { get; }
    DriveType DriveType { get; }
    bool IsReady { get; }
    long TotalSize { get; }
    long AvailableFreeSpace { get; }
}
