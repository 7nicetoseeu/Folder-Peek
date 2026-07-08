using System.Collections.Concurrent;

namespace FolderPeek.App;

public sealed class FolderContentProvider
{
    private static readonly TimeSpan PrewarmCacheLifetime = TimeSpan.FromSeconds(12);

    private readonly IFolderItemIconProvider _iconProvider;
    private readonly FolderIndexCacheService _cacheService;
    private readonly IFolderSnapshotSource _snapshotSource;
    private readonly Action<string>? _log;
    private readonly Func<DateTime> _utcNow;
    private readonly ConcurrentDictionary<string, CachedFolderItems> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _prewarmTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<FolderPanelItem>>>> _inflightLoads = new(StringComparer.OrdinalIgnoreCase);

    public FolderContentProvider(ShellIconProvider iconProvider, Action<string>? log = null)
        : this(iconProvider, cacheService: null, snapshotSource: null, log: log, utcNow: null)
    {
    }

    internal FolderContentProvider(
        IFolderItemIconProvider iconProvider,
        FolderIndexCacheService? cacheService,
        IFolderSnapshotSource? snapshotSource,
        Action<string>? log,
        Func<DateTime>? utcNow)
    {
        _iconProvider = iconProvider;
        _cacheService = cacheService ?? new FolderIndexCacheService(log);
        _snapshotSource = snapshotSource ?? new FileSystemFolderSnapshotSource();
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<IReadOnlyList<FolderPanelItem>> GetItemsAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        var normalizedPath = FolderIndexCacheService.NormalizePath(folderPath);
        if (TryGetCachedItems(normalizedPath, out var cachedItems))
        {
            return cachedItems;
        }

        var loadTask = GetOrCreateLoadTask(normalizedPath);
        return await loadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Prewarm(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        var normalizedPath = FolderIndexCacheService.NormalizePath(folderPath);
        if (TryGetCachedItems(normalizedPath, out _))
        {
            return;
        }

        _ = _prewarmTasks.GetOrAdd(
            normalizedPath,
            static (path, state) => Task.Run(async () =>
            {
                var provider = state!;

                try
                {
                    await provider.GetOrCreateLoadTask(path).ConfigureAwait(false);
                }
                catch
                {
                }
                finally
                {
                    provider._prewarmTasks.TryRemove(path, out _);
                }
            }),
            this);
    }

    private Task<IReadOnlyList<FolderPanelItem>> GetOrCreateLoadTask(string normalizedPath)
    {
        var lazyTask = _inflightLoads.GetOrAdd(
            normalizedPath,
            path => new Lazy<Task<IReadOnlyList<FolderPanelItem>>>(
                () => LoadItemsAsync(path),
                LazyThreadSafetyMode.ExecutionAndPublication));

        var task = lazyTask.Value;
        _ = task.ContinueWith(
            (_, state) =>
            {
                var provider = (FolderContentProvider)state!;
                if (provider._inflightLoads.TryGetValue(normalizedPath, out var current) &&
                    ReferenceEquals(current, lazyTask))
                {
                    provider._inflightLoads.TryRemove(normalizedPath, out var _);
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return task;
    }

    private Task<IReadOnlyList<FolderPanelItem>> LoadItemsAsync(string normalizedPath)
    {
        return Task.Run(() =>
        {
            if (TryGetCachedItems(normalizedPath, out var memoryCachedItems))
            {
                return memoryCachedItems;
            }

            var directoryLastWriteTimeUtc = _snapshotSource.GetDirectoryLastWriteTimeUtc(normalizedPath);
            var cacheReadResult = _cacheService.TryReadSnapshot(normalizedPath, directoryLastWriteTimeUtc);

            switch (cacheReadResult.Status)
            {
                case FolderIndexCacheReadStatus.Hit when cacheReadResult.Snapshot is not null:
                    _log?.Invoke($"cache-hit: {normalizedPath}");
                    return CacheSnapshotItems(normalizedPath, cacheReadResult.Snapshot.Items);
                case FolderIndexCacheReadStatus.Missing:
                    _log?.Invoke($"cache-miss: {normalizedPath}");
                    break;
                case FolderIndexCacheReadStatus.Stale:
                    _log?.Invoke($"cache-stale: {normalizedPath}");
                    break;
                case FolderIndexCacheReadStatus.Invalid:
                    _log?.Invoke($"cache-read-failed: {normalizedPath} ({cacheReadResult.Detail})");
                    break;
            }

            var itemData = _snapshotSource.ReadItems(normalizedPath, CancellationToken.None);
            var refreshedLastWriteTimeUtc = _snapshotSource.GetDirectoryLastWriteTimeUtc(normalizedPath);
            var items = CacheSnapshotItems(normalizedPath, itemData);

            _log?.Invoke($"cache-rebuild: {normalizedPath}");
            _ = _cacheService.WriteSnapshotAsync(
                new FolderIndexSnapshot(normalizedPath, refreshedLastWriteTimeUtc, itemData, _utcNow()),
                CancellationToken.None);

            return items;
        });
    }

    private bool TryGetCachedItems(string folderPath, out IReadOnlyList<FolderPanelItem> items)
    {
        items = Array.Empty<FolderPanelItem>();

        if (!_cache.TryGetValue(folderPath, out var cached))
        {
            return false;
        }

        if ((DateTime.UtcNow - cached.CreatedAtUtc) > PrewarmCacheLifetime)
        {
            _cache.TryRemove(folderPath, out _);
            return false;
        }

        items = cached.Items;
        return true;
    }

    private IReadOnlyList<FolderPanelItem> CacheSnapshotItems(string folderPath, IReadOnlyList<FolderIndexItemData> itemData)
    {
        var items = itemData
            .Select(item => new FolderPanelItem(
                item.DisplayName,
                item.FullPath,
                item.IsFolder,
                item.IsFolder ? _iconProvider.GetFolderIcon() : _iconProvider.GetFileIcon(item.FullPath),
                item.SecondaryText))
            .ToArray();

        _cache[folderPath] = new CachedFolderItems(items, DateTime.UtcNow);
        return items;
    }

    private sealed record CachedFolderItems(IReadOnlyList<FolderPanelItem> Items, DateTime CreatedAtUtc);
}
