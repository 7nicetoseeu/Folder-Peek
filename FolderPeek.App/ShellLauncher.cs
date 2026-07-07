using System.Diagnostics;

namespace FolderPeek.App;

public sealed class ShellLauncher
{
    public bool TryOpen(string fullPath, out string? errorMessage)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fullPath)
            {
                UseShellExecute = true
            });

            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }
}
