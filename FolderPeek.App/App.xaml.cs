namespace FolderPeek.App;

public partial class App : System.Windows.Application
{
    private PrototypeCoordinator? _coordinator;
    private AppThemeService? _themeService;
    private AppSettingsService? _settingsService;

    private void OnStartup(object sender, System.Windows.StartupEventArgs e)
    {
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

        _themeService = new AppThemeService(this);
        _settingsService = new AppSettingsService();

        var mainWindow = new MainWindow(_themeService, _settingsService);
        MainWindow = mainWindow;

        _coordinator = new PrototypeCoordinator(mainWindow, _settingsService);
        _coordinator.Start();
    }

    private void OnExit(object sender, System.Windows.ExitEventArgs e)
    {
        _coordinator?.Dispose();
        _themeService?.Dispose();
    }
}
