using System.Windows;
using Prompty.Services;

namespace Prompty;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Prompty", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        // After a self-update: wait for the previous instance to exit and remove the old exe.
        UpdateService.OnStartup(e.Args);
        base.OnStartup(e);
    }
}
