using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using GhostHunterPC.Services;

namespace GhostHunterPC.ViewModels;

public sealed class SmartFileOrganizerViewModel : ObservableObject
{
    private readonly UiLocalizationService _localization;
    private readonly SmartFileOrganizerService _service;
    private readonly FileFolderPickerService _folderPicker;
    private readonly Func<IReadOnlyCollection<string>> _excludedFolders;
    private CancellationTokenSource? _operationCancellation;
    private string _sourceFolder = string.Empty;
    private string _destinationFolder = string.Empty;
    private bool _organizeByMonth;
    private bool _isWorking;
    private OrganizerConflictResolution _selectedResolution = OrganizerConflictResolution.Skip;
    private OrganizerPreview? _preview;
    private string _status = string.Empty;
    private string _errorMessage = string.Empty;
    private int _completedCount;
    private bool _hasExecuted;

    public SmartFileOrganizerViewModel(
        UiLocalizationService localization,
        SmartFileOrganizerService service,
        FileFolderPickerService folderPicker,
        Func<IReadOnlyCollection<string>>? excludedFolders = null)
    {
        _localization = localization;
        _service = service;
        _folderPicker = folderPicker;
        _excludedFolders = excludedFolders ?? (() => Array.Empty<string>());
        PreviewItems = [];
        OperationLog = [];
        ChooseSourceFolderCommand = new RelayCommand(_ => ChooseSourceFolder(), _ => !IsWorking);
        ChooseDestinationFolderCommand = new RelayCommand(_ => ChooseDestinationFolder(), _ => !IsWorking);
        PreviewCommand = new RelayCommand(_ => _ = CreatePreviewAsync(), _ => !IsWorking && CanPreview);
        ExecuteCommand = new RelayCommand(_ => _ = ExecuteAsync(), _ => !IsWorking && Preview is not null && Preview.Items.Count > 0);
        CancelCommand = new RelayCommand(_ => _operationCancellation?.Cancel(), _ => IsWorking);
        UndoCommand = new RelayCommand(_ => _ = UndoAsync(), _ => !IsWorking && OperationLog.Any(entry => entry.Entry.Status == OrganizerOperationStatus.Moved));

        _localization.LanguageChanged += (_, _) => RefreshLocalizedValues();
    }

    public ObservableCollection<OrganizerPreviewItemViewModel> PreviewItems { get; }
    public ObservableCollection<OrganizerLogItemViewModel> OperationLog { get; }
    public RelayCommand ChooseSourceFolderCommand { get; }
    public RelayCommand ChooseDestinationFolderCommand { get; }
    public RelayCommand PreviewCommand { get; }
    public RelayCommand ExecuteCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand UndoCommand { get; }

    public string SourceLabel => _localization.GetText("organizer-source");
    public string DestinationLabel => _localization.GetText("organizer-destination");
    public string ChooseSourceLabel => _localization.GetText("organizer-choose-source");
    public string ChooseDestinationLabel => _localization.GetText("organizer-choose-destination");
    public string PreviewLabel => _localization.GetText("organizer-preview");
    public string ExecuteLabel => _localization.GetText("organizer-execute");
    public string CancelLabel => _localization.GetText("cancel-file-scan");
    public string UndoLabel => _localization.GetText("organizer-undo");
    public string OrganizeByMonthLabel => _localization.GetText("organizer-by-month");
    public string ConflictLabel => _localization.GetText("organizer-conflict-choice");
    public string SafetyNotice => _localization.GetText("organizer-safety");
    public string SummaryLabel => Preview is null
        ? string.Empty
        : string.Format(
            CultureInfo.CurrentCulture,
            _localization.GetText("organizer-summary"),
            Preview.Items.Count,
            FormatBytes(Preview.TotalBytes),
            Preview.ConflictCount);
    public string LogLabel => _localization.GetText("organizer-log");
    public string EmptyPreviewLabel => _localization.GetText("organizer-empty-preview");
    public string PreviewNameHeader => _localization.GetText("file-name");
    public string PreviewSourceHeader => _localization.GetText("organizer-source");
    public string PreviewDestinationHeader => _localization.GetText("organizer-destination");
    public string PreviewCategoryHeader => _localization.GetText("organizer-category");
    public string PreviewSizeHeader => _localization.GetText("file-size");
    public string PreviewConflictHeader => _localization.GetText("organizer-conflict");
    public string PreviewBasisHeader => _localization.GetText("organizer-type-basis");
    public string LogStatusHeader => _localization.GetText("organizer-log-status");
    public string LogDetailHeader => _localization.GetText("organizer-log-detail");
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

    public string ProgressLabel => $"{_localization.GetText("organizer-progress")}: {_completedCount}";
    public bool HasPreview => Preview is not null;
    public bool HasPreviewItems => PreviewItems.Count > 0;
    public bool HasExecuted => _hasExecuted;
    public bool CanPreview => !string.IsNullOrWhiteSpace(SourceFolder) && !string.IsNullOrWhiteSpace(DestinationFolder);

    public string SourceFolder
    {
        get => _sourceFolder;
        set
        {
            if (SetProperty(ref _sourceFolder, value))
            {
                InvalidatePreview();
            }
        }
    }

    public string DestinationFolder
    {
        get => _destinationFolder;
        set
        {
            if (SetProperty(ref _destinationFolder, value))
            {
                InvalidatePreview();
            }
        }
    }

    public bool OrganizeByMonth
    {
        get => _organizeByMonth;
        set
        {
            if (SetProperty(ref _organizeByMonth, value))
            {
                InvalidatePreview();
            }
        }
    }

    public bool IsWorking
    {
        get => _isWorking;
        private set
        {
            if (SetProperty(ref _isWorking, value))
            {
                ChooseSourceFolderCommand.RaiseCanExecuteChanged();
                ChooseDestinationFolderCommand.RaiseCanExecuteChanged();
                PreviewCommand.RaiseCanExecuteChanged();
                ExecuteCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
                UndoCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public OrganizerPreview? Preview
    {
        get => _preview;
        private set
        {
            if (SetProperty(ref _preview, value))
            {
                OnPropertyChanged(nameof(HasPreview));
                OnPropertyChanged(nameof(SummaryLabel));
                ExecuteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public IReadOnlyList<OrganizerConflictOption> ConflictOptions =>
    [
        new(OrganizerConflictResolution.Skip, _localization.GetText("organizer-skip")),
        new(OrganizerConflictResolution.Rename, _localization.GetText("organizer-rename")),
        new(OrganizerConflictResolution.Replace, _localization.GetText("organizer-replace"))
    ];

    public OrganizerConflictOption? SelectedConflictOption
    {
        get => ConflictOptions.FirstOrDefault(option => option.Resolution == _selectedResolution);
        set
        {
            if (value is not null && _selectedResolution != value.Resolution)
            {
                _selectedResolution = value.Resolution;
                OnPropertyChanged();
            }
        }
    }

    private void ChooseSourceFolder()
    {
        var path = _folderPicker.PickFolder(_localization.GetText("organizer-choose-source"));
        if (path is not null)
        {
            SourceFolder = path;
            ErrorMessage = string.Empty;
        }
    }

    private void ChooseDestinationFolder()
    {
        var path = _folderPicker.PickFolder(_localization.GetText("organizer-choose-destination"));
        if (path is not null)
        {
            DestinationFolder = path;
            ErrorMessage = string.Empty;
        }
    }

    private async Task CreatePreviewAsync()
    {
        if (!CanPreview || IsWorking)
        {
            return;
        }

        BeginOperation(_localization.GetText("organizer-preview-running"));
        try
        {
            var source = SourceFolder;
            var destination = DestinationFolder;
            var byMonth = OrganizeByMonth;
            var token = _operationCancellation!.Token;
            var preview = await Task.Run(
                () => _service.CreatePreview(source, destination, byMonth, token, _excludedFolders().ToArray()),
                token);
            Preview = preview;
            PreviewItems.Clear();
            foreach (var item in preview.Items)
            {
                PreviewItems.Add(new OrganizerPreviewItemViewModel(item, _localization));
            }

            OnPropertyChanged(nameof(HasPreviewItems));
            OnPropertyChanged(nameof(SummaryLabel));
            Status = string.Format(
                CultureInfo.CurrentCulture,
                _localization.GetText("organizer-preview-ready"),
                preview.Items.Count,
                preview.ConflictCount);
            ErrorMessage = preview.Errors.Count == 0
                ? string.Empty
                : $"{_localization.GetText("organizer-preview-errors")}: {string.Join(Environment.NewLine, preview.Errors.Take(10))}";
            _hasExecuted = false;
            OnPropertyChanged(nameof(HasExecuted));
        }
        catch (OperationCanceledException)
        {
            Status = _localization.GetText("organizer-cancelled");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException)
        {
            Preview = null;
            PreviewItems.Clear();
            OnPropertyChanged(nameof(HasPreviewItems));
            Status = _localization.GetText("organizer-failed");
            ErrorMessage = exception.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task ExecuteAsync()
    {
        if (Preview is null || Preview.Items.Count == 0 || IsWorking)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            Application.Current.MainWindow,
            string.Format(
                CultureInfo.CurrentCulture,
                _localization.GetText("organizer-confirm"),
                Preview.Items.Count,
                FormatBytes(Preview.TotalBytes),
                _localization.GetText($"organizer-{_selectedResolution.ToString().ToLowerInvariant()}")),
            _localization.GetText("organizer-confirm-title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        var previousLogCount = _service.OperationLog.Count;
        BeginOperation(_localization.GetText("organizer-executing"));
        _completedCount = 0;
        OnPropertyChanged(nameof(ProgressLabel));
        var progress = new Progress<OrganizerExecutionProgress>(update =>
        {
            _completedCount = update.Completed;
            OnPropertyChanged(nameof(ProgressLabel));
        });
        try
        {
            var preview = Preview;
            var resolution = _selectedResolution;
            var token = _operationCancellation!.Token;
            await Task.Run(
                () => _service.ExecuteAsync(preview, resolution, progress, token),
                token);
            AddNewLogEntries(previousLogCount);
            _hasExecuted = true;
            OnPropertyChanged(nameof(HasExecuted));
            Status = _localization.GetText("organizer-execution-complete");
            ErrorMessage = string.Empty;
            Preview = null;
            PreviewItems.Clear();
            OnPropertyChanged(nameof(HasPreviewItems));
        }
        catch (OperationCanceledException)
        {
            AddNewLogEntries(previousLogCount);
            _hasExecuted = true;
            OnPropertyChanged(nameof(HasExecuted));
            Status = _localization.GetText("organizer-cancelled");
            ErrorMessage = _localization.GetText("organizer-interrupted-detail");
            Preview = null;
            PreviewItems.Clear();
            OnPropertyChanged(nameof(HasPreviewItems));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException)
        {
            AddNewLogEntries(previousLogCount);
            Status = _localization.GetText("organizer-failed");
            ErrorMessage = exception.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task UndoAsync()
    {
        if (IsWorking)
        {
            return;
        }

        BeginOperation(_localization.GetText("organizer-undo-running"));
        try
        {
            var undone = await _service.UndoLastAsync(CancellationToken.None);
            if (undone)
            {
                var entry = _service.OperationLog.Last(item => item.Status == OrganizerOperationStatus.Undone);
                var viewEntry = OperationLog.Last(item => ReferenceEquals(item.Entry, entry));
                viewEntry.Refresh();
                Status = _localization.GetText("organizer-undo-complete");
            }
            else
            {
                Status = _localization.GetText("organizer-no-undo");
            }

            ErrorMessage = string.Empty;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or NotSupportedException)
        {
            Status = _localization.GetText("organizer-undo-failed");
            ErrorMessage = exception.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    private void AddNewLogEntries(int previousCount)
    {
        foreach (var entry in _service.OperationLog.Skip(previousCount))
        {
            OperationLog.Add(new OrganizerLogItemViewModel(entry, _localization));
        }

        UndoCommand.RaiseCanExecuteChanged();
    }

    private void BeginOperation(string status)
    {
        ErrorMessage = string.Empty;
        Status = status;
        _operationCancellation = new CancellationTokenSource();
        IsWorking = true;
    }

    private void EndOperation()
    {
        IsWorking = false;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
    }

    private void InvalidatePreview()
    {
        Preview = null;
        PreviewItems.Clear();
        OnPropertyChanged(nameof(HasPreviewItems));
        OnPropertyChanged(nameof(SummaryLabel));
        PreviewCommand.RaiseCanExecuteChanged();
    }

    private void RefreshLocalizedValues()
    {
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(DestinationLabel));
        OnPropertyChanged(nameof(ChooseSourceLabel));
        OnPropertyChanged(nameof(ChooseDestinationLabel));
        OnPropertyChanged(nameof(PreviewLabel));
        OnPropertyChanged(nameof(ExecuteLabel));
        OnPropertyChanged(nameof(CancelLabel));
        OnPropertyChanged(nameof(UndoLabel));
        OnPropertyChanged(nameof(OrganizeByMonthLabel));
        OnPropertyChanged(nameof(ConflictLabel));
        OnPropertyChanged(nameof(SafetyNotice));
        OnPropertyChanged(nameof(SummaryLabel));
        OnPropertyChanged(nameof(LogLabel));
        OnPropertyChanged(nameof(EmptyPreviewLabel));
        OnPropertyChanged(nameof(PreviewNameHeader));
        OnPropertyChanged(nameof(PreviewSourceHeader));
        OnPropertyChanged(nameof(PreviewDestinationHeader));
        OnPropertyChanged(nameof(PreviewCategoryHeader));
        OnPropertyChanged(nameof(PreviewSizeHeader));
        OnPropertyChanged(nameof(PreviewConflictHeader));
        OnPropertyChanged(nameof(PreviewBasisHeader));
        OnPropertyChanged(nameof(LogStatusHeader));
        OnPropertyChanged(nameof(LogDetailHeader));
        OnPropertyChanged(nameof(ProgressLabel));
        OnPropertyChanged(nameof(ConflictOptions));
        OnPropertyChanged(nameof(SelectedConflictOption));
        foreach (var item in PreviewItems)
        {
            item.Refresh();
        }

        foreach (var entry in OperationLog)
        {
            entry.Refresh();
        }
    }

    private static string FormatBytes(long bytes)
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

public sealed record OrganizerConflictOption(OrganizerConflictResolution Resolution, string Label);

public sealed class OrganizerPreviewItemViewModel(
    OrganizerPlanItem item,
    UiLocalizationService localization) : INotifyPropertyChanged
{
    public string Name => Path.GetFileName(item.SourcePath);
    public string SourcePath => item.SourcePath;
    public string DestinationPath => item.DestinationPath;
    public string Category => localization.GetText($"organizer-category-{item.Category.ToString().ToLowerInvariant()}");
    public string Size => $"{item.SizeBytes.ToString("N0", CultureInfo.CurrentCulture)} B";
    public string Conflict => item.HasNameConflict ? localization.GetText("organizer-conflict-yes") : string.Empty;
    public string TypeBasis => item.TypeBasis;
    public void Refresh()
    {
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(Conflict));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class OrganizerLogItemViewModel(
    OrganizerOperationLogEntry entry,
    UiLocalizationService localization) : INotifyPropertyChanged
{
    public OrganizerOperationLogEntry Entry { get; } = entry;
    public string Timestamp => Entry.Timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string SourcePath => Entry.SourcePath;
    public string DestinationPath => Entry.DestinationPath;
    public string Status => localization.GetText($"organizer-status-{Entry.Status.ToString().ToLowerInvariant()}");
    public string Detail => Entry.Detail;
    public void Refresh()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Detail));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
