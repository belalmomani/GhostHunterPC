using System.Collections.ObjectModel;
using System.IO;
using System.Security;
using GhostHunterPC.Services;

namespace GhostHunterPC.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly UserSettingsService _service;
    private readonly UiLocalizationService _localization;
    private readonly ThemeService _theme;
    private readonly SystemMonitorViewModel _monitor;
    private readonly PcHistoryViewModel _history;
    private readonly FileFolderPickerService _folderPicker;
    private string _errorMessage = string.Empty;
    private string _status = string.Empty;

    public SettingsViewModel(
        UserSettingsService service,
        UiLocalizationService localization,
        ThemeService theme,
        SystemMonitorViewModel monitor,
        PcHistoryViewModel history,
        FileFolderPickerService folderPicker)
    {
        _service = service;
        _localization = localization;
        _theme = theme;
        _monitor = monitor;
        _history = history;
        _folderPicker = folderPicker;
        ExcludedFolders = new ObservableCollection<string>(service.Settings.ExcludedFolders);
        ExcludedFolders.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasExcludedFolders));
        MonitorIntervals = monitor.RefreshIntervals;
        AddExcludedFolderCommand = new RelayCommand(_ => AddExcludedFolder());
        RemoveExcludedFolderCommand = new RelayCommand(RemoveExcludedFolder);
        _localization.LanguageChanged += (_, _) => RefreshLocalizedValues();
    }

    public ObservableCollection<string> ExcludedFolders { get; }
    public bool HasExcludedFolders => ExcludedFolders.Count > 0;
    public IReadOnlyList<string> LanguageOptions =>
        [_localization.GetText("settings-arabic-option"), _localization.GetText("settings-english-option")];
    public IReadOnlyList<string> ThemeOptions =>
        [_localization.GetText("settings-dark-option"), _localization.GetText("settings-light-option")];
    public IReadOnlyList<string> MonitorIntervals { get; }
    public RelayCommand AddExcludedFolderCommand { get; }
    public RelayCommand RemoveExcludedFolderCommand { get; }
    public string LanguageLabel => _localization.GetText("settings-language");
    public string ThemeLabel => _localization.GetText("settings-theme");
    public string MonitorIntervalLabel => _localization.GetText("settings-monitor-interval");
    public string ExcludedFoldersLabel => _localization.GetText("settings-excluded-folders");
    public string NoExcludedFoldersLabel => _localization.GetText("settings-no-exclusions");
    public string AddFolderLabel => _localization.GetText("settings-add-folder");
    public string RemoveFolderLabel => _localization.GetText("settings-remove-folder");
    public string PrivacyLabel => _localization.GetText("settings-privacy");
    public string PrivacyNotice => _localization.GetText("settings-privacy-notice");
    public string SaveHistoryLabel => _localization.GetText("settings-save-history");
    public string NotificationsLabel => _localization.GetText("settings-notifications");
    public string NotificationsUnavailable => _localization.GetText("settings-notifications-unavailable");
    public string LocalStorageLabel => _localization.GetText("settings-storage-location");
    public string StoragePath => _service.StorageFolder;
    public string SettingsSavedLabel => _localization.GetText("settings-saved");
    public string ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public string SelectedLanguage
    {
        get => _localization.GetText(_service.Settings.Language == "ar" ? "settings-arabic-option" : "settings-english-option");
        set
        {
            var language = value == _localization.GetText("settings-arabic-option") ? "ar"
                : value == _localization.GetText("settings-english-option") ? "en"
                : null;
            if (language is null || language == _service.Settings.Language)
            {
                return;
            }

            _service.Settings.Language = language;
            Save(() => _localization.SetLanguage(language));
        }
    }

    public string SelectedTheme
    {
        get => _localization.GetText(_service.Settings.LightTheme ? "settings-light-option" : "settings-dark-option");
        set
        {
            var light = value == _localization.GetText("settings-light-option");
            if (!light && value != _localization.GetText("settings-dark-option"))
            {
                return;
            }

            if (light == _service.Settings.LightTheme)
            {
                return;
            }

            _service.Settings.LightTheme = light;
            Save(() =>
            {
                _theme.SetLightTheme(light);
                OnPropertyChanged(nameof(SelectedTheme));
            });
        }
    }

    public string SelectedMonitorInterval
    {
        get => _service.Settings.MonitorIntervalSeconds;
        set
        {
            if (!MonitorIntervals.Contains(value, StringComparer.Ordinal)
                || value == _service.Settings.MonitorIntervalSeconds)
            {
                return;
            }

            _service.Settings.MonitorIntervalSeconds = value;
            Save(() =>
            {
                _monitor.SelectedRefreshInterval = value;
                OnPropertyChanged(nameof(SelectedMonitorInterval));
            });
        }
    }

    public bool SaveHistory
    {
        get => _service.Settings.SaveHistory;
        set
        {
            if (value == _service.Settings.SaveHistory)
            {
                return;
            }

            _service.Settings.SaveHistory = value;
            Save(() =>
            {
                _history.SaveEventHistory = value;
                OnPropertyChanged(nameof(SaveHistory));
            });
        }
    }

    private void AddExcludedFolder()
    {
        var path = _folderPicker.PickFolder(_localization.GetText("settings-add-folder"));
        if (path is null)
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        if (ExcludedFolders.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        ExcludedFolders.Add(fullPath);
        _service.Settings.ExcludedFolders.Add(fullPath);
        Save(() => OnPropertyChanged(nameof(ExcludedFolders)));
    }

    private void RemoveExcludedFolder(object? parameter)
    {
        if (parameter is not string path || !ExcludedFolders.Remove(path))
        {
            return;
        }

        _service.Settings.ExcludedFolders.RemoveAll(item =>
            string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        Save(() => OnPropertyChanged(nameof(ExcludedFolders)));
    }

    private void Save(Action afterSave)
    {
        try
        {
            _service.Save();
            afterSave();
            ErrorMessage = string.Empty;
            Status = SettingsSavedLabel;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or System.Text.Json.JsonException)
        {
            ErrorMessage = exception.Message;
            Status = _localization.GetText("settings-save-failed");
        }
    }

    public void SavePreferences()
    {
        Save(() =>
        {
            _history.SaveEventHistory = _service.Settings.SaveHistory;
            _monitor.SelectedRefreshInterval = _service.Settings.MonitorIntervalSeconds;
            _theme.SetLightTheme(_service.Settings.LightTheme);
            _localization.SetLanguage(_service.Settings.Language);
        });
    }

    private void RefreshLocalizedValues()
    {
        OnPropertyChanged(nameof(LanguageLabel));
        OnPropertyChanged(nameof(ThemeLabel));
        OnPropertyChanged(nameof(MonitorIntervalLabel));
        OnPropertyChanged(nameof(ExcludedFoldersLabel));
        OnPropertyChanged(nameof(NoExcludedFoldersLabel));
        OnPropertyChanged(nameof(AddFolderLabel));
        OnPropertyChanged(nameof(RemoveFolderLabel));
        OnPropertyChanged(nameof(PrivacyLabel));
        OnPropertyChanged(nameof(PrivacyNotice));
        OnPropertyChanged(nameof(SaveHistoryLabel));
        OnPropertyChanged(nameof(NotificationsLabel));
        OnPropertyChanged(nameof(NotificationsUnavailable));
        OnPropertyChanged(nameof(LocalStorageLabel));
        OnPropertyChanged(nameof(SettingsSavedLabel));
        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(nameof(SelectedTheme));
        OnPropertyChanged(nameof(LanguageOptions));
        OnPropertyChanged(nameof(ThemeOptions));
    }
}
