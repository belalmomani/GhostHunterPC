using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using GhostHunterPC.Services;

namespace GhostHunterPC.ViewModels;

public sealed class FileInvestigatorViewModel : ObservableObject
{
    private readonly UiLocalizationService _localization;
    private readonly FileInvestigationService _service;
    private readonly FileFolderPickerService _folderPicker;
    private readonly Func<IReadOnlyCollection<string>> _excludedFolders;
    private CancellationTokenSource? _scanCancellation;
    private string _selectedFolder = string.Empty;
    private string _searchText = string.Empty;
    private string _status = string.Empty;
    private string _errorMessage = string.Empty;
    private int _filesScanned;
    private int _errorsCount;
    private bool _isScanning;
    private FileInvestigationItemViewModel? _selectedResult;

    public FileInvestigatorViewModel(
        UiLocalizationService localization,
        FileInvestigationService service,
        FileFolderPickerService folderPicker,
        Func<IReadOnlyCollection<string>>? excludedFolders = null)
    {
        _localization = localization;
        _service = service;
        _folderPicker = folderPicker;
        _excludedFolders = excludedFolders ?? (() => Array.Empty<string>());
        Results = [];
        ResultsView = CollectionViewSource.GetDefaultView(Results);
        ResultsView.Filter = FilterResult;

        ChooseFolderCommand = new RelayCommand(_ => ChooseFolder(), _ => !IsScanning);
        StartScanCommand = new RelayCommand(
            _ => _ = ScanAsync(),
            _ => !IsScanning && !string.IsNullOrWhiteSpace(SelectedFolder));
        CancelScanCommand = new RelayCommand(
            _ => _scanCancellation?.Cancel(),
            _ => IsScanning);
        CalculateHashCommand = new RelayCommand(
            _ => _ = CalculateHashAsync(),
            _ => SelectedResult is not null && !SelectedResult.IsHashing);
        OpenLocationCommand = new RelayCommand(
            _ => OpenSelectedLocation(),
            _ => SelectedResult is not null);

        _localization.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(PrivacyNotice));
            OnPropertyChanged(nameof(ProgressLabel));
            OnPropertyChanged(nameof(ChooseFolderLabel));
            OnPropertyChanged(nameof(StartScanLabel));
            OnPropertyChanged(nameof(CancelScanLabel));
            OnPropertyChanged(nameof(SearchLabel));
            OnPropertyChanged(nameof(CalculateHashLabel));
            OnPropertyChanged(nameof(OpenLocationLabel));
            OnPropertyChanged(nameof(EmptyResultsLabel));
            foreach (var result in Results)
            {
                result.RefreshLocalizedValues();
            }

            ResultsView.Refresh();
        };
    }

    public ObservableCollection<FileInvestigationItemViewModel> Results { get; }
    public ICollectionView ResultsView { get; }
    public bool HasResults => Results.Count > 0;
    public RelayCommand ChooseFolderCommand { get; }
    public RelayCommand StartScanCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public RelayCommand CalculateHashCommand { get; }
    public RelayCommand OpenLocationCommand { get; }

    public string Title => _localization.GetText("investigator");
    public string Description => _localization.GetText("investigator-description");
    public string PrivacyNotice => _localization.GetText("file-investigator-privacy");
    public string ProgressLabel => $"{_localization.GetText("files-scanned")}: {_filesScanned} · {_localization.GetText("file-errors")}: {_errorsCount}";
    public string ChooseFolderLabel => _localization.GetText("choose-folder");
    public string StartScanLabel => _localization.GetText("start-file-scan");
    public string CancelScanLabel => _localization.GetText("cancel-file-scan");
    public string SearchLabel => _localization.GetText("file-search");
    public string CalculateHashLabel => _localization.GetText("file-calculate-hash");
    public string OpenLocationLabel => _localization.GetText("file-open-location");
    public string EmptyResultsLabel => _localization.GetText("file-no-results");

    public string SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (SetProperty(ref _selectedFolder, value))
            {
                StartScanCommand.RaiseCanExecuteChanged();
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
                ResultsView.Refresh();
            }
        }
    }

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

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                StartScanCommand.RaiseCanExecuteChanged();
                CancelScanCommand.RaiseCanExecuteChanged();
                ChooseFolderCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public FileInvestigationItemViewModel? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (SetProperty(ref _selectedResult, value))
            {
                CalculateHashCommand.RaiseCanExecuteChanged();
                OpenLocationCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private void ChooseFolder()
    {
        var folder = _folderPicker.PickFolder();
        if (folder is not null)
        {
            SelectedFolder = folder;
            ErrorMessage = string.Empty;
        }
    }

    private async Task ScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        Results.Clear();
        OnPropertyChanged(nameof(HasResults));
        SelectedResult = null;
        _filesScanned = 0;
        _errorsCount = 0;
        ErrorMessage = string.Empty;
        Status = _localization.GetText("file-scan-starting");
        _scanCancellation = new CancellationTokenSource();
        IsScanning = true;
        OnPropertyChanged(nameof(ProgressLabel));

        var progress = new Progress<FileInvestigationProgress>(update =>
        {
            _filesScanned = update.FilesScanned;
            _errorsCount = update.Errors;
            if (update.LatestItem is not null)
            {
                Results.Add(new FileInvestigationItemViewModel(update.LatestItem, _localization));
                OnPropertyChanged(nameof(HasResults));
            }

            OnPropertyChanged(nameof(ProgressLabel));
        });

        try
        {
            var token = _scanCancellation.Token;
            var folderPath = SelectedFolder;
            var result = await Task.Run(
                () => _service.ScanFolderAsync(folderPath, progress, token, _excludedFolders().ToArray()),
                token);
            Status = _localization.GetText("file-scan-complete");
            _errorsCount = result.Errors.Count;
            ErrorMessage = result.Errors.Count == 0
                ? string.Empty
                : $"{_localization.GetText("file-scan-errors")}: {string.Join(Environment.NewLine, result.Errors.Take(10))}";
        }
        catch (OperationCanceledException)
        {
            Status = _localization.GetText("file-scan-cancelled");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException)
        {
            Status = _localization.GetText("file-scan-failed");
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsScanning = false;
            _scanCancellation.Dispose();
            _scanCancellation = null;
            OnPropertyChanged(nameof(ProgressLabel));
        }
    }

    private async Task CalculateHashAsync()
    {
        var selected = SelectedResult;
        if (selected is null)
        {
            return;
        }

        var hashTask = selected.CalculateHashAsync(_service, CancellationToken.None);
        CalculateHashCommand.RaiseCanExecuteChanged();
        await hashTask;
        CalculateHashCommand.RaiseCanExecuteChanged();
    }

    private void OpenSelectedLocation()
    {
        if (SelectedResult is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{SelectedResult.Path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is IOException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    private bool FilterResult(object item)
    {
        if (item is not FileInvestigationItemViewModel result || string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return result.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            || result.Path.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            || result.Extension.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            || result.DetectedType.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            || result.Publisher.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            || result.SignatureLabel.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            || result.Warning.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase);
    }
}
