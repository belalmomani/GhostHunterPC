using System.Windows;
using GhostHunterPC.Services;
using GhostHunterPC.ViewModels;

namespace GhostHunterPC;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var localization = new UiLocalizationService();
        var theme = new ThemeService();
        var settings = new UserSettingsService();
        var viewModel = new MainViewModel(localization, theme, new DeviceInfoService(), settings);
        var mainWindow = new MainWindow
        {
            DataContext = viewModel
        };

        MainWindow = mainWindow;
        mainWindow.Show();
    }
}
