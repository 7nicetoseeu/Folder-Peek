namespace FolderPeek.App;

public partial class App : System.Windows.Application
{
    private PrototypeCoordinator? _coordinator;
    private AppThemeService? _themeService;
    private AppSettingsService? _settingsService;
    private SingleInstanceService? _singleInstanceService;

    private void OnStartup(object sender, System.Windows.StartupEventArgs e)
    {
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

        _singleInstanceService = new SingleInstanceService();
        if (!_singleInstanceService.TryAcquirePrimary())
        {
            _singleInstanceService.TryForwardOpenFolder(e.Args);
            Shutdown();
            return;
        }

        _themeService = new AppThemeService(this);
        _settingsService = new AppSettingsService();

        var mainWindow = new MainWindow(_themeService, _settingsService);
        MainWindow = mainWindow;

        _coordinator = new PrototypeCoordinator(mainWindow, _settingsService);
        _coordinator.Start();
        _singleInstanceService.OpenFolderRequested += OnOpenFolderRequested;
        _singleInstanceService.StartServer();

        var folderPath = SingleInstanceService.TryParseOpenFolderArgument(e.Args);
        if (folderPath is not null)
        {
            _coordinator.OpenFolderFromShell(folderPath);
        }
    }

    private void OnExit(object sender, System.Windows.ExitEventArgs e)
    {
        if (_singleInstanceService is not null)
        {
            _singleInstanceService.OpenFolderRequested -= OnOpenFolderRequested;
            _singleInstanceService.Dispose();
        }
        _coordinator?.Dispose();
        _themeService?.Dispose();
    }

    private void OnOpenFolderRequested(object? sender, string fullPath)
    {
        _ = Dispatcher.BeginInvoke(() => _coordinator?.OpenFolderFromShell(fullPath));
    }
}
