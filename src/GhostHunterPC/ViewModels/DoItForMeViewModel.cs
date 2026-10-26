using System.Collections.ObjectModel;
using System.IO;
using System.Security;
using System.Text.Json;
using System.Windows;
using GhostHunterPC.Services;

namespace GhostHunterPC.ViewModels;

public sealed class DoItForMeViewModel : ObservableObject
{
    private readonly UiLocalizationService _localization;
    private readonly DoItForMeService _service;
    private readonly PcHistoryService _history;
    private readonly Action<string> _navigate;
    private readonly Func<Task<string>> _refreshDeviceStatus;
    private DoItForMePlan? _plan;
    private bool _isWorking;
    private bool _hasCompletedPlan;
    private string _status = string.Empty;
    private string _errorMessage = string.Empty;

    public DoItForMeViewModel(
        UiLocalizationService localization,
        DoItForMeService service,
        PcHistoryService history,
        Action<string> navigate,
        Func<Task<string>> refreshDeviceStatus)
    {
        _localization = localization;
        _service = service;
        _history = history;
        _navigate = navigate;
        _refreshDeviceStatus = refreshDeviceStatus;
        Tasks = new ObservableCollection<DoItForMeTaskViewModel>(
            service.GetTasks().Select(task => new DoItForMeTaskViewModel(task, localization)));
        ActionLog = new ObservableCollection<DoItForMeLogEntryViewModel>(
            history.Data.Events
                .Where(item => item.Type.StartsWith("DoItForMe:", StringComparison.Ordinal))
                .OrderByDescending(item => item.OccurredAt)
                .Select(item => new DoItForMeLogEntryViewModel(
                    item.OccurredAt,
                    item.Type["DoItForMe:".Length..],
                    item.Details,
                    localization)));

        SelectTaskCommand = new RelayCommand(SelectTask);
        ConfirmCommand = new RelayCommand(_ => _ = ConfirmAndContinueAsync(), _ => CanConfirm);
        CancelPlanCommand = new RelayCommand(_ => CancelPlan(), _ => CanCancelPlan);
        _localization.LanguageChanged += (_, _) => RefreshLocalizedValues();
    }

    public ObservableCollection<DoItForMeTaskViewModel> Tasks { get; }
    public ObservableCollection<DoItForMeLogEntryViewModel> ActionLog { get; }
    public RelayCommand SelectTaskCommand { get; }
    public RelayCommand ConfirmCommand { get; }
    public RelayCommand CancelPlanCommand { get; }

    public DoItForMePlan? Plan
    {
        get => _plan;
        private set
        {
            if (SetProperty(ref _plan, value))
            {
                OnPropertyChanged(nameof(HasPlan));
                OnPropertyChanged(nameof(PlanTaskLabel));
                OnPropertyChanged(nameof(PlanDescription));
                OnPropertyChanged(nameof(PlanImpact));
                OnPropertyChanged(nameof(PlanUndo));
                OnPropertyChanged(nameof(PlanAvailability));
                ConfirmCommand.RaiseCanExecuteChanged();
                CancelPlanCommand.RaiseCanExecuteChanged();
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

    public bool HasPlan => Plan is not null;
    public bool HasActionLog => ActionLog.Count > 0;
    public bool IsWorking => _isWorking;
    public string Intro => _localization.GetText("doit-intro");
    public string SafetyNotice => _localization.GetText("doit-safety");
    public string ChooseTaskLabel => _localization.GetText("doit-choose-task");
    public string PlanHeading => _localization.GetText("doit-plan-heading");
    public string PlanPrompt => _localization.GetText("doit-plan-prompt");
    public string ConfirmLabel => _localization.GetText("doit-confirm-button");
    public string CancelLabel => _localization.GetText("doit-cancel-button");
    public string LogHeading => _localization.GetText("doit-log-heading");
    public string LogEmptyLabel => _localization.GetText("doit-log-empty");
    public string LogTimeHeader => _localization.GetText("doit-log-time");
    public string LogTaskHeader => _localization.GetText("doit-log-task");
    public string LogResultHeader => _localization.GetText("doit-log-result");
    public string PlanTaskLabel => Plan is null ? string.Empty : _localization.GetText($"doit-task-{Plan.Task.Id}");
    public string PlanDescription => Plan is null ? string.Empty : _localization.GetText(Plan.Task.DescriptionKey);
    public string PlanImpact => Plan is null ? string.Empty : _localization.GetText(Plan.Task.ImpactKey);
    public string PlanUndo => Plan is null ? string.Empty : _localization.GetText(Plan.Task.UndoKey);
    public string PlanAvailability => Plan is null
        ? string.Empty
        : _localization.GetText(Plan.Task.IsAvailable ? "doit-available" : "doit-unavailable");
    public string AvailabilityHeader => _localization.GetText("doit-availability");
    public string ImpactHeader => _localization.GetText("doit-impact-heading");
    public string UndoHeader => _localization.GetText("doit-undo-heading");
    public string IsWorkingLabel => _localization.GetText("doit-working");

    private bool CanConfirm => Plan is { Task.IsAvailable: true } && !_isWorking && !_hasCompletedPlan;
    private bool CanCancelPlan => Plan is not null && !_isWorking && !_hasCompletedPlan;

    private void SelectTask(object? parameter)
    {
        if (parameter is not DoItForMeTaskViewModel task || !Tasks.Contains(task))
        {
            return;
        }

        ErrorMessage = string.Empty;
        Status = string.Empty;
        _hasCompletedPlan = false;
        Plan = _service.CreatePlan(task.Task.Id);
        CancelPlanCommand.RaiseCanExecuteChanged();
    }

    private async Task ConfirmAndContinueAsync()
    {
        var plan = Plan;
        if (plan is null || !CanConfirm)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            Application.Current.MainWindow,
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _localization.GetText("doit-confirm-message"),
                _localization.GetText($"doit-task-{plan.Task.Id}"),
                _localization.GetText(plan.Task.ImpactKey)),
            _localization.GetText("doit-confirm-title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            Status = _localization.GetText("doit-confirm-declined");
            return;
        }

        var task = _service.AuthorizePlan(plan, confirmed: true);
        _isWorking = true;
        OnPropertyChanged(nameof(IsWorking));
        ConfirmCommand.RaiseCanExecuteChanged();
        CancelPlanCommand.RaiseCanExecuteChanged();
        ErrorMessage = string.Empty;

        try
        {
            string outcome;
            if (task.ReadsDeviceStatus)
            {
                outcome = await _refreshDeviceStatus();
                _navigate(task.TargetPageKey);
                Status = outcome;
            }
            else
            {
                _navigate(task.TargetPageKey);
                outcome = _localization.GetText("doit-handed-off");
                Status = outcome;
            }

            RecordOutcome(task, outcome);
            _hasCompletedPlan = true;
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            Status = _localization.GetText("doit-operation-failed");
            ErrorMessage = exception.Message;
            RecordOutcome(task, Status);
            _hasCompletedPlan = true;
        }
        finally
        {
            _isWorking = false;
            OnPropertyChanged(nameof(IsWorking));
            ConfirmCommand.RaiseCanExecuteChanged();
            CancelPlanCommand.RaiseCanExecuteChanged();
        }
    }

    private void CancelPlan()
    {
        if (!CanCancelPlan || Plan is null)
        {
            return;
        }

        var task = Plan.Task;
        Plan = null;
        _hasCompletedPlan = false;
        Status = _localization.GetText("doit-plan-cancelled");
        RecordOutcome(task, Status);
    }

    private void RecordOutcome(DoItForMeTask task, string result)
    {
        var entry = new DoItForMeLogEntryViewModel(
            DateTimeOffset.Now,
            task.Id.ToString(),
            result,
            _localization);
        ActionLog.Insert(0, entry);
        OnPropertyChanged(nameof(HasActionLog));

        if (_history.LoadError is not null)
        {
            ErrorMessage = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _localization.GetText("doit-log-failed"),
                _history.LoadError);
            return;
        }

        try
        {
            _history.RecordApplicationEvent($"DoItForMe:{task.Id}", result);
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            ErrorMessage = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _localization.GetText("doit-log-failed"),
                exception.Message);
            return;
        }

        // Keep the existing timeline view in sync with this persisted local event.
        ActionLogged?.Invoke();
    }

    public event Action? ActionLogged;

    private void RefreshLocalizedValues()
    {
        foreach (var task in Tasks)
        {
            task.RefreshLocalizedValues();
        }

        foreach (var entry in ActionLog)
        {
            entry.RefreshLocalizedValues();
        }

        OnPropertyChanged(nameof(Intro));
        OnPropertyChanged(nameof(SafetyNotice));
        OnPropertyChanged(nameof(ChooseTaskLabel));
        OnPropertyChanged(nameof(PlanHeading));
        OnPropertyChanged(nameof(PlanPrompt));
        OnPropertyChanged(nameof(ConfirmLabel));
        OnPropertyChanged(nameof(CancelLabel));
        OnPropertyChanged(nameof(LogHeading));
        OnPropertyChanged(nameof(LogEmptyLabel));
        OnPropertyChanged(nameof(LogTimeHeader));
        OnPropertyChanged(nameof(LogTaskHeader));
        OnPropertyChanged(nameof(LogResultHeader));
        OnPropertyChanged(nameof(PlanTaskLabel));
        OnPropertyChanged(nameof(PlanDescription));
        OnPropertyChanged(nameof(PlanImpact));
        OnPropertyChanged(nameof(PlanUndo));
        OnPropertyChanged(nameof(PlanAvailability));
        OnPropertyChanged(nameof(AvailabilityHeader));
        OnPropertyChanged(nameof(ImpactHeader));
        OnPropertyChanged(nameof(UndoHeader));
        OnPropertyChanged(nameof(IsWorkingLabel));
    }

    private static bool IsExpectedOperationException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or InvalidOperationException
            or JsonException
            or NotSupportedException;
}

public sealed class DoItForMeTaskViewModel(DoItForMeTask task, UiLocalizationService localization) : ObservableObject
{
    public DoItForMeTask Task { get; } = task;
    public string Label => localization.GetText($"doit-task-{Task.Id}");
    public string AvailabilityLabel => localization.GetText(Task.IsAvailable ? "doit-available" : "doit-unavailable");
    public string Description => localization.GetText(Task.DescriptionKey);

    public void RefreshLocalizedValues()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(AvailabilityLabel));
        OnPropertyChanged(nameof(Description));
    }
}

public sealed class DoItForMeLogEntryViewModel(
    DateTimeOffset timestamp,
    string taskId,
    string result,
    UiLocalizationService localization) : ObservableObject
{
    public DateTimeOffset Timestamp { get; } = timestamp;
    public string TaskLabel => Enum.TryParse<DoItForMeTaskId>(taskId, out var id)
        ? localization.GetText($"doit-task-{id}")
        : taskId;
    public string Result { get; } = result;

    public void RefreshLocalizedValues() => OnPropertyChanged(nameof(TaskLabel));
}
