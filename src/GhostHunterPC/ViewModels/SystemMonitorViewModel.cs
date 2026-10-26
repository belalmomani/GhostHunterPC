using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using GhostHunterPC.Services;

namespace GhostHunterPC.ViewModels;

public sealed class SystemMonitorViewModel : ObservableObject
{
    private readonly UiLocalizationService _localization;
    private readonly SystemMonitorService _service;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CancellationTokenSource? _monitorCancellation;
    private string _searchText = string.Empty;
    private string _status = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _isMonitoring;
    private bool _isRefreshing;
    private double? _cpuPercent;
    private double? _ramPercent;
    private string _ramUsage = string.Empty;
    private DateTimeOffset? _lastUpdated;
    private string _selectedRefreshInterval = "2";

    public SystemMonitorViewModel(UiLocalizationService localization, SystemMonitorService service)
    {
        _localization = localization;
        _service = service;
        Processes = [];
        Drives = [];
        RefreshIntervals = ["1", "2", "5", "10", "30"];
        ProcessesView = CollectionViewSource.GetDefaultView(Processes);
        ProcessesView.Filter = FilterProcess;

        StartMonitoringCommand = new RelayCommand(_ => _ = StartMonitoringAsync(), _ => !IsMonitoring);
        StopMonitoringCommand = new RelayCommand(_ => _monitorCancellation?.Cancel(), _ => IsMonitoring);
        RefreshNowCommand = new RelayCommand(_ => _ = RefreshOnceAsync(), _ => !IsRefreshing);
        _localization.LanguageChanged += (_, _) => RefreshLocalizedValues();
    }

    public ObservableCollection<MonitoredProcessViewModel> Processes { get; }
    public ObservableCollection<MonitorDriveViewModel> Drives { get; }
    public ICollectionView ProcessesView { get; }
    public IReadOnlyList<string> RefreshIntervals { get; }
    public RelayCommand StartMonitoringCommand { get; }
    public RelayCommand StopMonitoringCommand { get; }
    public RelayCommand RefreshNowCommand { get; }

    public string Description => _localization.GetText("monitor-description");
    public string StartLabel => _localization.GetText("monitor-start");
    public string StopLabel => _localization.GetText("monitor-stop");
    public string RefreshLabel => _localization.GetText("monitor-refresh");
    public string IntervalLabel => _localization.GetText("monitor-interval");
    public string SearchLabel => _localization.GetText("monitor-search");
    public string CpuLabel => _localization.GetText("monitor-cpu");
    public string RamLabel => _localization.GetText("monitor-ram");
    public string DiskLabel => _localization.GetText("monitor-disks");
    public string ProcessLabel => _localization.GetText("monitor-processes");
    public string ProcessNameHeader => _localization.GetText("monitor-process-name");
    public string PidHeader => _localization.GetText("monitor-pid");
    public string ProcessMemoryHeader => _localization.GetText("monitor-process-memory");
    public string ProcessErrorHeader => _localization.GetText("monitor-process-error");
    public string DriveHeader => _localization.GetText("monitor-drive");
    public string DriveTotalHeader => _localization.GetText("monitor-drive-total");
    public string DriveAvailableHeader => _localization.GetText("monitor-drive-available");
    public string EmptyProcessesLabel => _localization.GetText("monitor-empty-processes");
    public string EmptyDrivesLabel => _localization.GetText("monitor-empty-drives");
    public string LastUpdatedLabel => _localization.GetText("monitor-last-updated");
    public string ProcessCountLabel => string.Format(
        CultureInfo.CurrentCulture,
        _localization.GetText("monitor-process-count"),
        Processes.Count);
    public string CpuValue => _cpuPercent is double cpu
        ? $"{cpu:F1}%"
        : _localization.GetText("unavailable");
    public double CpuPercentValue => _cpuPercent ?? 0;
    public string RamValue => _ramPercent is double ram
        ? $"{ram:F1}% ({_ramUsage})"
        : _localization.GetText("unavailable");
    public double RamPercent => _ramPercent ?? 0;
    public string LastUpdatedValue => _lastUpdated?.ToLocalTime().ToString("T", CultureInfo.CurrentCulture)
        ?? _localization.GetText("unavailable");
    public bool HasProcesses => Processes.Count > 0;
    public bool HasDrives => Drives.Count > 0;
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                RefreshNowCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            if (SetProperty(ref _isMonitoring, value))
            {
                StartMonitoringCommand.RaiseCanExecuteChanged();
                StopMonitoringCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ProcessesView.Refresh();
            }
        }
    }

    public string SelectedRefreshInterval
    {
        get => _selectedRefreshInterval;
        set
        {
            if (RefreshIntervals.Contains(value, StringComparer.Ordinal)
                && SetProperty(ref _selectedRefreshInterval, value))
            {
                OnPropertyChanged(nameof(RefreshIntervalDescription));
            }
        }
    }

    public string RefreshIntervalDescription => string.Format(
        CultureInfo.CurrentCulture,
        _localization.GetText("monitor-next-interval"),
        SelectedRefreshInterval);

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    private async Task StartMonitoringAsync()
    {
        if (IsMonitoring)
        {
            return;
        }

        _monitorCancellation = new CancellationTokenSource();
        IsMonitoring = true;
        Status = _localization.GetText("monitor-starting");
        var token = _monitorCancellation.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await RefreshSnapshotAsync(token);
                await Task.Delay(TimeSpan.FromSeconds(int.Parse(SelectedRefreshInterval, CultureInfo.InvariantCulture)), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Status = _localization.GetText("monitor-stopped");
        }
        finally
        {
            _monitorCancellation.Dispose();
            _monitorCancellation = null;
            IsMonitoring = false;
        }
    }

    private async Task RefreshOnceAsync()
    {
        try
        {
            await RefreshSnapshotAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            Status = _localization.GetText("monitor-cancelled");
        }
    }

    private async Task RefreshSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!await _refreshLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            var snapshot = await Task.Run(() => _service.ReadSnapshot(cancellationToken), cancellationToken);
            _cpuPercent = snapshot.CpuPercent;
            _ramPercent = snapshot.TotalMemoryBytes is ulong total && total > 0
                && snapshot.AvailableMemoryBytes is ulong available && available <= total
                    ? 100d * (total - available) / total
                    : null;
            _ramUsage = snapshot.TotalMemoryBytes is ulong totalBytes
                && snapshot.AvailableMemoryBytes is ulong freeBytes
                    ? $"{FormatBytes(totalBytes - freeBytes)} / {FormatBytes(totalBytes)}"
                    : string.Empty;
            _lastUpdated = snapshot.CapturedAt;
            Processes.Clear();
            foreach (var process in snapshot.Processes)
            {
                Processes.Add(new MonitoredProcessViewModel(process, _localization));
            }

            Drives.Clear();
            foreach (var drive in snapshot.Drives)
            {
                Drives.Add(new MonitorDriveViewModel(drive));
            }

            _errorMessage = snapshot.Errors.Count == 0
                ? string.Empty
                : string.Join(Environment.NewLine, snapshot.Errors.Take(15));
            Status = snapshot.Errors.Count == 0
                ? _localization.GetText("monitor-updated")
                : _localization.GetText("monitor-partial");
            ProcessesView.Refresh();
            OnPropertyChanged(nameof(CpuValue));
            OnPropertyChanged(nameof(CpuPercentValue));
            OnPropertyChanged(nameof(RamValue));
            OnPropertyChanged(nameof(RamPercent));
            OnPropertyChanged(nameof(LastUpdatedValue));
            OnPropertyChanged(nameof(ProcessCountLabel));
            OnPropertyChanged(nameof(HasProcesses));
            OnPropertyChanged(nameof(HasDrives));
            OnPropertyChanged(nameof(ErrorMessage));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            ErrorMessage = exception.Message;
            Status = _localization.GetText("monitor-failed");
        }
        finally
        {
            IsRefreshing = false;
            _refreshLock.Release();
        }
    }

    private bool FilterProcess(object item)
    {
        if (item is not MonitoredProcessViewModel process || string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return process.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            || process.ProcessIdLabel.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase);
    }

    private void RefreshLocalizedValues()
    {
        foreach (var property in new[]
        {
            nameof(Description), nameof(StartLabel), nameof(StopLabel), nameof(RefreshLabel), nameof(IntervalLabel),
            nameof(SearchLabel), nameof(CpuLabel), nameof(RamLabel), nameof(DiskLabel), nameof(ProcessLabel),
            nameof(ProcessNameHeader), nameof(PidHeader), nameof(ProcessMemoryHeader), nameof(ProcessErrorHeader),
            nameof(DriveHeader), nameof(DriveTotalHeader), nameof(DriveAvailableHeader), nameof(EmptyProcessesLabel),
            nameof(EmptyDrivesLabel), nameof(LastUpdatedLabel), nameof(ProcessCountLabel), nameof(CpuValue),
            nameof(CpuPercentValue), nameof(RamValue), nameof(RamPercent), nameof(LastUpdatedValue), nameof(RefreshIntervalDescription)
        })
        {
            OnPropertyChanged(property);
        }

        foreach (var process in Processes)
        {
            process.RefreshLocalizedValues(_localization);
        }
    }

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }
}

public sealed class MonitoredProcessViewModel(
    MonitoredProcessSnapshot process,
    UiLocalizationService localization) : ObservableObject
{
    private string _errorLabel = process.Error is null
        ? string.Empty
        : localization.GetText("monitor-process-unavailable");

    public string Name => process.Name;
    public int? ProcessId => process.ProcessId;
    public string ProcessIdLabel => process.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public long? MemoryBytes => process.MemoryBytes;
    public string MemoryLabel => process.MemoryBytes is long bytes
        ? $"{bytes.ToString("N0", CultureInfo.CurrentCulture)} B"
        : "—";
    public string Error => string.IsNullOrEmpty(_errorLabel)
        ? process.Error ?? string.Empty
        : $"{_errorLabel}: {process.Error}";

    public void RefreshLocalizedValues(UiLocalizationService localization)
    {
        _errorLabel = process.Error is null ? string.Empty : localization.GetText("monitor-process-unavailable");
        OnPropertyChanged(nameof(Error));
    }
}

public sealed class MonitorDriveViewModel(MonitorDriveSnapshot drive)
{
    public string Name => drive.Name;
    public ulong? TotalBytes => drive.TotalBytes;
    public ulong? AvailableBytes => drive.AvailableBytes;
    public string TotalLabel => drive.TotalBytes is ulong total ? FormatBytes(total) : "—";
    public string AvailableLabel => drive.AvailableBytes is ulong free ? FormatBytes(free) : "—";
    public string Error => drive.Error ?? string.Empty;

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }
}
