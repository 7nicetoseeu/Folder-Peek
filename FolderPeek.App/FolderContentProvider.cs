using System.IO;
using System.Collections.Concurrent;

namespace FolderPeek.App;

public sealed class FolderContentProvider
{
    private static readonly TimeSpan PrewarmCacheLifetime = TimeSpan.FromSeconds(12);

    private readonly ShellIconProvider _iconProvider;
    private readonly ConcurrentDictionary<string, CachedFolderItems> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _prewarmTasks = new(StringComparer.OrdinalIgnoreCase);

    public FolderContentProvider(ShellIconProvider iconProvider)
    {
        _iconProvider = iconProvider;
    }

    public Task<IReadOnlyList<FolderPanelItem>> GetItemsAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        if (TryGetCachedItems(folderPath, out var cachedItems))
        {
            return Task.FromResult(cachedItems);
        }

        return Task.Run(() => GetItems(folderPath, cancellationToken), cancellationToken);
    }

    public void Prewarm(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || TryGetCachedItems(folderPath, out _))
        {
            return;
        }

        _ = _prewarmTasks.GetOrAdd(
            folderPath,
            static (path, state) => Task.Run(() =>
            {
                var provider = state!;

                try
                {
                    var items = provider.GetItems(path, CancellationToken.None);
                    provider._cache[path] = new CachedFolderItems(items, DateTime.UtcNow);
                }
                catch
                {
                    // 预热失败不影响正常展开，真正展开时再走正式读取链路。
                }
                finally
                {
                    provider._prewarmTasks.TryRemove(path, out _);
                }
            }),
            this);
    }

    private IReadOnlyList<FolderPanelItem> GetItems(string folderPath, CancellationToken cancellationToken)
    {
        var directory = new DirectoryInfo(folderPath);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException($"找不到文件夹：{folderPath}");
        }

        var items = new List<FolderPanelItem>();

        foreach (var childDirectory in directory.EnumerateDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldSkip(childDirectory.Attributes))
            {
                continue;
            }

            items.Add(new FolderPanelItem(
                childDirectory.Name,
                childDirectory.FullName,
                true,
                _iconProvider.GetFolderIcon(),
                "文件夹"));
        }

        foreach (var file in directory.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldSkip(file.Attributes))
            {
                continue;
            }

            items.Add(new FolderPanelItem(
                file.Name,
                file.FullName,
                false,
                _iconProvider.GetFileIcon(file.FullName),
                ResolveFileKind(file.Extension)));
        }

        var result = items
            .OrderByDescending(item => item.IsFolder)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        _cache[folderPath] = new CachedFolderItems(result, DateTime.UtcNow);
        return result;
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

    private static bool ShouldSkip(FileAttributes attributes)
    {
        return attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System);
    }

    private static string ResolveFileKind(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return "文件";
        }

        return $"{extension.ToUpperInvariant()} 文件";
    }

    private sealed record CachedFolderItems(IReadOnlyList<FolderPanelItem> Items, DateTime CreatedAtUtc);
}
