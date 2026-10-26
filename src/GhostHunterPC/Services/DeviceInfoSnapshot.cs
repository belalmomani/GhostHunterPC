namespace GhostHunterPC.Services;

public enum DeviceInfoSection
{
    OperatingSystem,
    Processor,
    Memory,
    Drives,
    Uptime
}

public sealed class DeviceInfoSnapshot
{
    public string? OperatingSystem { get; init; }
    public string? OperatingSystemVersion { get; init; }
    public string? Architecture { get; init; }
    public string? ProcessorName { get; init; }
    public int? PhysicalCores { get; init; }
    public int? LogicalProcessors { get; init; }
    public ulong? TotalMemoryBytes { get; init; }
    public ulong? AvailableMemoryBytes { get; init; }
    public IReadOnlyList<DriveSnapshot> Drives { get; init; } = [];
    public TimeSpan? Uptime { get; init; }
    public IReadOnlyList<DeviceInfoSection> Errors { get; init; } = [];
}

public sealed record DriveSnapshot(string Name, ulong TotalBytes, ulong AvailableBytes);
