namespace FolderPeek.App;

internal sealed record FolderIndexItemData(
    string DisplayName,
    string FullPath,
    bool IsFolder,
    string SecondaryText);

internal sealed record FolderIndexSnapshot(
    string FolderPath,
    DateTime DirectoryLastWriteTimeUtc,
    IReadOnlyList<FolderIndexItemData> Items,
    DateTime IndexedAtUtc);

internal enum FolderIndexCacheReadStatus
{
    Hit,
    Missing,
    Stale,
    Invalid
}

internal sealed record FolderIndexCacheReadResult(
    FolderIndexCacheReadStatus Status,
    FolderIndexSnapshot? Snapshot,
    string? Detail = null);
