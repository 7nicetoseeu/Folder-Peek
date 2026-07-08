using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FolderPeek.App;

internal sealed class FolderIndexCacheService
{
    private const int SchemaVersion = 1;
    private const int CleanupWriteThreshold = 20;
    private static readonly TimeSpan CleanupRetention = TimeSpan.FromDays(30);

    private readonly string _cacheRootPath;
    private readonly Action<string>? _log;
    private readonly Func<DateTime> _utcNow;
    private int _writesSinceCleanup;
    private int _cleanupScheduled;

    public FolderIndexCacheService(Action<string>? log = null)
        : this(cacheRootPath: null, log: log, utcNow: null, scheduleStartupCleanup: true)
    {
    }

    internal FolderIndexCacheService(
        string? cacheRootPath,
        Action<string>? log = null,
        Func<DateTime>? utcNow = null,
        bool scheduleStartupCleanup = true)
    {
        _cacheRootPath = cacheRootPath ?? AppStoragePaths.GetFolderIndexCacheRootPath();
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);

        if (scheduleStartupCleanup)
        {
            ScheduleCleanup();
        }
    }

    public FolderIndexCacheReadResult TryReadSnapshot(string folderPath, DateTime expectedDirectoryLastWriteTimeUtc)
    {
        var normalizedPath = NormalizePath(folderPath);
        var cacheFilePath = GetCacheFilePath(normalizedPath);
        if (!File.Exists(cacheFilePath))
        {
            return new FolderIndexCacheReadResult(FolderIndexCacheReadStatus.Missing, null);
        }

        try
        {
            var json = File.ReadAllText(cacheFilePath);
            var cacheFile = JsonSerializer.Deserialize<FolderIndexCacheFile>(json);
            if (cacheFile is null)
            {
                return new FolderIndexCacheReadResult(FolderIndexCacheReadStatus.Invalid, null, "empty-cache-file");
            }

            if (cacheFile.SchemaVersion != SchemaVersion)
            {
                return new FolderIndexCacheReadResult(FolderIndexCacheReadStatus.Invalid, null, "schema-version-mismatch");
            }

            if (string.IsNullOrWhiteSpace(cacheFile.FolderPath))
            {
                return new FolderIndexCacheReadResult(FolderIndexCacheReadStatus.Invalid, null, "missing-folder-path");
            }

            var normalizedCachedPath = NormalizePath(cacheFile.FolderPath);
            if (!string.Equals(normalizedPath, normalizedCachedPath, StringComparison.OrdinalIgnoreCase))
            {
                return new FolderIndexCacheReadResult(FolderIndexCacheReadStatus.Invalid, null, "folder-path-mismatch");
            }

            if (cacheFile.Items is null)
            {
                return new FolderIndexCacheReadResult(FolderIndexCacheReadStatus.Invalid, null, "missing-items");
            }

            if (cacheFile.DirectoryLastWriteTimeUtc != expectedDirectoryLastWriteTimeUtc)
            {
                return new FolderIndexCacheReadResult(FolderIndexCacheReadStatus.Stale, null);
            }

            var snapshot = new FolderIndexSnapshot(
                normalizedCachedPath,
                cacheFile.DirectoryLastWriteTimeUtc,
                cacheFile.Items
                    .Select(item => new FolderIndexItemData(
                        item.DisplayName ?? string.Empty,
                        item.FullPath ?? string.Empty,
                        item.IsFolder,
                        item.SecondaryText ?? string.Empty))
                    .ToArray(),
                cacheFile.IndexedAtUtc);

            TryTouchLastAccess(cacheFilePath);
            return new FolderIndexCacheReadResult(FolderIndexCacheReadStatus.Hit, snapshot);
        }
        catch (Exception ex)
        {
            return new FolderIndexCacheReadResult(FolderIndexCacheReadStatus.Invalid, null, ex.Message);
        }
    }

    public async Task WriteSnapshotAsync(FolderIndexSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(snapshot.FolderPath);
        var cacheFilePath = GetCacheFilePath(normalizedPath);
        var tempFilePath = $"{cacheFilePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            Directory.CreateDirectory(_cacheRootPath);

            var cacheFile = new FolderIndexCacheFile
            {
                SchemaVersion = SchemaVersion,
                FolderPath = normalizedPath,
                DirectoryLastWriteTimeUtc = snapshot.DirectoryLastWriteTimeUtc,
                IndexedAtUtc = snapshot.IndexedAtUtc,
                Items = snapshot.Items
                    .Select(item => new FolderIndexCacheItemFile
                    {
                        DisplayName = item.DisplayName,
                        FullPath = item.FullPath,
                        IsFolder = item.IsFolder,
                        SecondaryText = item.SecondaryText
                    })
                    .ToArray()
            };

            var json = JsonSerializer.Serialize(cacheFile, new JsonSerializerOptions { WriteIndented = false });
            await File.WriteAllTextAsync(tempFilePath, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempFilePath, cacheFilePath, overwrite: true);
            TryTouchLastAccess(cacheFilePath);

            if (Interlocked.Increment(ref _writesSinceCleanup) >= CleanupWriteThreshold)
            {
                Interlocked.Exchange(ref _writesSinceCleanup, 0);
                ScheduleCleanup();
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"cache-write-failed: {normalizedPath} ({ex.Message})");
            TryDeleteFile(tempFilePath);
        }
    }

    internal Task CleanupStaleEntriesAsync()
    {
        return Task.Run(() =>
        {
            if (!Directory.Exists(_cacheRootPath))
            {
                return;
            }

            var now = _utcNow();
            foreach (var cacheFilePath in Directory.EnumerateFiles(_cacheRootPath, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if ((now - File.GetLastAccessTimeUtc(cacheFilePath)) > CleanupRetention)
                    {
                        File.Delete(cacheFilePath);
                        continue;
                    }

                    var json = File.ReadAllText(cacheFilePath);
                    var cacheFile = JsonSerializer.Deserialize<FolderIndexCacheFile>(json);
                    if (cacheFile is null || string.IsNullOrWhiteSpace(cacheFile.FolderPath))
                    {
                        File.Delete(cacheFilePath);
                        continue;
                    }

                    var normalizedCachedPath = NormalizePath(cacheFile.FolderPath);
                    if (!Directory.Exists(normalizedCachedPath))
                    {
                        File.Delete(cacheFilePath);
                    }
                }
                catch
                {
                    TryDeleteFile(cacheFilePath);
                }
            }
        });
    }

    internal string GetCacheFilePathForFolder(string folderPath)
    {
        return GetCacheFilePath(NormalizePath(folderPath));
    }

    internal static string NormalizePath(string folderPath)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
    }

    private void ScheduleCleanup()
    {
        if (Interlocked.Exchange(ref _cleanupScheduled, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await CleanupStaleEntriesAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _cleanupScheduled, 0);
            }
        });
    }

    private string GetCacheFilePath(string normalizedPath)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalizedPath.ToUpperInvariant()));
        return Path.Combine(_cacheRootPath, $"{Convert.ToHexString(hash)}.json");
    }

    private void TryTouchLastAccess(string cacheFilePath)
    {
        try
        {
            File.SetLastAccessTimeUtc(cacheFilePath, _utcNow());
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
        }
    }

    private sealed class FolderIndexCacheFile
    {
        public int SchemaVersion { get; set; }

        public string? FolderPath { get; set; }

        public DateTime DirectoryLastWriteTimeUtc { get; set; }

        public DateTime IndexedAtUtc { get; set; }

        public FolderIndexCacheItemFile[]? Items { get; set; }
    }

    private sealed class FolderIndexCacheItemFile
    {
        public string? DisplayName { get; set; }

        public string? FullPath { get; set; }

        public bool IsFolder { get; set; }

        public string? SecondaryText { get; set; }
    }
}
