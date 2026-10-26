using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using GhostHunterPC.Services;

namespace GhostHunterPC.ViewModels;

public sealed class PcHistoryViewModel : ObservableObject
{
    private readonly UiLocalizationService _localization;
    private readonly PcHistoryService _service;
    private readonly ObservableCollection<TimelineEventViewModel> _events = [];
    private readonly ObservableCollection<InventoryItemViewModel> _baselineItems = [];
    private readonly ObservableCollection<InventoryChangeViewModel> _changes = [];
    private string _status = string.Empty;
    private string _errorMessage = string.Empty;
    private string _selectedEventType = string.Empty;
    private DateTime? _fromDate;
    private DateTime? _toDate;
    private bool _isBusy;

    public PcHistoryViewModel(UiLocalizationService localization, PcHistoryService service)
    {
        _localization = localization;
        _service = service;
        EventsView = CollectionViewSource.GetDefaultView(_events);
        EventsView.Filter = FilterEvent;
        CreateBaselineCommand = new RelayCommand(_ => _ = CreateBaselineAsync(), _ => !IsBusy);
        CompareCommand = new RelayCommand(_ => _ = CompareAsync(), _ => !IsBusy && _service.Data.Baseline is not null);
        ImportWindowsEventsCommand = new RelayCommand(_ => _ = ImportWindowsEventsAsync(), _ => !IsBusy);
        ExportCommand = new RelayCommand(_ => Export(), _ => !IsBusy);
        if (_service.LoadError is null)
        {
            try
            {
                _service.RecordApplicationEvent("Application started", "GhostHunterPC started; no earlier activity is implied.");
            }
            catch (Exception exception) when (IsExpectedHistoryException(exception))
            {
                ErrorMessage = exception.Message;
            }
        }

        RefreshEvents();
        if (_service.LoadError is not null)
        {
            ErrorMessage = _service.LoadError;
        }

        _localization.LanguageChanged += (_, _) => RefreshLocalizedValues();
    }

    public ICollectionView EventsView { get; }
    public ObservableCollection<InventoryItemViewModel> BaselineItems => _baselineItems;
    public ObservableCollection<InventoryChangeViewModel> Changes => _changes;
    public IReadOnlyList<string> EventTypes => new[] { AllEventsLabel }.Concat(
        _events.Select(item => item.Type).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal)).ToArray();

    public RelayCommand CreateBaselineCommand { get; }
    public RelayCommand CompareCommand { get; }
    public RelayCommand ImportWindowsEventsCommand { get; }
    public RelayCommand ExportCommand { get; }

    public string CreateBaselineLabel => _localization.GetText("history-create-baseline");
    public string CompareLabel => _localization.GetText("history-compare");
    public string ImportWindowsLabel => _localization.GetText("history-import-windows");
    public string ExportLabel => _localization.GetText("history-export");
    public string TimelineLabel => _localization.GetText("history-timeline");
    public string BaselineLabel => _localization.GetText("history-baseline");
    public string ChangesLabel => _localization.GetText("history-changes");
    public string DetailsLabel => _localization.GetText("history-details");
    public string SourceLabel => _localization.GetText("history-source");
    public string TypeLabel => _localization.GetText("history-type");
    public string TimeLabel => _localization.GetText("history-time");
    public string AddedLabel => _localization.GetText("history-added");
    public string RemovedLabel => _localization.GetText("history-removed");
    public string ChangedLabel => _localization.GetText("history-changed");
    public string CategoryLabel => _localization.GetText("history-category");
    public string KeyLabel => _localization.GetText("history-key");
    public string BeforeLabel => _localization.GetText("history-before");
    public string AfterLabel => _localization.GetText("history-after");
    public string FromDateLabel => _localization.GetText("history-from-date");
    public string ToDateLabel => _localization.GetText("history-to-date");
    public string AllEventsLabel => _localization.GetText("history-all-types");
    public string EmptyTimelineLabel => _localization.GetText("history-empty-timeline");
    public string EmptyChangesLabel => _localization.GetText("history-empty-changes");
    public string EmptyBaselineItemsLabel => _localization.GetText("history-empty-baseline");
    public string PrivacyNotice => _localization.GetText("history-privacy");
    public string BaselineSummary => _service.Data.Baseline is { } baseline
        ? string.Format(
            CultureInfo.CurrentCulture,
            _localization.GetText("history-baseline-summary"),
            baseline.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            baseline.Items.Count,
            baseline.Errors.Count)
        : _localization.GetText("history-no-baseline");
    public bool HasChanges => Changes.Count > 0;
    public bool HasEvents => !EventsView.IsEmpty;
    public bool HasBaselineItems => BaselineItems.Count > 0;
    public bool HasBaseline => _service.Data.Baseline is not null;
    public bool SaveEventHistory
    {
        get => _service.SaveEventHistory;
        set => _service.SaveEventHistory = value;
    }

    public void RefreshFromService()
    {
        RefreshEvents();
        OnPropertyChanged(nameof(HasEvents));
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                CreateBaselineCommand.RaiseCanExecuteChanged();
                CompareCommand.RaiseCanExecuteChanged();
                ImportWindowsEventsCommand.RaiseCanExecuteChanged();
                ExportCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SelectedEventType
    {
        get => _selectedEventType;
        set
        {
            if (SetProperty(ref _selectedEventType, value))
            {
                EventsView.Refresh();
                OnPropertyChanged(nameof(HasEvents));
            }
        }
    }

    public DateTime? FromDate
    {
        get => _fromDate;
        set
        {
            if (SetProperty(ref _fromDate, value))
            {
                EventsView.Refresh();
                OnPropertyChanged(nameof(HasEvents));
            }
        }
    }

    public DateTime? ToDate
    {
        get => _toDate;
        set
        {
            if (SetProperty(ref _toDate, value))
            {
                EventsView.Refresh();
                OnPropertyChanged(nameof(HasEvents));
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

    private async Task CreateBaselineAsync()
    {
        if (_service.Data.Baseline is not null
            && MessageBox.Show(
                _localization.GetText("history-replace-confirm"),
                _localization.GetText("history-confirm-title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var baseline = await RunAsync(() => _service.CreateBaseline());
        if (baseline is not null)
        {
            RefreshBaselineItems();
            Changes.Clear();
            OnPropertyChanged(nameof(HasChanges));
            ErrorMessage = string.Join(Environment.NewLine, baseline.Errors);
            Status = string.Format(
                CultureInfo.CurrentCulture,
                _localization.GetText("history-baseline-created"),
                baseline.Items.Count,
                baseline.Errors.Count);
            OnPropertyChanged(nameof(BaselineSummary));
            OnPropertyChanged(nameof(HasBaseline));
            CompareCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task CompareAsync()
    {
        var comparison = await RunAsync(() => _service.CompareWithBaseline());
        if (comparison is not null)
        {
            Changes.Clear();
            foreach (var change in comparison.Changes)
            {
                Changes.Add(new InventoryChangeViewModel(change, _localization));
            }

            OnPropertyChanged(nameof(HasChanges));
            ErrorMessage = string.Join(Environment.NewLine, comparison.Errors);
            Status = string.Format(
                CultureInfo.CurrentCulture,
                _localization.GetText("history-comparison-complete"),
                comparison.Changes.Count(item => item.Status == "Added"),
                comparison.Changes.Count(item => item.Status == "Removed"),
                comparison.Changes.Count(item => item.Status == "Changed"),
                comparison.Errors.Count);
        }
    }

    private async Task ImportWindowsEventsAsync()
    {
        var result = await RunAsync(() => new ImportedWindowsEvents(_service.ImportRecentWindowsEvents()));
        if (result is not null)
        {
            RefreshEvents();
            Status = string.Format(CultureInfo.CurrentCulture, _localization.GetText("history-windows-imported"), result.Count);
        }
    }

    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            Title = _localization.GetText("history-export"),
            Filter = _localization.GetText("history-export-filter"),
            DefaultExt = ".json",
            AddExtension = true,
            FileName = "GhostHunterPC-history.json"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _service.Export(dialog.FileName);
            ErrorMessage = string.Empty;
            Status = _localization.GetText("history-exported");
        }
        catch (Exception exception) when (IsExpectedHistoryException(exception))
        {
            ErrorMessage = exception.Message;
            Status = _localization.GetText("history-failed");
        }
    }

    private async Task<T?> RunAsync<T>(Func<T> operation) where T : class
    {
        if (IsBusy)
        {
            return null;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var result = await Task.Run(operation);
            RefreshEvents();
            OnPropertyChanged(nameof(BaselineSummary));
            return result;
        }
        catch (Exception exception) when (IsExpectedHistoryException(exception))
        {
            ErrorMessage = exception.Message;
            Status = _localization.GetText("history-failed");
            return default;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool FilterEvent(object item)
    {
        if (item is not TimelineEventViewModel timelineEvent)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SelectedEventType)
            && SelectedEventType != AllEventsLabel
            && timelineEvent.Type != SelectedEventType)
        {
            return false;
        }

        var localDate = timelineEvent.Event.OccurredAt.LocalDateTime;
        return (FromDate is null || localDate.Date >= FromDate.Value.Date)
            && (ToDate is null || localDate.Date <= ToDate.Value.Date);
    }

    private void RefreshEvents()
    {
        _events.Clear();
        foreach (var timelineEvent in _service.Data.Events.OrderByDescending(item => item.OccurredAt))
        {
            _events.Add(new TimelineEventViewModel(timelineEvent));
        }

        if (!EventTypes.Contains(SelectedEventType, StringComparer.Ordinal))
        {
            SelectedEventType = AllEventsLabel;
        }

        EventsView.Refresh();
        OnPropertyChanged(nameof(EventTypes));
        OnPropertyChanged(nameof(HasEvents));
    }

    private void RefreshLocalizedValues()
    {
        OnPropertyChanged(string.Empty);
        foreach (var change in Changes)
        {
            change.RefreshLocalizedValues(_localization);
        }

        if (!EventTypes.Contains(SelectedEventType, StringComparer.Ordinal))
        {
            SelectedEventType = AllEventsLabel;
        }

        OnPropertyChanged(nameof(EventTypes));
        EventsView.Refresh();
    }

    private void RefreshBaselineItems()
    {
        BaselineItems.Clear();
        if (_service.Data.Baseline is { } baseline)
        {
            foreach (var item in baseline.Items.OrderBy(item => item.Category, StringComparer.Ordinal)
                         .ThenBy(item => item.Key, StringComparer.Ordinal))
            {
                BaselineItems.Add(new InventoryItemViewModel(item));
            }
        }

        OnPropertyChanged(nameof(HasBaselineItems));
    }

    private static bool IsExpectedHistoryException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException
            or TimeoutException
            or InvalidDataException
            or System.Xml.XmlException
            or JsonException;
}

internal sealed record ImportedWindowsEvents(int Count);

public sealed class TimelineEventViewModel
{
    private readonly TimelineEvent _event;

    public TimelineEventViewModel(TimelineEvent timelineEvent)
    {
        _event = timelineEvent;
    }

    public TimelineEvent Event => _event;
    public string Type => _event.Type;
    public string Source => _event.Source;
    public string Details => _event.Details;
    public DateTimeOffset OccurredAt => _event.OccurredAt;
    public string TimeLabel => _event.OccurredAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}

public sealed class InventoryItemViewModel(InventoryItem item)
{
    public string Category => item.Category;
    public string Key => item.Key;
    public string Value => item.Value;
    public DateTimeOffset CollectedAt => item.CollectedAt;
    public string CollectedAtLabel => item.CollectedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}

public sealed class InventoryChangeViewModel(InventoryChange change, UiLocalizationService localization) : ObservableObject
{
    public string Status => change.Status switch
    {
        "Added" => localization.GetText("history-added"),
        "Removed" => localization.GetText("history-removed"),
        "Changed" => localization.GetText("history-changed"),
        _ => change.Status
    };

    public string Category => change.Category;
    public string Key => change.Key;
    public string PreviousValue => change.PreviousValue ?? "—";
    public string CurrentValue => change.CurrentValue ?? "—";
    public string ObservedAtLabel => change.ObservedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public void RefreshLocalizedValues(UiLocalizationService _) => OnPropertyChanged(nameof(Status));
}
