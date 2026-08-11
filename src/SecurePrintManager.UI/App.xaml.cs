using System.Windows;
using SecurePrintManager.Core;

namespace SecurePrintManager.UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var loginWindow = new LoginWindow();
        var result = loginWindow.ShowDialog();

        if (result == true && loginWindow.AuthenticatedUser != null)
        {
            var mainWindow = new MainWindow(loginWindow.AuthenticatedUser);
            mainWindow.Show();
        }
        else
        {
            Shutdown();
        }
    }
}
