namespace FolderPeek.App;

internal interface IFolderSnapshotSource
{
    DateTime GetDirectoryLastWriteTimeUtc(string folderPath);

    IReadOnlyList<FolderIndexItemData> ReadItems(string folderPath, CancellationToken cancellationToken);
}
