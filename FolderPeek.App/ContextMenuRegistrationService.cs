using Microsoft.Win32;
using System.IO;

namespace FolderPeek.App;

public sealed class ContextMenuRegistrationService
{
    private const string MenuKeyPath = @"Software\Classes\Directory\shell\FolderPeek";

    public bool TrySetEnabled(bool isEnabled, out string? errorMessage)
    {
        try
        {
            if (!isEnabled)
            {
                Registry.CurrentUser.DeleteSubKeyTree(MenuKeyPath, throwOnMissingSubKey: false);
                errorMessage = null;
                return true;
            }

            using var menuKey = Registry.CurrentUser.CreateSubKey(MenuKeyPath);
            if (menuKey is null)
            {
                errorMessage = "无法创建当前用户的右键菜单注册表项。";
                return false;
            }

            menuKey.SetValue("MUIVerb", "使用 Folder Peek 展开", RegistryValueKind.String);
            menuKey.SetValue("Icon", GetExecutablePath(), RegistryValueKind.String);
            using var commandKey = menuKey.CreateSubKey("command");
            commandKey?.SetValue(string.Empty, $"\"{GetExecutablePath()}\" --open-folder \"%1\"", RegistryValueKind.String);
            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static string GetExecutablePath()
    {
        return Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FolderPeek.App.exe");
    }
}
