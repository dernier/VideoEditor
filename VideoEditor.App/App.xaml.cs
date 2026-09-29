using System.Globalization;
using System.Windows;
using System.Windows.Threading;

namespace VideoEditor.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        DispatcherUnhandledException += OnUnhandled;
        // Argomento opzionale: cartella o file del progetto da aprire.
        var window = new MainWindow(e.Args.FirstOrDefault());
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Video Editor — errore", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
