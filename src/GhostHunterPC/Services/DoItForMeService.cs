namespace GhostHunterPC.Services;

public enum DoItForMeTaskId
{
    OrganizeDownloads,
    FindLargeFiles,
    FindDuplicates,
    ReviewStartupApps,
    CollectDeviceStatus
}

public sealed record DoItForMeTask(
    DoItForMeTaskId Id,
    string DescriptionKey,
    string ImpactKey,
    string UndoKey,
    string TargetPageKey,
    bool IsAvailable,
    bool ReadsDeviceStatus = false);

public sealed record DoItForMePlan(DoItForMeTask Task, DateTimeOffset CreatedAt);

public sealed class DoItForMeService
{
    private static readonly IReadOnlyDictionary<DoItForMeTaskId, DoItForMeTask> AllowedTasks =
        new Dictionary<DoItForMeTaskId, DoItForMeTask>
        {
            [DoItForMeTaskId.OrganizeDownloads] = new(
                DoItForMeTaskId.OrganizeDownloads,
                "doit-plan-organize",
                "doit-impact-organize",
                "doit-undo-organize",
                "organizer",
                IsAvailable: true),
            [DoItForMeTaskId.FindLargeFiles] = new(
                DoItForMeTaskId.FindLargeFiles,
                "doit-plan-large",
                "doit-impact-large",
                "doit-undo-readonly",
                "investigator",
                IsAvailable: false),
            [DoItForMeTaskId.FindDuplicates] = new(
                DoItForMeTaskId.FindDuplicates,
                "doit-plan-duplicates",
                "doit-impact-duplicates",
                "doit-undo-duplicates",
                "duplicates",
                IsAvailable: true),
            [DoItForMeTaskId.ReviewStartupApps] = new(
                DoItForMeTaskId.ReviewStartupApps,
                "doit-plan-startup",
                "doit-impact-startup",
                "doit-undo-startup",
                "fingerprint",
                IsAvailable: true),
            [DoItForMeTaskId.CollectDeviceStatus] = new(
                DoItForMeTaskId.CollectDeviceStatus,
                "doit-plan-device",
                "doit-impact-device",
                "doit-undo-readonly",
                "dashboard",
                IsAvailable: true,
                ReadsDeviceStatus: true)
        };

    public IReadOnlyList<DoItForMeTask> GetTasks() => AllowedTasks.Values.ToArray();

    public DoItForMePlan CreatePlan(DoItForMeTaskId taskId)
    {
        if (!AllowedTasks.TryGetValue(taskId, out var task))
        {
            throw new ArgumentOutOfRangeException(nameof(taskId), "This task is not in the allowed action list.");
        }

        return new DoItForMePlan(task, DateTimeOffset.Now);
    }

    public DoItForMeTask AuthorizePlan(DoItForMePlan plan, bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!confirmed)
        {
            throw new InvalidOperationException("Explicit user confirmation is required before continuing.");
        }

        if (!AllowedTasks.TryGetValue(plan.Task.Id, out var allowedTask)
            || allowedTask != plan.Task)
        {
            throw new InvalidOperationException("The requested action is not in the allowed action list.");
        }

        if (!allowedTask.IsAvailable)
        {
            throw new InvalidOperationException("This task is not available and cannot be executed.");
        }

        return allowedTask;
    }
}
