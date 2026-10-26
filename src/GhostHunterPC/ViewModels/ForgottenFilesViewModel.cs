using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using GhostHunterPC.Services;

namespace GhostHunterPC.ViewModels;

public sealed class ForgottenFilesViewModel : ObservableObject
{
    private readonly UiLocalizationService _localization;
    private readonly ForgottenFilesService _service;
    private readonly FileFolderPickerService _folderPicker;
    private readonly Func<IReadOnlyCollection<string>> _excludedFolders;
    private CancellationTokenSource? _operationCancellation;
    private string _selectedFolder = string.Empty;
    private string _searchText = string.Empty;
    private int _oldAfterMonths = 6;
    private bool _isScanning;
    private bool _isRecycling;
    private int _filesDiscovered;
    private int _filesHashed;
    private int _filesProcessed;
    private int _errorCount;
    private string _status = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _hasScanned;

    public ForgottenFilesViewModel(
        UiLocalizationService localization,
        ForgottenFilesService service,
        FileFolderPickerService folderPicker,
        Func<IReadOnlyCollection<string>>? excludedFolders = null)
    {
        _localization = localization;
        _service = service;
        _folderPicker = folderPicker;
        _excludedFolders = excludedFolders ?? (() => Array.Empty<string>());
        DuplicateFiles = [];
        OldFiles = [];
        DuplicateFilesView = CollectionViewSource.GetDefaultView(DuplicateFiles);
        OldFilesView = CollectionViewSource.GetDefaultView(OldFiles);
        DuplicateFilesView.Filter = item => MatchesSearch(item as ForgottenFileViewModel);
        OldFilesView.Filter = item => MatchesSearch(item as ForgottenFileViewModel);

        ChooseFolderCommand = new RelayCommand(_ => ChooseFolder(), _ => !IsWorking);
        ScanCommand = new RelayCommand(_ => _ = ScanAsync(), _ => !IsWorking && !string.IsNullOrWhiteSpace(SelectedFolder));
        CancelCommand = new RelayCommand(_ => _operationCancellation?.Cancel(), _ => IsWorking);
        RecycleSelectedCommand = new RelayCommand(_ => _ = RecycleSelectedAsync(), _ => !IsWorking && SelectedFilesCount > 0);
        _localization.LanguageChanged += (_, _) => RefreshLocalizedValues();
    }

    public ObservableCollection<ForgottenFileViewModel> DuplicateFiles { get; }
    public ObservableCollection<ForgottenFileViewModel> OldFiles { get; }
    public ICollectionView DuplicateFilesView { get; }
    public ICollectionView OldFilesView { get; }
    public RelayCommand ChooseFolderCommand { get; }
    public RelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RecycleSelectedCommand { get; }

    public string ChooseFolderLabel => _localization.GetText("choose-folder");
    public string ScanLabel => _localization.GetText("forgotten-scan");
    public string CancelLabel => _localization.GetText("cancel-file-scan");
    public string DuplicateTabLabel => _localization.GetText("forgotten-duplicates");
    public string OldTabLabel => _localization.GetText("forgotten-old-files");
    public string RecycleLabel => _localization.GetText("forgotten-recycle");
    public string FolderLabel => _localization.GetText("file-folder");
    public string SearchLabel => _localization.GetText("file-search");
    public string OldMonthsLabel => _localization.GetText("forgotten-months");
    public string DuplicateExplanation => _localization.GetText("forgotten-duplicate-explanation");
    public string OldFilesDisclaimer => _localization.GetText("forgotten-old-disclaimer");
    public string DuplicateEmptyLabel => _localization.GetText(_hasScanned ? "forgotten-no-duplicates" : "forgotten-not-scanned");
    public string OldEmptyLabel => _localization.GetText(_hasScanned ? "forgotten-no-old-files" : "forgotten-not-scanned");
    public string DuplicateNameHeader => _localization.GetText("file-name");
    public string DuplicatePathHeader => _localization.GetText("file-path");
    public string DuplicateSizeHeader => _localization.GetText("file-size");
    public string DuplicateModifiedHeader => _localization.GetText("file-modified");
    public string DuplicateHashHeader => _localization.GetText("forgotten-hash");
    public string DuplicateGroupHeader => _localization.GetText("forgotten-group");
    public string DuplicateKeepHeader => _localization.GetText("forgotten-keep");
    public string OldNameHeader => _localization.GetText("file-name");
    public string OldPathHeader => _localization.GetText("file-path");
    public string OldSizeHeader => _localization.GetText("file-size");
    public string OldModifiedHeader => _localization.GetText("file-modified");
    public string SummaryLabel => string.Format(
        CultureInfo.CurrentCulture,
        _localization.GetText("forgotten-summary"),
        DuplicateFiles.Select(item => item.GroupKey).Distinct(StringComparer.Ordinal).Count(),
        FormatBytes(DuplicateFiles.Sum(item => item.SizeBytes)),
        FormatBytes(DuplicateFiles
            .Where(item => !item.IsRecommendedKeep)
            .GroupBy(item => item.GroupKey, StringComparer.Ordinal)
            .Sum(group => Math.Max(0, group.Count()) * group.First().SizeBytes)),
        FormatBytes(SelectedFilesBytes),
        OldFiles.Count);
    public string ProgressLabel => string.Format(
        CultureInfo.CurrentCulture,
        _localization.GetText("forgotten-progress"),
        _filesDiscovered,
        _filesHashed,
        _filesProcessed,
        _errorCount);

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

    public bool IsWorking => _isScanning || _isRecycling;
    public bool HasDuplicateFiles => DuplicateFiles.Count > 0;
    public bool HasOldFiles => OldFiles.Count > 0;
    public bool HasSelectedFiles => SelectedFilesCount > 0;

    public string SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (SetProperty(ref _selectedFolder, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
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
                DuplicateFilesView.Refresh();
                OldFilesView.Refresh();
            }
        }
    }

    public int OldAfterMonths
    {
        get => _oldAfterMonths;
        set
        {
            if (SetProperty(ref _oldAfterMonths, Math.Clamp(value, 1, 1200)))
            {
                ScanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private int SelectedFilesCount => DuplicateFiles.Count(item => item.IsSelectedForRecycleBin);
    private long SelectedFilesBytes => DuplicateFiles
        .Where(item => item.IsSelectedForRecycleBin)
        .Sum(item => item.SizeBytes);

    private void ChooseFolder()
    {
        var folder = _folderPicker.PickFolder(_localization.GetText("forgotten-choose-folder"));
        if (folder is not null)
        {
            SelectedFolder = folder;
            ErrorMessage = string.Empty;
        }
    }

    private async Task ScanAsync()
    {
        if (IsWorking || string.IsNullOrWhiteSpace(SelectedFolder))
        {
            return;
        }

        ClearResults();
        _hasScanned = false;
        OnPropertyChanged(nameof(DuplicateEmptyLabel));
        OnPropertyChanged(nameof(OldEmptyLabel));
        _filesDiscovered = 0;
        _filesHashed = 0;
        _filesProcessed = 0;
        _errorCount = 0;
        OnPropertyChanged(nameof(ProgressLabel));
        BeginOperation(scanning: true, _localization.GetText("forgotten-starting"));
        var progress = new Progress<ForgottenFilesProgress>(update =>
        {
            _filesDiscovered = update.FilesDiscovered;
            _filesHashed = update.FilesHashed;
            OnPropertyChanged(nameof(ProgressLabel));
        });

        try
        {
            var folder = SelectedFolder;
            var months = OldAfterMonths;
            var token = _operationCancellation!.Token;
            var result = await Task.Run(
                () => _service.ScanAsync(folder, months, progress, token, _excludedFolders().ToArray()),
                token);
            PopulateResults(result);
            _hasScanned = true;
            OnPropertyChanged(nameof(DuplicateEmptyLabel));
            OnPropertyChanged(nameof(OldEmptyLabel));
            OnPropertyChanged(nameof(SummaryLabel));
            _errorCount = result.Errors.Count;
            OnPropertyChanged(nameof(ProgressLabel));
            Status = string.Format(
                CultureInfo.CurrentCulture,
                _localization.GetText("forgotten-scan-complete"),
                result.DuplicateGroups.Count,
                result.OldFiles.Count);
            ErrorMessage = result.Errors.Count == 0
                ? string.Empty
                : $"{_localization.GetText("forgotten-errors")}: {string.Join(Environment.NewLine, result.Errors.Take(10))}";
        }
        catch (OperationCanceledException)
        {
            Status = _localization.GetText("forgotten-cancelled");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException)
        {
            Status = _localization.GetText("forgotten-failed");
            ErrorMessage = exception.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task RecycleSelectedAsync()
    {
        var selected = DuplicateFiles.Where(item => item.IsSelectedForRecycleBin).ToArray();
        if (selected.Length == 0 || IsWorking)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            Application.Current.MainWindow,
            string.Format(
                CultureInfo.CurrentCulture,
                _localization.GetText("forgotten-recycle-confirm"),
                selected.Length,
                FormatBytes(selected.Sum(item => item.SizeBytes))),
            _localization.GetText("forgotten-recycle-title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        BeginOperation(scanning: false, _localization.GetText("forgotten-recycle-running"));
        _filesProcessed = 0;
        OnPropertyChanged(nameof(ProgressLabel));
        try
        {
            var token = _operationCancellation!.Token;
            var results = await _service.MoveToRecycleBinAsync(
                selected.Select(item => new RecycleFileRequest(item.Path, item.File.Sha256)),
                new Progress<RecycleFileResult>(_ =>
                {
                    _filesProcessed++;
                    OnPropertyChanged(nameof(ProgressLabel));
                }),
                token);

            foreach (var result in results.Results.Where(result => result.Succeeded))
            {
                var item = selected.FirstOrDefault(candidate =>
                    string.Equals(candidate.Path, result.Path, StringComparison.OrdinalIgnoreCase));
                if (item is not null)
                {
                    DuplicateFiles.Remove(item);
                }
            }

            var affectedGroups = selected.Select(item => item.GroupKey).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var groupKey in affectedGroups)
            {
                var remaining = DuplicateFiles.Where(item => item.GroupKey == groupKey).ToArray();
                if (remaining.Length < 2)
                {
                    foreach (var item in remaining)
                    {
                        item.PropertyChanged -= DuplicateItemChanged;
                        DuplicateFiles.Remove(item);
                    }
                }
                else
                {
                    foreach (var item in remaining)
                    {
                        item.GroupCount = remaining.Length;
                    }
                }
            }

            var failures = results.Results.Where(result => !result.Succeeded).ToArray();
            Status = results.WasCancelled
                ? _localization.GetText("forgotten-cancelled")
                : string.Format(
                    CultureInfo.CurrentCulture,
                    _localization.GetText("forgotten-recycle-complete"),
                    results.Results.Count(result => result.Succeeded),
                    failures.Length);
            ErrorMessage = failures.Length == 0
                ? string.Empty
                : string.Join(Environment.NewLine, failures.Select(result => $"{result.Path}: {result.Error}").Take(10));
            RefreshResultState();
        }
        catch (OperationCanceledException)
        {
            Status = _localization.GetText("forgotten-cancelled");
        }
        finally
        {
            EndOperation();
        }
    }

    private void PopulateResults(ForgottenFilesScanResult result)
    {
        foreach (var file in result.DuplicateGroups.SelectMany(group => group.Files))
        {
            var group = result.DuplicateGroups.First(candidate =>
                string.Equals(candidate.Sha256, file.Sha256, StringComparison.Ordinal)
                && candidate.SizeBytes == file.SizeBytes);
            var viewModel = new ForgottenFileViewModel(
                file,
                $"{group.Sha256}:{group.SizeBytes}",
                group.Files.Count,
                string.Equals(file.Path, group.RecommendedKeepPath, StringComparison.OrdinalIgnoreCase),
                _localization);
            viewModel.PropertyChanged += DuplicateItemChanged;
            DuplicateFiles.Add(viewModel);
        }

        foreach (var file in result.OldFiles)
        {
            OldFiles.Add(new ForgottenFileViewModel(file, string.Empty, 0, false, _localization));
        }

        RefreshResultState();
    }

    private void DuplicateItemChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ForgottenFileViewModel.IsSelectedForRecycleBin))
        {
            RecycleSelectedCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(HasSelectedFiles));
            OnPropertyChanged(nameof(SummaryLabel));
        }
    }

    private bool MatchesSearch(ForgottenFileViewModel? item) =>
        item is null || string.IsNullOrWhiteSpace(SearchText)
        || item.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
        || item.Path.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
        || item.GroupKey.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase);

    private void ClearResults()
    {
        foreach (var item in DuplicateFiles)
        {
            item.PropertyChanged -= DuplicateItemChanged;
        }

        DuplicateFiles.Clear();
        OldFiles.Clear();
        RefreshResultState();
    }

    private void RefreshResultState()
    {
        DuplicateFilesView.Refresh();
        OldFilesView.Refresh();
        RecycleSelectedCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(HasDuplicateFiles));
        OnPropertyChanged(nameof(HasOldFiles));
        OnPropertyChanged(nameof(HasSelectedFiles));
        OnPropertyChanged(nameof(SummaryLabel));
    }

    private void BeginOperation(bool scanning, string status)
    {
        ErrorMessage = string.Empty;
        Status = status;
        _operationCancellation = new CancellationTokenSource();
        _isScanning = scanning;
        _isRecycling = !scanning;
        OnPropertyChanged(nameof(IsWorking));
        ChooseFolderCommand.RaiseCanExecuteChanged();
        ScanCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        RecycleSelectedCommand.RaiseCanExecuteChanged();
    }

    private void EndOperation()
    {
        _isScanning = false;
        _isRecycling = false;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        OnPropertyChanged(nameof(IsWorking));
        ChooseFolderCommand.RaiseCanExecuteChanged();
        ScanCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        RecycleSelectedCommand.RaiseCanExecuteChanged();
    }

    private void RefreshLocalizedValues()
    {
        foreach (var property in new[]
        {
            nameof(ChooseFolderLabel), nameof(ScanLabel), nameof(CancelLabel), nameof(DuplicateTabLabel),
            nameof(OldTabLabel), nameof(RecycleLabel), nameof(FolderLabel), nameof(SearchLabel),
            nameof(OldMonthsLabel), nameof(DuplicateExplanation), nameof(OldFilesDisclaimer),
            nameof(DuplicateEmptyLabel), nameof(OldEmptyLabel), nameof(DuplicateNameHeader),
            nameof(DuplicatePathHeader), nameof(DuplicateSizeHeader), nameof(DuplicateModifiedHeader),
            nameof(DuplicateHashHeader), nameof(DuplicateGroupHeader), nameof(DuplicateKeepHeader),
            nameof(OldNameHeader), nameof(OldPathHeader), nameof(OldSizeHeader), nameof(OldModifiedHeader),
            nameof(SummaryLabel), nameof(ProgressLabel)
        })
        {
            OnPropertyChanged(property);
        }

        foreach (var item in DuplicateFiles.Concat(OldFiles))
        {
            item.RefreshLocalizedValues();
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

public sealed class ForgottenFileViewModel : ObservableObject
{
    private readonly UiLocalizationService _localization;
    private bool _isSelectedForRecycleBin;

    public ForgottenFileViewModel(
        ForgottenFile file,
        string groupKey,
        int groupCount,
        bool isRecommendedKeep,
        UiLocalizationService localization)
    {
        File = file;
        GroupKey = groupKey;
        GroupCount = groupCount;
        IsRecommendedKeep = isRecommendedKeep;
        _localization = localization;
    }

    public ForgottenFile File { get; }
    public string Path => File.Path;
    public string Name => System.IO.Path.GetFileName(File.Path);
    public long SizeBytes => File.SizeBytes;
    public string SizeLabel => $"{SizeBytes.ToString("N0", CultureInfo.CurrentCulture)} B";
    public DateTime LastModified => File.LastModified;
    public string ModifiedLabel => File.LastModified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string GroupKey { get; }
    public string HashLabel => File.Sha256;
    private int _groupCount;
    public int GroupCount
    {
        get => _groupCount;
        set
        {
            if (SetProperty(ref _groupCount, value))
            {
                OnPropertyChanged(nameof(GroupLabel));
            }
        }
    }
    public string GroupLabel => $"{GroupKey[..Math.Min(12, GroupKey.Length)]} · {GroupCount}";
    public bool IsRecommendedKeep { get; }
    public string KeepLabel => IsRecommendedKeep ? _localization.GetText("forgotten-recommended-keep") : string.Empty;

    public bool IsSelectedForRecycleBin
    {
        get => _isSelectedForRecycleBin;
        set
        {
            if (IsRecommendedKeep && value)
            {
                return;
            }

            SetProperty(ref _isSelectedForRecycleBin, value);
        }
    }

    public void RefreshLocalizedValues() => OnPropertyChanged(nameof(KeepLabel));
}
