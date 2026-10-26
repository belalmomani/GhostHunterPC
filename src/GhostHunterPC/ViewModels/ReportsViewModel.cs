using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security;
using System.Windows;
using GhostHunterPC.Services;
using Microsoft.Win32;

namespace GhostHunterPC.ViewModels;

public sealed class ReportsViewModel : ObservableObject
{
    private readonly UiLocalizationService _localization;
    private readonly ReportExportService _export;
    private readonly Func<DeviceInfoSnapshot?> _deviceInfo;
    private readonly FileInvestigatorViewModel _investigator;
    private readonly ForgottenFilesViewModel _duplicates;
    private readonly SystemMonitorViewModel _monitor;
    private readonly PcHistoryViewModel _history;
    private ReportKind _selectedKind = ReportKind.DeviceScan;
    private string _status = string.Empty;
    private string _errorMessage = string.Empty;

    public ReportsViewModel(
        UiLocalizationService localization,
        ReportExportService export,
        Func<DeviceInfoSnapshot?> deviceInfo,
        FileInvestigatorViewModel investigator,
        ForgottenFilesViewModel duplicates,
        SystemMonitorViewModel monitor,
        PcHistoryViewModel history)
    {
        _localization = localization;
        _export = export;
        _deviceInfo = deviceInfo;
        _investigator = investigator;
        _duplicates = duplicates;
        _monitor = monitor;
        _history = history;
        ReportKinds = new ObservableCollection<ReportKindOption>(
            Enum.GetValues<ReportKind>().Select(kind => new ReportKindOption(kind, localization)));
        ExportCsvCommand = new RelayCommand(_ => Export(".csv"));
        ExportJsonCommand = new RelayCommand(_ => Export(".json"));
        _localization.LanguageChanged += (_, _) => RefreshLocalizedValues();
    }

    public ObservableCollection<ReportKindOption> ReportKinds { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ExportJsonCommand { get; }
    public ReportKind SelectedKind
    {
        get => _selectedKind;
        set
        {
            if (SetProperty(ref _selectedKind, value))
            {
                OnPropertyChanged(nameof(SelectedReportLabel));
            }
        }
    }

    public string SelectedReportLabel => _localization.GetText($"report-{SelectedKind}");
    public string Title => _localization.GetText("report-title");
    public string Description => _localization.GetText("report-description");
    public string ExportCsvLabel => _localization.GetText("report-export-csv");
    public string ExportJsonLabel => _localization.GetText("report-export-json");
    public string PrivacyNotice => _localization.GetText("report-privacy");
    public string PdfUnavailable => _localization.GetText("report-pdf-unavailable");
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }

    private void Export(string extension)
    {
        var kind = SelectedKind;
        var isCsv = extension == ".csv";
        var dialog = new SaveFileDialog
        {
            Title = _localization.GetText(isCsv ? "report-export-csv" : "report-export-json"),
            Filter = isCsv ? "CSV (*.csv)|*.csv" : "JSON (*.json)|*.json",
            DefaultExt = extension,
            AddExtension = true,
            FileName = $"GhostHunterPC-{kind.ToString().ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmm}"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var report = BuildReport(kind);
            if (isCsv)
            {
                _export.ExportCsv(dialog.FileName, report);
            }
            else
            {
                _export.ExportJson(dialog.FileName, report);
            }

            ErrorMessage = string.Empty;
            Status = string.Format(
                CultureInfo.CurrentCulture,
                _localization.GetText("report-exported"),
                Path.GetFullPath(dialog.FileName));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or ArgumentException
            or NotSupportedException)
        {
            Status = _localization.GetText("report-failed");
            ErrorMessage = exception.Message;
        }
    }

    private ReportDocument BuildReport(ReportKind kind)
    {
        var rows = new List<ReportRow>();
        switch (kind)
        {
            case ReportKind.DeviceScan:
                if (_deviceInfo() is { } info)
                {
                    rows.Add(new("Device information", "Operating system", info.OperatingSystem ?? "Unavailable"));
                    rows.Add(new("Device information", "Processor", info.ProcessorName ?? "Unavailable"));
                    rows.Add(new("Device information", "Logical processors", info.LogicalProcessors?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"));
                    rows.Add(new("Device information", "Memory bytes", info.TotalMemoryBytes?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"));
                    rows.Add(new("Device information", "Fixed drives", info.Drives.Count.ToString(CultureInfo.InvariantCulture)));
                    rows.Add(new("Device scan", "Full scan status", "Not performed"));
                }
                else
                {
                    rows.Add(new("Device scan", "Status", "Device information has not been collected"));
                }
                break;
            case ReportKind.FileInvestigator:
                if (_investigator.Results.Count == 0)
                {
                    rows.Add(new("File investigation", "Status", "No results available"));
                    break;
                }
                rows.AddRange(_investigator.Results.Select((item, index) =>
                    new ReportRow("File", $"File {index + 1}", string.Join("; ", new[]
                    {
                        item.Extension,
                        item.SizeLabel,
                        item.SignatureLabel
                    }))));
                break;
            case ReportKind.Duplicates:
                if (_duplicates.DuplicateFiles.Count == 0)
                {
                    rows.Add(new("Duplicates", "Status", "No duplicate results available"));
                    break;
                }
                rows.AddRange(_duplicates.DuplicateFiles.Select((item, index) =>
                    new ReportRow("Duplicate candidate", $"Candidate {index + 1}", $"{item.SizeLabel}; group size {item.GroupCount}")));
                break;
            case ReportKind.SystemMonitor:
                rows.Add(new("System monitor", "CPU", _monitor.CpuValue));
                rows.Add(new("System monitor", "Memory", _monitor.RamValue));
                rows.Add(new("System monitor", "Process count", _monitor.Processes.Count.ToString(CultureInfo.InvariantCulture)));
                rows.Add(new("System monitor", "Fixed-drive count", _monitor.Drives.Count.ToString(CultureInfo.InvariantCulture)));
                break;
            case ReportKind.BaselineChanges:
                if (_history.Changes.Count == 0)
                {
                    rows.Add(new("Baseline comparison", "Status", _history.HasBaseline
                        ? "No comparison changes available"
                        : "No baseline available"));
                    break;
                }
                rows.AddRange(_history.Changes
                    .GroupBy(item => (item.Status, item.Category))
                    .Select(group => new ReportRow(
                        group.Key.Category,
                        group.Key.Status,
                        group.Count().ToString(CultureInfo.InvariantCulture))));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return new ReportDocument(_localization.GetText($"report-{kind}"), DateTimeOffset.Now, rows);
    }

    private void RefreshLocalizedValues()
    {
        foreach (var option in ReportKinds)
        {
            option.RefreshLocalizedValues();
        }

        OnPropertyChanged(nameof(SelectedReportLabel));
    }
}

public sealed class ReportKindOption(ReportKind kind, UiLocalizationService localization) : ObservableObject
{
    public ReportKind Kind { get; } = kind;
    public string Label => localization.GetText($"report-{Kind}");
    public void RefreshLocalizedValues() => OnPropertyChanged(nameof(Label));
}
