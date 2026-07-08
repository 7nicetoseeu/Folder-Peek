using System.Windows.Media;

namespace FolderPeek.App;

internal interface IFolderItemIconProvider
{
    ImageSource? GetFolderIcon();

    ImageSource? GetFileIcon(string fullPath);
}
