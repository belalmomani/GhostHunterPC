using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security;
using System.Windows;
using GhostHunterPC.Services;

namespace GhostHunterPC.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly (string Key, string Icon)[] Pages =
    [
        ("dashboard", "\uE80F"),
        ("do-it-for-me", "\uE8FB"),
        ("scan", "\uE721"),
        ("investigator", "\uE8A5"),
        ("organizer", "\uE8B7"),
        ("duplicates", "\uE8C8"),
        ("monitor", "\uE7F4"),
        ("fingerprint", "\uE72E"),
        ("timeline", "\uE823"),
        ("reports", "\uE9D2"),
        ("settings", "\uE713")
    ];

    private readonly UiLocalizationService _localization;
    private readonly ThemeService _theme;
    private readonly DeviceInfoService _deviceInfoService;
    private readonly UserSettingsService _settingsService;
    private string _selectedPageKey = "dashboard";
    private PageViewModel _currentPage = null!;
    private DeviceInfoSnapshot? _deviceInfo;
    private DateTimeOffset? _lastDeviceRefresh;
    private string? _refreshFailure;
    private bool _isRefreshing;

    public MainViewModel(
        UiLocalizationService localization,
        ThemeService theme,
        DeviceInfoService deviceInfoService,
        UserSettingsService? settingsService = null)
    {
        _localization = localization;
        _theme = theme;
        _deviceInfoService = deviceInfoService;
        _settingsService = settingsService ?? new UserSettingsService();
        _localization.SetLanguage(_settingsService.Settings.Language);
        _theme.SetLightTheme(_settingsService.Settings.LightTheme);
        IReadOnlyCollection<string> GetExcludedFolders() => _settingsService.Settings.ExcludedFolders;
        FileInvestigator = new FileInvestigatorViewModel(
            localization,
            new FileInvestigationService(),
            new FileFolderPickerService(),
            GetExcludedFolders);
        FileOrganizer = new SmartFileOrganizerViewModel(
            localization,
            new SmartFileOrganizerService(),
            new FileFolderPickerService(),
            GetExcludedFolders);
        ForgottenFiles = new ForgottenFilesViewModel(
            localization,
            new ForgottenFilesService(),
            new FileFolderPickerService(),
            GetExcludedFolders);
        SystemMonitor = new SystemMonitorViewModel(localization, new SystemMonitorService());
        var pcHistoryService = new PcHistoryService();
        pcHistoryService.SaveEventHistory = _settingsService.Settings.SaveHistory;
        PcHistory = new PcHistoryViewModel(localization, pcHistoryService);
        DoItForMe = new DoItForMeViewModel(
            localization,
            new DoItForMeService(),
            pcHistoryService,
            NavigateToPage,
            RefreshDeviceInfoForDoItForMeAsync);
        DoItForMe.ActionLogged += PcHistory.RefreshFromService;
        Settings = new SettingsViewModel(
            _settingsService,
            localization,
            theme,
            SystemMonitor,
            PcHistory,
            new FileFolderPickerService());
        Settings.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(SettingsViewModel.SelectedTheme))
            {
                OnPropertyChanged(nameof(ThemeActionLabel));
            }
        };
        SystemMonitor.SelectedRefreshInterval = _settingsService.Settings.MonitorIntervalSeconds;
        Reports = new ReportsViewModel(
            localization,
            new ReportExportService(),
            () => _deviceInfo,
            FileInvestigator,
            ForgottenFiles,
            SystemMonitor,
            PcHistory);
        NavigationItems = [];
        SelectPageCommand = new RelayCommand(SelectPage);
        RefreshDeviceInfoCommand = new RelayCommand(
            _ => _ = RefreshDeviceInfoAsync(),
            _ => !IsRefreshing);
        ToggleLanguageCommand = new RelayCommand(_ => _localization.ToggleLanguage());
        ToggleThemeCommand = new RelayCommand(_ =>
        {
            _theme.ToggleTheme();
            _settingsService.Settings.LightTheme = _theme.IsLight;
            Settings.SavePreferences();
            OnPropertyChanged(nameof(ThemeActionLabel));
        });

        _localization.LanguageChanged += (_, _) =>
        {
            _settingsService.Settings.Language = _localization.IsArabic ? "ar" : "en";
            Settings.SavePreferences();
            RefreshLocalizedContent();
        };
        RefreshLocalizedContent();
        _ = RefreshDeviceInfoAsync();
    }

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }
    public RelayCommand SelectPageCommand { get; }
    public RelayCommand RefreshDeviceInfoCommand { get; }
    public RelayCommand ToggleLanguageCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public FileInvestigatorViewModel FileInvestigator { get; }
    public SmartFileOrganizerViewModel FileOrganizer { get; }
    public ForgottenFilesViewModel ForgottenFiles { get; }
    public SystemMonitorViewModel SystemMonitor { get; }
    public PcHistoryViewModel PcHistory { get; }
    public DoItForMeViewModel DoItForMe { get; }
    public ReportsViewModel Reports { get; }
    public SettingsViewModel Settings { get; }

    public string ProgramName => "GHOST HUNTER PC";
    public string NavigationHeading => _localization.GetText("navigation");
    public string LanguageActionLabel => _localization.GetText("language-action");
    public string ThemeActionLabel => _localization.GetText(_theme.IsLight ? "switch-to-dark" : "switch-to-light");
    public string SidebarFooter => _localization.GetText("footer");
    public FlowDirection Direction => _localization.IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    public int SidebarColumn => _localization.IsArabic ? 1 : 0;
    public int MainContentColumn => _localization.IsArabic ? 0 : 1;
    public GridLength FirstColumnWidth => _localization.IsArabic
        ? new GridLength(1, GridUnitType.Star)
        : new GridLength(280);
    public GridLength SecondColumnWidth => _localization.IsArabic
        ? new GridLength(280)
        : new GridLength(1, GridUnitType.Star);

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                RefreshDeviceInfoCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(RefreshButtonLabel));
                RefreshLocalizedContent();
            }
        }
    }

    public string RefreshButtonLabel => _localization.GetText(
        IsRefreshing ? "refreshing" : "refresh-device-info");

    public PageViewModel CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    private void SelectPage(object? parameter)
    {
        if (parameter is not NavigationItemViewModel item)
        {
            return;
        }

        _selectedPageKey = item.Key;
        RefreshLocalizedContent();
    }

    private async Task<string> RefreshDeviceInfoAsync()
    {
        if (IsRefreshing)
        {
            return _localization.GetText("device-info-already-refreshing");
        }

        _refreshFailure = null;
        IsRefreshing = true;
        RefreshLocalizedContent();

        try
        {
            _deviceInfo = await Task.Run(_deviceInfoService.Read);
            _lastDeviceRefresh = DateTimeOffset.Now;
        }
        catch (Exception exception) when (IsExpectedReadException(exception))
        {
            _refreshFailure = exception.Message;
        }
        finally
        {
            IsRefreshing = false;
            RefreshLocalizedContent();
        }

        return _localization.GetText(_refreshFailure is not null
            ? "device-info-failed"
            : _deviceInfo?.Errors.Count > 0
                ? "device-info-partial"
                : "device-info-ready");
    }

    private void RefreshLocalizedContent()
    {
        OnPropertyChanged(nameof(RefreshButtonLabel));
        NavigationItems.Clear();
        foreach (var (key, icon) in Pages)
        {
            NavigationItems.Add(new NavigationItemViewModel(
                key,
                _localization.GetText(key),
                icon)
            {
                IsSelected = key == _selectedPageKey
            });
        }

        var isDashboard = _selectedPageKey == "dashboard";
        var cards = isDashboard ? BuildDashboardCards() : Array.Empty<StatusCardViewModel>();
        var errors = _deviceInfo?.Errors
            .Distinct()
            .Select(section => _localization.GetText($"error-{section}"))
            .ToList() ?? [];
        if (!string.IsNullOrWhiteSpace(_refreshFailure))
        {
            errors.Add(_refreshFailure);
        }

        var dashboardStatus = IsRefreshing
            ? _localization.GetText("refreshing")
            : _refreshFailure is not null
                ? _localization.GetText("device-info-failed")
                : _deviceInfo is null
                    ? _localization.GetText("loading")
                    : _deviceInfo.Errors.Count == 0
                        ? _localization.GetText("device-info-ready")
                        : _localization.GetText("device-info-partial");

        CurrentPage = new PageViewModel(
            _selectedPageKey,
            _localization.GetText(_selectedPageKey),
            _localization.GetText($"{_selectedPageKey}-description"),
            isDashboard,
            _localization.GetText("empty-title"),
            _localization.GetText("empty-description"),
            cards)
        {
            WorkspaceNotice = _localization.GetText("notice"),
            DashboardStatus = dashboardStatus,
            DashboardErrorDetails = errors.Count == 0
                ? string.Empty
                : $"{_localization.GetText("error-details")} {string.Join("، ", errors)}",
            Investigator = FileInvestigator,
            Organizer = FileOrganizer,
            ForgottenFiles = ForgottenFiles,
            SystemMonitor = SystemMonitor,
            PcHistory = PcHistory,
            DoItForMe = DoItForMe,
            Reports = Reports,
            Settings = Settings
        };

        OnPropertyChanged(nameof(NavigationHeading));
        OnPropertyChanged(nameof(LanguageActionLabel));
        OnPropertyChanged(nameof(ThemeActionLabel));
        OnPropertyChanged(nameof(SidebarFooter));
        OnPropertyChanged(nameof(Direction));
        OnPropertyChanged(nameof(SidebarColumn));
        OnPropertyChanged(nameof(MainContentColumn));
        OnPropertyChanged(nameof(FirstColumnWidth));
        OnPropertyChanged(nameof(SecondColumnWidth));
    }

    private StatusCardViewModel[] BuildDashboardCards()
    {
        var unavailable = _localization.GetText("unavailable");
        var loading = _localization.GetText("loading");
        var system = _deviceInfo;

        var operatingSystem = system?.OperatingSystem ?? (IsRefreshing ? loading : unavailable);
        var operatingSystemDetails = new List<string>();
        if (!string.IsNullOrWhiteSpace(system?.OperatingSystemVersion))
        {
            operatingSystemDetails.Add($"{_localization.GetText("system-version")}: {system.OperatingSystemVersion}");
        }

        if (!string.IsNullOrWhiteSpace(system?.Architecture))
        {
            operatingSystemDetails.Add($"{_localization.GetText("architecture")}: {system.Architecture}");
        }

        var processor = system?.ProcessorName ?? (IsRefreshing ? loading : unavailable);
        var processorDetails = new List<string>();
        if (system?.PhysicalCores is int cores)
        {
            processorDetails.Add($"{_localization.GetText("physical-cores")}: {cores}");
        }
        else
        {
            processorDetails.Add($"{_localization.GetText("physical-cores")}: {unavailable}");
        }

        if (system?.LogicalProcessors is int logicalProcessors)
        {
            processorDetails.Add($"{_localization.GetText("logical-processors")}: {logicalProcessors}");
        }

        var memory = system?.TotalMemoryBytes is ulong totalMemory
            ? FormatBytes(totalMemory)
            : IsRefreshing ? loading : unavailable;
        var memoryDetails = system?.AvailableMemoryBytes is ulong availableMemory
            ? $"{_localization.GetText("available")}: {FormatBytes(availableMemory)}"
            : unavailable;

        var disks = system?.Drives ?? [];
        var diskTotal = disks.Count == 0
            ? IsRefreshing && system is null ? loading : unavailable
            : FormatBytes(disks.Aggregate<DriveSnapshot, ulong>(0, (total, drive) => total + drive.TotalBytes));
        var diskDetails = disks.Count == 0
            ? unavailable
            : string.Join(Environment.NewLine, disks.Select(drive =>
                $"{drive.Name}  {FormatBytes(drive.AvailableBytes)} {_localization.GetText("disk-free")} / {FormatBytes(drive.TotalBytes)}"));

        var uptime = system?.Uptime is TimeSpan duration
            ? FormatUptime(duration, _localization.GetText("uptime-days"))
            : IsRefreshing ? loading : unavailable;
        var appStatus = _localization.GetText(system is null
            ? IsRefreshing ? "loading" : "unavailable"
            : "running");
        var appStatusDetail = system is null
            ? _localization.GetText(_refreshFailure is null ? "loading" : "device-info-failed")
            : _localization.GetText(system.Errors.Count == 0 ? "device-info-ready" : "reader-partial");
        var lastRefresh = _lastDeviceRefresh?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            ?? _localization.GetText("never-refreshed");

        return
        [
            new StatusCardViewModel(_localization.GetText("operating-system"), operatingSystem,
                operatingSystemDetails.Count == 0 ? unavailable : string.Join(" · ", operatingSystemDetails)),
            new StatusCardViewModel(_localization.GetText("processor"), processor,
                processorDetails.Count == 0 ? unavailable : string.Join(" · ", processorDetails)),
            new StatusCardViewModel(_localization.GetText("memory"), memory, memoryDetails),
            new StatusCardViewModel(_localization.GetText("disks"), diskTotal, diskDetails),
            new StatusCardViewModel(_localization.GetText("uptime"), uptime,
                system?.Uptime is null && system is null ? loading : string.Empty),
            new StatusCardViewModel(_localization.GetText("application-status"), appStatus, appStatusDetail),
            new StatusCardViewModel(_localization.GetText("last-program-scan"),
                _localization.GetText("scan-not-run"),
                _localization.GetText("scan-not-implemented")),
            new StatusCardViewModel(_localization.GetText("last-device-refresh"), lastRefresh,
                _localization.GetText("last-device-refresh-detail"))
        ];
    }

    private static string FormatBytes(ulong bytes)
    {
        const double gibibyte = 1024d * 1024 * 1024;
        return $"{bytes / gibibyte:N1} GiB";
    }

    private static string FormatUptime(TimeSpan uptime, string daysLabel) =>
        $"{(int)uptime.TotalDays} {daysLabel} {uptime.Hours:D2}:{uptime.Minutes:D2}";

    private static bool IsExpectedReadException(Exception exception) =>
        exception is IOException
        or UnauthorizedAccessException
        or SecurityException
        or InvalidOperationException
        or Win32Exception
        or DllNotFoundException
        or EntryPointNotFoundException
        or OverflowException;

    private void NavigateToPage(string pageKey)
    {
        if (!Pages.Any(page => page.Key == pageKey))
        {
            throw new ArgumentOutOfRangeException(nameof(pageKey), "The requested page is not available.");
        }

        _selectedPageKey = pageKey;
        RefreshLocalizedContent();
    }

    private Task<string> RefreshDeviceInfoForDoItForMeAsync() => RefreshDeviceInfoAsync();

}
