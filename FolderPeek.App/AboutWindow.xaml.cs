using System.Windows;

namespace FolderPeek.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        AppIconAssets.ApplyWindowIcon(this);
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
