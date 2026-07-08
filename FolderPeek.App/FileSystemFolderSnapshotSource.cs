using System.IO;

namespace FolderPeek.App;

internal sealed class FileSystemFolderSnapshotSource : IFolderSnapshotSource
{
    public DateTime GetDirectoryLastWriteTimeUtc(string folderPath)
    {
        var directory = new DirectoryInfo(folderPath);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException($"Directory not found: {folderPath}");
        }

        return directory.LastWriteTimeUtc;
    }

    public IReadOnlyList<FolderIndexItemData> ReadItems(string folderPath, CancellationToken cancellationToken)
    {
        var directory = new DirectoryInfo(folderPath);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException($"Directory not found: {folderPath}");
        }

        var items = new List<FolderIndexItemData>();

        foreach (var childDirectory in directory.EnumerateDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldSkip(childDirectory.Attributes))
            {
                continue;
            }

            items.Add(new FolderIndexItemData(
                childDirectory.Name,
                childDirectory.FullName,
                true,
                "Folder"));
        }

        foreach (var file in directory.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldSkip(file.Attributes))
            {
                continue;
            }

            items.Add(new FolderIndexItemData(
                file.Name,
                file.FullName,
                false,
                ResolveFileKind(file.Extension)));
        }

        return items
            .OrderByDescending(item => item.IsFolder)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static bool ShouldSkip(FileAttributes attributes)
    {
        return attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System);
    }

    private static string ResolveFileKind(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return "File";
        }

        return $"{extension.ToUpperInvariant()} File";
    }
}
