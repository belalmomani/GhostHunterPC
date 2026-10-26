namespace GhostHunterPC.ViewModels;

public sealed class PageViewModel
{
    public PageViewModel(string key, string title, string description, bool isDashboard,
        string emptyStateTitle, string emptyStateDescription, IReadOnlyList<StatusCardViewModel> cards)
    {
        Key = key;
        Title = title;
        Description = description;
        IsDashboard = isDashboard;
        EmptyStateTitle = emptyStateTitle;
        EmptyStateDescription = emptyStateDescription;
        Cards = cards;
    }

    public string Key { get; }
    public string Title { get; }
    public string Description { get; }
    public bool IsDashboard { get; }
    public bool IsDoItForMe => Key == "do-it-for-me";
    public string EmptyStateTitle { get; }
    public string EmptyStateDescription { get; }
    public IReadOnlyList<StatusCardViewModel> Cards { get; }
    public string WorkspaceNotice { get; init; } = string.Empty;
    public string DashboardStatus { get; init; } = string.Empty;
    public string DashboardErrorDetails { get; init; } = string.Empty;
    public bool IsInvestigator => Key == "investigator";
    public bool IsOrganizer => Key == "organizer";
    public bool IsDuplicates => Key == "duplicates";
    public bool IsMonitor => Key == "monitor";
    public bool IsFingerprint => Key == "fingerprint";
    public bool IsTimeline => Key == "timeline";
    public bool IsReports => Key == "reports";
    public bool IsSettings => Key == "settings";
    public FileInvestigatorViewModel? Investigator { get; init; }
    public SmartFileOrganizerViewModel? Organizer { get; init; }
    public ForgottenFilesViewModel? ForgottenFiles { get; init; }
    public SystemMonitorViewModel? SystemMonitor { get; init; }
    public PcHistoryViewModel? PcHistory { get; init; }
    public DoItForMeViewModel? DoItForMe { get; init; }
    public ReportsViewModel? Reports { get; init; }
    public SettingsViewModel? Settings { get; init; }
}
