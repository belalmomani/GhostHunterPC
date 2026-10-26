namespace GhostHunterPC.ViewModels;

public sealed class NavigationItemViewModel : ObservableObject
{
    private bool _isSelected;

    public NavigationItemViewModel(string key, string label, string iconGlyph)
    {
        Key = key;
        Label = label;
        IconGlyph = iconGlyph;
    }

    public string Key { get; }
    public string Label { get; }
    public string IconGlyph { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
