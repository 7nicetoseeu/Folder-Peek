namespace FolderPeek.App.Tests;

public sealed class AppStoragePathsTests
{
    [Fact]
    public void OverrideEnvironmentVariable_IsUsedForAllRuntimeFiles()
    {
        using var environmentOverride = new EnvironmentVariableScope(
            AppStoragePaths.DataRootOverrideEnvironmentVariable,
            Path.Combine(Path.GetTempPath(), "FolderPeek.Tests", Guid.NewGuid().ToString("N")));

        var expectedRoot = Path.GetFullPath(environmentOverride.Value);

        Assert.Equal(expectedRoot, AppStoragePaths.GetDataRootPath());
        Assert.Equal(Path.Combine(expectedRoot, "FolderPeek.settings.json"), AppStoragePaths.GetSettingsFilePath());
        Assert.Equal(Path.Combine(expectedRoot, "FolderPeek.theme.json"), AppStoragePaths.GetThemeFilePath());
        Assert.Equal(Path.Combine(expectedRoot, "cache", "folder-index"), AppStoragePaths.GetFolderIndexCacheRootPath());
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previousValue;

        public EnvironmentVariableScope(string name, string value)
        {
            _name = name;
            _previousValue = Environment.GetEnvironmentVariable(name);
            Value = value;
            Environment.SetEnvironmentVariable(name, value);
        }

        public string Value { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previousValue);
        }
    }
}
