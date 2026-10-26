namespace GhostHunterPC.ViewModels;

public sealed class StatusCardViewModel
{
    public StatusCardViewModel(string label, string value, string detail)
    {
        Label = label;
        Value = value;
        Detail = detail;
    }

    public string Label { get; }
    public string Value { get; }
    public string Detail { get; }
}
