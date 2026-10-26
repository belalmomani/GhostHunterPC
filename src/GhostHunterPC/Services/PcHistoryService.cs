using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;

namespace GhostHunterPC.Services;

public sealed record InventoryItem(string Category, string Key, string Value, DateTimeOffset CollectedAt);
public sealed record InventorySnapshot(DateTimeOffset CapturedAt, IReadOnlyList<InventoryItem> Items, IReadOnlyList<string> Errors);
public sealed record InventoryBaseline(DateTimeOffset CreatedAt, IReadOnlyList<InventoryItem> Items, IReadOnlyList<string> Errors);
public sealed record InventoryChange(string Status, string Category, string Key, string? PreviousValue, string? CurrentValue, DateTimeOffset ObservedAt);
public sealed record TimelineEvent(DateTimeOffset OccurredAt, string Type, string Source, string Details, string EventKey);
public sealed record PcHistoryData(InventoryBaseline? Baseline, IReadOnlyList<TimelineEvent> Events);
public sealed record InventoryComparison(DateTimeOffset ComparedAt, IReadOnlyList<InventoryChange> Changes, IReadOnlyList<string> Errors);

public interface IComputerInventorySource
{
    InventorySnapshot Capture();
}

public sealed class WindowsComputerInventorySource : IComputerInventorySource
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly string[] StartupKeys =
    [
        @"Software\Microsoft\Windows\CurrentVersion\Run",
        @"Software\Microsoft\Windows\CurrentVersion\RunOnce"
    ];

    private readonly DeviceInfoService _deviceInfoService;

    public WindowsComputerInventorySource(DeviceInfoService? deviceInfoService = null) =>
        _deviceInfoService = deviceInfoService ?? new DeviceInfoService();

    public InventorySnapshot Capture()
    {
        var capturedAt = DateTimeOffset.Now;
        var items = new List<InventoryItem>();
        var errors = new List<string>();
        var system = _deviceInfoService.Read();

        Add("system", "Operating system", $"{system.OperatingSystem ?? "Unavailable"} {system.OperatingSystemVersion}".Trim());
        Add("system", "Architecture", system.Architecture ?? "Unavailable");
        Add("system", "Processor", system.ProcessorName ?? "Unavailable");
        Add("system", "Logical processors", system.LogicalProcessors?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable");
        Add("system", "Physical memory", system.TotalMemoryBytes?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable");
        foreach (var drive in system.Drives)
        {
            Add("system", $"Drive {drive.Name}", $"Total={drive.TotalBytes}");
        }

        foreach (var section in system.Errors)
        {
            errors.Add($"System information: {section}");
        }

        ReadInstalledPrograms(RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM64");
        ReadInstalledPrograms(RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32");
        ReadInstalledPrograms(RegistryHive.CurrentUser, RegistryView.Default, "HKCU");
        ReadStartupEntries(RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM64");
        ReadStartupEntries(RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32");
        ReadStartupEntries(RegistryHive.CurrentUser, RegistryView.Default, "HKCU");
        ReadProcesses();

        return new InventorySnapshot(capturedAt, items, errors);

        void Add(string category, string key, string value) =>
            items.Add(new InventoryItem(category, key, value, capturedAt));

        void ReadInstalledPrograms(RegistryHive hive, RegistryView view, string scope)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(UninstallKey, writable: false);
                if (uninstall is null)
                {
                    return;
                }

                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var program = uninstall.OpenSubKey(subKeyName, writable: false);
                        var name = program?.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        var version = program?.GetValue("DisplayVersion") as string ?? string.Empty;
                        var publisher = program?.GetValue("Publisher") as string ?? string.Empty;
                        Add("program", $"{scope}\\{subKeyName}", $"{name} | {version} | {publisher}");
                    }
                    catch (Exception exception) when (IsExpectedReadException(exception))
                    {
                        errors.Add($"Program {scope}\\{subKeyName}: {exception.Message}");
                    }
                }
            }
            catch (Exception exception) when (IsExpectedReadException(exception))
            {
                errors.Add($"Installed programs {scope}: {exception.Message}");
            }
        }

        void ReadStartupEntries(RegistryHive hive, RegistryView view, string scope)
        {
            foreach (var startupPath in StartupKeys)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var startup = baseKey.OpenSubKey(startupPath, writable: false);
                    if (startup is null)
                    {
                        continue;
                    }

                    foreach (var valueName in startup.GetValueNames())
                    {
                        try
                        {
                            var value = startup.GetValue(valueName)?.ToString();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                Add("startup", $"{scope}\\{startupPath}\\{valueName}", value);
                            }
                        }
                        catch (Exception exception) when (IsExpectedReadException(exception))
                        {
                            errors.Add($"Startup item {scope}\\{valueName}: {exception.Message}");
                        }
                    }
                }
                catch (Exception exception) when (IsExpectedReadException(exception))
                {
                    errors.Add($"Startup entries {scope}: {exception.Message}");
                }
            }
        }

        void ReadProcesses()
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
            }
            catch (Exception exception) when (IsExpectedReadException(exception))
            {
                errors.Add($"Process list: {exception.Message}");
                return;
            }

            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        Add("process", process.Id.ToString(CultureInfo.InvariantCulture), process.ProcessName);
                    }
                    catch (Exception exception) when (IsExpectedReadException(exception))
                    {
                        errors.Add($"Process details: {exception.Message}");
                    }
                }
            }
        }
    }

    private static bool IsExpectedReadException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException;
}

public sealed class PcHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _storagePath;
    private readonly IComputerInventorySource _inventorySource;
    private PcHistoryData _data;
    public string? LoadError { get; private set; }
    public bool SaveEventHistory { get; set; } = true;

    public PcHistoryService(string? storagePath = null, IComputerInventorySource? inventorySource = null)
    {
        _storagePath = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GhostHunterPC",
            "pc-history.json");
        _inventorySource = inventorySource ?? new WindowsComputerInventorySource();
        _data = Load();
    }

    public PcHistoryData Data => _data;

    public void RecordApplicationEvent(string type, string details)
    {
        if (!SaveEventHistory)
        {
            return;
        }

        var timelineEvent = new TimelineEvent(DateTimeOffset.Now, type, "GhostHunterPC", details, Guid.NewGuid().ToString("N"));
        var nextData = WithEvent(_data, timelineEvent);
        Save(nextData);
        _data = nextData;
    }

    public InventoryBaseline CreateBaseline()
    {
        var snapshot = _inventorySource.Capture();
        var baseline = new InventoryBaseline(snapshot.CapturedAt, snapshot.Items, snapshot.Errors);
        var nextData = _data with { Baseline = baseline };
        nextData = WithEvent(nextData, new TimelineEvent(
            snapshot.CapturedAt,
            "Baseline created",
            "GhostHunterPC",
            $"Captured {snapshot.Items.Count} inventory items; {snapshot.Errors.Count} read errors.",
            Guid.NewGuid().ToString("N")));
        Save(nextData);
        _data = nextData;
        return baseline;
    }

    public InventoryComparison CompareWithBaseline()
    {
        var baseline = _data.Baseline
            ?? throw new InvalidOperationException("Create a baseline before comparing inventory.");
        var current = _inventorySource.Capture();
        var changes = Compare(baseline.Items, current.Items, current.CapturedAt, baseline.Errors, current.Errors);
        var comparison = new InventoryComparison(current.CapturedAt, changes, current.Errors);
        var nextData = WithEvent(_data, new TimelineEvent(
            current.CapturedAt,
            "Baseline comparison",
            "GhostHunterPC",
            $"Compared {current.Items.Count} inventory items: {changes.Count(change => change.Status == "Added")} added, {changes.Count(change => change.Status == "Removed")} removed, {changes.Count(change => change.Status == "Changed")} changed.",
            Guid.NewGuid().ToString("N")));
        Save(nextData);
        _data = nextData;
        return comparison;
    }

    public int ImportRecentWindowsEvents()
    {
        if (!SaveEventHistory)
        {
            return 0;
        }

        var imported = WindowsEventLogReader.ReadRecent(24, 50);
        var existing = _data.Events.Select(item => item.EventKey).ToHashSet(StringComparer.Ordinal);
        var added = imported.Where(item => existing.Add(item.EventKey)).ToArray();
        var nextData = _data with { Events = _data.Events.Concat(added).OrderByDescending(item => item.OccurredAt).ToArray() };
        Save(nextData);
        _data = nextData;
        return added.Length;
    }

    public void Export(string destinationPath)
    {
        if (LoadError is not null)
        {
            throw new InvalidDataException(LoadError);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(_data, JsonOptions), new UTF8Encoding(false));
    }

    public static IReadOnlyList<InventoryChange> Compare(
        IReadOnlyList<InventoryItem> baseline,
        IReadOnlyList<InventoryItem> current,
        DateTimeOffset comparedAt,
        IReadOnlyList<string>? baselineErrors = null,
        IReadOnlyList<string>? currentErrors = null)
    {
        var oldItems = baseline.ToDictionary(item => (item.Category, item.Key));
        var newItems = current.ToDictionary(item => (item.Category, item.Key));
        var incompleteBaseline = GetIncompleteCategories(baselineErrors ?? []);
        var incompleteCurrent = GetIncompleteCategories(currentErrors ?? []);
        var changes = new List<InventoryChange>();
        foreach (var (key, oldItem) in oldItems)
        {
            if (!newItems.TryGetValue(key, out var newItem))
            {
                if (!incompleteCurrent.Contains(oldItem.Category))
                {
                    changes.Add(new InventoryChange("Removed", oldItem.Category, oldItem.Key, oldItem.Value, null, comparedAt));
                }
            }
            else if (!string.Equals(oldItem.Value, newItem.Value, StringComparison.Ordinal))
            {
                changes.Add(new InventoryChange("Changed", oldItem.Category, oldItem.Key, oldItem.Value, newItem.Value, comparedAt));
            }
        }

        foreach (var (key, newItem) in newItems)
        {
            if (!oldItems.ContainsKey(key) && !incompleteBaseline.Contains(newItem.Category))
            {
                changes.Add(new InventoryChange("Added", newItem.Category, newItem.Key, null, newItem.Value, comparedAt));
            }
        }

        return changes
            .OrderBy(change => change.Category, StringComparer.Ordinal)
            .ThenBy(change => change.Key, StringComparer.Ordinal)
            .ToArray();
    }

    private static HashSet<string> GetIncompleteCategories(IReadOnlyList<string> errors)
    {
        var categories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var error in errors)
        {
            if (error.StartsWith("System information:", StringComparison.Ordinal))
            {
                categories.Add("system");
            }
            else if (error.StartsWith("Installed programs", StringComparison.Ordinal)
                || error.StartsWith("Program ", StringComparison.Ordinal))
            {
                categories.Add("program");
            }
            else if (error.StartsWith("Startup entries", StringComparison.Ordinal)
                || error.StartsWith("Startup item ", StringComparison.Ordinal))
            {
                categories.Add("startup");
            }
            else if (error.StartsWith("Process list", StringComparison.Ordinal)
                || error.StartsWith("Process details", StringComparison.Ordinal))
            {
                categories.Add("process");
            }
        }

        return categories;
    }

    private PcHistoryData Load()
    {
        if (!File.Exists(_storagePath))
        {
            return new PcHistoryData(null, []);
        }

        try
        {
            return JsonSerializer.Deserialize<PcHistoryData>(File.ReadAllText(_storagePath), JsonOptions)
                ?? new PcHistoryData(null, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadError = $"Could not load local PC history from '{_storagePath}': {exception.Message}";
            return new PcHistoryData(null, []);
        }
    }

    private PcHistoryData WithEvent(PcHistoryData data, TimelineEvent timelineEvent) =>
        !SaveEventHistory
            ? data
            : data with { Events = data.Events.Append(timelineEvent).OrderByDescending(item => item.OccurredAt).ToArray() };

    private void Save(PcHistoryData data)
    {
        if (LoadError is not null)
        {
            throw new InvalidDataException(LoadError);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_storagePath))!);
        var temporaryPath = $"{_storagePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(data, JsonOptions), new UTF8Encoding(false));
            File.Move(temporaryPath, _storagePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

internal static class WindowsEventLogReader
{
    public static IReadOnlyList<TimelineEvent> ReadRecent(int hours, int limit)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "wevtutil.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("qe");
        startInfo.ArgumentList.Add("System");
        startInfo.ArgumentList.Add("/q:*[System[TimeCreated[timediff(@SystemTime) <= " +
            (hours * 60 * 60 * 1000).ToString(CultureInfo.InvariantCulture) + "]]]");
        startInfo.ArgumentList.Add("/f:xml");
        startInfo.ArgumentList.Add($"/c:{limit.ToString(CultureInfo.InvariantCulture)}");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows Event Log reader could not be started.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw new TimeoutException("Reading recent Windows System events exceeded 10 seconds.");
        }

        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"Windows Event Log query failed with exit code {process.ExitCode}."
                : error.Trim());
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var xmlOutput = output.Trim();
        if (xmlOutput.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
        {
            var declarationEnd = xmlOutput.IndexOf("?>", StringComparison.Ordinal);
            if (declarationEnd >= 0)
            {
                xmlOutput = xmlOutput[(declarationEnd + 2)..];
            }
        }

        var document = XDocument.Parse($"<Root>{xmlOutput}</Root>");
        return document.Root!.Descendants().Where(element => element.Name.LocalName == "Event").Select(element =>
        {
            var system = element.Elements().FirstOrDefault(child => child.Name.LocalName == "System");
            var provider = system?.Elements().FirstOrDefault(child => child.Name.LocalName == "Provider")?.Attribute("Name")?.Value ?? "Windows System";
            var eventId = system?.Elements().FirstOrDefault(child => child.Name.LocalName == "EventID")?.Value ?? "unknown";
            var recordId = system?.Elements().FirstOrDefault(child => child.Name.LocalName == "EventRecordID")?.Value ?? Guid.NewGuid().ToString("N");
            var level = system?.Elements().FirstOrDefault(child => child.Name.LocalName == "Level")?.Value ?? "unknown";
            var timestampText = system?.Elements().FirstOrDefault(child => child.Name.LocalName == "TimeCreated")?.Attribute("SystemTime")?.Value;
            var occurredAt = DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTimeOffset.Now;
            return new TimelineEvent(
                occurredAt,
                $"Windows event {eventId}",
                "Windows Event Log / System",
                $"Provider: {provider}; Event ID: {eventId}; Level: {level}; Record: {recordId}",
                $"Windows:System:{recordId}");
        }).ToArray();
    }
}
