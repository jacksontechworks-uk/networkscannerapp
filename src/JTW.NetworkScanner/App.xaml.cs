using System.Windows;
using System.Windows.Threading;
using JTW.NetworkScanner.Core;

namespace JTW.NetworkScanner;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Unobserved task exception", args.Exception); args.SetObserved(); };

        // Remove leftovers from a previous self-update (files renamed to *.old while in use).
        Updater.CleanupOldFiles();

        AppSettings.Load();

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppSettings.Save();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message + "\n\nDetails were written to the log file.",
            AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
