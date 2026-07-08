namespace FolderPeek.App.Tests;

public sealed class FolderIndexCacheServiceTests
{
    [Fact]
    public async Task Cleanup_RemovesEntriesForMissingDirectories()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "FolderPeek.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var cacheRootPath = Path.Combine(rootPath, "cache");
        var missingFolderPath = Path.Combine(rootPath, "missing-folder");
        var cacheService = new FolderIndexCacheService(cacheRootPath, scheduleStartupCleanup: false);
        var snapshot = new FolderIndexSnapshot(
            missingFolderPath,
            new DateTime(2026, 7, 8, 4, 0, 0, DateTimeKind.Utc),
            new[]
            {
                new FolderIndexItemData("alpha.txt", Path.Combine(missingFolderPath, "alpha.txt"), false, ".TXT File")
            },
            new DateTime(2026, 7, 8, 4, 0, 0, DateTimeKind.Utc));

        try
        {
            await cacheService.WriteSnapshotAsync(snapshot);
            var cacheFilePath = cacheService.GetCacheFilePathForFolder(missingFolderPath);
            Assert.True(File.Exists(cacheFilePath));

            await cacheService.CleanupStaleEntriesAsync();

            Assert.False(File.Exists(cacheFilePath));
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
