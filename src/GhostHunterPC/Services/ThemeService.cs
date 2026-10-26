using System.Windows;
using System.Windows.Media;

namespace GhostHunterPC.Services;

public sealed class ThemeService
{
    private bool _isLight;

    public bool IsLight => _isLight;

    public void ToggleTheme()
    {
        SetLightTheme(!_isLight);
    }

    public void SetLightTheme(bool isLight)
    {
        _isLight = isLight;
        var palette = _isLight
            ? new Dictionary<string, string>
            {
                ["WindowBackgroundBrush"] = "#F3F5F9",
                ["SidebarBackgroundBrush"] = "#FFFFFF",
                ["SurfaceBrush"] = "#FFFFFF",
                ["CardBrush"] = "#FFFFFF",
                ["PrimaryTextBrush"] = "#172033",
                ["SecondaryTextBrush"] = "#5E687A",
                ["BorderBrush"] = "#DCE1EA",
                ["AccentBrush"] = "#5B4BDA",
                ["AccentSoftBrush"] = "#EAE7FF"
            }
            : new Dictionary<string, string>
            {
                ["WindowBackgroundBrush"] = "#10131A",
                ["SidebarBackgroundBrush"] = "#151922",
                ["SurfaceBrush"] = "#1B202B",
                ["CardBrush"] = "#202633",
                ["PrimaryTextBrush"] = "#F2F4F8",
                ["SecondaryTextBrush"] = "#A6AFBF",
                ["BorderBrush"] = "#303746",
                ["AccentBrush"] = "#8B7CFF",
                ["AccentSoftBrush"] = "#302B55"
            };

        foreach (var (key, color) in palette)
        {
            Application.Current.Resources[key] = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(color));
        }
    }
}
