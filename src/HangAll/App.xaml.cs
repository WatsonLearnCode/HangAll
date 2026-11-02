using System.Windows;
using HangAll.Core;
using HangAll.UI;

namespace HangAll;

public partial class App : Application
{
    private AppController? _controller;
    public static EventWaitHandle? ToggleSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? lastError = null;
        var lastErrorAt = DateTime.MinValue;
        DispatcherUnhandledException += (_, ex) =>
        {
            // The same failure every tick would fill the log; one line a second is enough.
            var text = ex.Exception.ToString();
            if (text != lastError || (DateTime.Now - lastErrorAt).TotalSeconds > 1)
            {
                Log.Error($"Unhandled: {text}");
                lastError = text;
                lastErrorAt = DateTime.Now;
            }
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error($"Fatal: {ex.ExceptionObject}");

        try
        {
            _controller = new AppController();
        }
        catch (Exception ex)
        {
            // Without a tray icon there would be nothing to quit: say what happened and leave.
            Log.Error($"Could not start: {ex}");
            MessageBox.Show($"{Strings.StartupFailed}\n\n{ex.Message}\n\n{Log.Location}", Strings.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // A second launch asks the running one to show the line.
        if (ToggleSignal is not null)
        {
            var signal = ToggleSignal;
            var thread = new Thread(() =>
            {
                while (signal.WaitOne())
                    Dispatcher.BeginInvoke(() => _controller?.Toggle());
            }) { IsBackground = true, Name = "HangAll toggle" };
            thread.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        base.OnExit(e);
    }
}
