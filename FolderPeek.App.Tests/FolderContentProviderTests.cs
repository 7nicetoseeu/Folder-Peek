using System.Collections.Concurrent;

namespace FolderPeek.App.Tests;

public sealed class FolderContentProviderTests
{
    [Fact]
    public async Task FirstRead_WritesPersistentCache_AndSecondProviderHitsLocalCache()
    {
        using var workspace = new TestWorkspace();
        var folderPath = workspace.CreateFolder("desktop");
        var source = new FakeSnapshotSource();
        source.Set(folderPath, new DateTime(2026, 7, 8, 4, 0, 0, DateTimeKind.Utc),
            new FolderIndexItemData("alpha.txt", Path.Combine(folderPath, "alpha.txt"), false, ".TXT File"));

        var firstProvider = CreateProvider(workspace.CacheRootPath, source);
        var firstItems = await firstProvider.GetItemsAsync(folderPath);
        Assert.Single(firstItems);
        Assert.Equal(1, source.ReadItemsCallCount);

        var cacheService = new FolderIndexCacheService(workspace.CacheRootPath, scheduleStartupCleanup: false);
        await WaitForFileAsync(cacheService.GetCacheFilePathForFolder(folderPath));

        var secondProvider = CreateProvider(workspace.CacheRootPath, source);
        var secondItems = await secondProvider.GetItemsAsync(folderPath);

        Assert.Single(secondItems);
        Assert.Equal(1, source.ReadItemsCallCount);
    }

    [Fact]
    public async Task StaleCache_RebuildsFromSource()
    {
        using var workspace = new TestWorkspace();
        var folderPath = workspace.CreateFolder("desktop");
        var source = new FakeSnapshotSource();
        source.Set(folderPath, new DateTime(2026, 7, 8, 4, 0, 0, DateTimeKind.Utc),
            new FolderIndexItemData("alpha.txt", Path.Combine(folderPath, "alpha.txt"), false, ".TXT File"));

        var provider = CreateProvider(workspace.CacheRootPath, source);
        await provider.GetItemsAsync(folderPath);
        Assert.Equal(1, source.ReadItemsCallCount);

        var cacheService = new FolderIndexCacheService(workspace.CacheRootPath, scheduleStartupCleanup: false);
        await WaitForFileAsync(cacheService.GetCacheFilePathForFolder(folderPath));

        source.Set(folderPath, new DateTime(2026, 7, 8, 4, 5, 0, DateTimeKind.Utc),
            new FolderIndexItemData("beta.txt", Path.Combine(folderPath, "beta.txt"), false, ".TXT File"));

        var secondProvider = CreateProvider(workspace.CacheRootPath, source);
        var items = await secondProvider.GetItemsAsync(folderPath);

        Assert.Equal(2, source.ReadItemsCallCount);
        Assert.Equal("beta.txt", Assert.Single(items).DisplayName);
    }

    [Fact]
    public async Task InvalidCacheFile_FallsBackToSource_AndRewritesCache()
    {
        using var workspace = new TestWorkspace();
        var folderPath = workspace.CreateFolder("desktop");
        var source = new FakeSnapshotSource();
        source.Set(folderPath, new DateTime(2026, 7, 8, 4, 0, 0, DateTimeKind.Utc),
            new FolderIndexItemData("alpha.txt", Path.Combine(folderPath, "alpha.txt"), false, ".TXT File"));

        var cacheService = new FolderIndexCacheService(workspace.CacheRootPath, scheduleStartupCleanup: false);
        var cacheFilePath = cacheService.GetCacheFilePathForFolder(folderPath);
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);
        await File.WriteAllTextAsync(cacheFilePath, "{ not-valid-json");

        var provider = CreateProvider(workspace.CacheRootPath, source);
        var items = await provider.GetItemsAsync(folderPath);

        Assert.Single(items);
        Assert.Equal(1, source.ReadItemsCallCount);
        await WaitForConditionAsync(async () =>
        {
            var content = await File.ReadAllTextAsync(cacheFilePath);
            return content.Contains("\"SchemaVersion\":1", StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task MissingDirectory_DoesNotUseStaleCache()
    {
        using var workspace = new TestWorkspace();
        var folderPath = workspace.CreateFolder("desktop");

        File.WriteAllText(Path.Combine(folderPath, "alpha.txt"), "alpha");

        var cacheService = new FolderIndexCacheService(workspace.CacheRootPath, scheduleStartupCleanup: false);
        var provider = new FolderContentProvider(
            new StubIconProvider(),
            cacheService,
            new FileSystemFolderSnapshotSource(),
            log: null,
            utcNow: null);

        await provider.GetItemsAsync(folderPath);
        await WaitForFileAsync(cacheService.GetCacheFilePathForFolder(folderPath));

        Directory.Delete(folderPath, recursive: true);

        var secondProvider = new FolderContentProvider(
            new StubIconProvider(),
            cacheService,
            new FileSystemFolderSnapshotSource(),
            log: null,
            utcNow: null);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => secondProvider.GetItemsAsync(folderPath));
    }

    [Fact]
    public async Task Prewarm_UsesSameLoadPipeline()
    {
        using var workspace = new TestWorkspace();
        var folderPath = workspace.CreateFolder("desktop");
        var source = new FakeSnapshotSource();
        source.Set(folderPath, new DateTime(2026, 7, 8, 4, 0, 0, DateTimeKind.Utc),
            new FolderIndexItemData("alpha.txt", Path.Combine(folderPath, "alpha.txt"), false, ".TXT File"));

        var provider = CreateProvider(workspace.CacheRootPath, source);
        provider.Prewarm(folderPath);

        await WaitForConditionAsync(() => source.ReadItemsCallCount == 1);
        var items = await provider.GetItemsAsync(folderPath);

        Assert.Single(items);
        Assert.Equal(1, source.ReadItemsCallCount);
    }

    [Fact]
    public async Task ConcurrentRequests_ShareSingleSourceRead()
    {
        using var workspace = new TestWorkspace();
        var folderPath = workspace.CreateFolder("desktop");
        var source = new FakeSnapshotSource
        {
            ReadDelay = TimeSpan.FromMilliseconds(100)
        };
        source.Set(folderPath, new DateTime(2026, 7, 8, 4, 0, 0, DateTimeKind.Utc),
            new FolderIndexItemData("alpha.txt", Path.Combine(folderPath, "alpha.txt"), false, ".TXT File"));

        var provider = CreateProvider(workspace.CacheRootPath, source);
        var tasks = Enumerable.Range(0, 6)
            .Select(_ => provider.GetItemsAsync(folderPath))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, source.ReadItemsCallCount);
        Assert.All(tasks, task => Assert.Single(task.Result));
    }

    private static FolderContentProvider CreateProvider(string cacheRootPath, FakeSnapshotSource source)
    {
        return new FolderContentProvider(
            new StubIconProvider(),
            new FolderIndexCacheService(cacheRootPath, scheduleStartupCleanup: false),
            source,
            log: null,
            utcNow: null);
    }

    private static Task WaitForFileAsync(string filePath)
    {
        return WaitForConditionAsync(() => File.Exists(filePath));
    }

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Condition was not met in time.");
    }

    private static async Task WaitForConditionAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Condition was not met in time.");
    }

    private sealed class StubIconProvider : IFolderItemIconProvider
    {
        public System.Windows.Media.ImageSource? GetFolderIcon()
        {
            return null;
        }

        public System.Windows.Media.ImageSource? GetFileIcon(string fullPath)
        {
            return null;
        }
    }

    private sealed class FakeSnapshotSource : IFolderSnapshotSource
    {
        private readonly ConcurrentDictionary<string, SnapshotState> _states = new(StringComparer.OrdinalIgnoreCase);

        public int ReadItemsCallCount;

        public TimeSpan ReadDelay { get; init; }

        public void Set(string folderPath, DateTime lastWriteTimeUtc, params FolderIndexItemData[] items)
        {
            _states[FolderIndexCacheService.NormalizePath(folderPath)] = new SnapshotState(lastWriteTimeUtc, items);
        }

        public DateTime GetDirectoryLastWriteTimeUtc(string folderPath)
        {
            if (_states.TryGetValue(FolderIndexCacheService.NormalizePath(folderPath), out var state))
            {
                return state.LastWriteTimeUtc;
            }

            throw new DirectoryNotFoundException(folderPath);
        }

        public IReadOnlyList<FolderIndexItemData> ReadItems(string folderPath, CancellationToken cancellationToken)
        {
            if (!_states.TryGetValue(FolderIndexCacheService.NormalizePath(folderPath), out var state))
            {
                throw new DirectoryNotFoundException(folderPath);
            }

            Interlocked.Increment(ref ReadItemsCallCount);
            if (ReadDelay > TimeSpan.Zero)
            {
                Task.Delay(ReadDelay, cancellationToken).GetAwaiter().GetResult();
            }

            return state.Items;
        }

        private sealed record SnapshotState(DateTime LastWriteTimeUtc, IReadOnlyList<FolderIndexItemData> Items);
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "FolderPeek.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            CacheRootPath = Path.Combine(RootPath, "cache");
        }

        public string RootPath { get; }

        public string CacheRootPath { get; }

        public string CreateFolder(string name)
        {
            var folderPath = Path.Combine(RootPath, name);
            Directory.CreateDirectory(folderPath);
            return folderPath;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
