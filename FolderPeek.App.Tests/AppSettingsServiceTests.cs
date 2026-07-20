namespace FolderPeek.App.Tests;

public sealed class AppSettingsServiceTests
{
    [Fact]
    public void NewSettings_KeepLegacySpaceGesture()
    {
        using var storage = new TemporaryStorageScope();

        var settings = new AppSettingsService();

        Assert.Null(settings.ExpandMode);
    }

    [Fact]
    public void ExpandMode_IsPersisted()
    {
        using var storage = new TemporaryStorageScope();
        var settings = new AppSettingsService();

        settings.SetExpandMode(FolderExpandMode.LongPressRight);

        var reloadedSettings = new AppSettingsService();
        Assert.Equal(FolderExpandMode.LongPressRight, reloadedSettings.ExpandMode);
    }

    [Fact]
    public void UseDefaultExpandMode_ClearsPersistedTestGesture()
    {
        using var storage = new TemporaryStorageScope();
        var settings = new AppSettingsService();
        settings.SetExpandMode(FolderExpandMode.MiddleDrag);

        settings.UseDefaultExpandMode();

        var reloadedSettings = new AppSettingsService();
        Assert.Null(reloadedSettings.ExpandMode);
    }

    private sealed class TemporaryStorageScope : IDisposable
    {
        private readonly string? _previousValue;

        public TemporaryStorageScope()
        {
            _previousValue = Environment.GetEnvironmentVariable(AppStoragePaths.DataRootOverrideEnvironmentVariable);
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FolderPeek.Tests", Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(AppStoragePaths.DataRootOverrideEnvironmentVariable, Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(AppStoragePaths.DataRootOverrideEnvironmentVariable, _previousValue);
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
