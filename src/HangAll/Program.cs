namespace HangAll;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {

        Interop.Native.SetAppUserModelId();

        if (args.Contains("--update-check"))
        {
            // Asks the release API once and writes the answer to the log: for
            // checking the update path without opening the settings window.
            Core.Updater.CheckAsync().GetAwaiter().GetResult();
            Core.Log.Notice($"Update check (cli): {Core.Updater.State} {Core.Updater.Found?.Version}");
            return Core.Updater.State switch
            {
                Core.Updater.Status.Failed => 1,
                Core.Updater.Status.Available => 2,
                _ => 0,
            };
        }

        if (args.Length == 3 && args[0] == "--ocr")
        {
            // Reads the text in an image into a file: for checking the OCR without a desktop.
            var text = Core.Ocr.ReadAsync(System.IO.Path.GetFullPath(args[1])).GetAwaiter().GetResult();
            System.IO.File.WriteAllText(args[2], text ?? "<ocr unavailable>");
            return text is null ? 1 : 0;
        }

        using var mutex = new Mutex(true, AppIdentity.MutexName, out bool first);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, AppIdentity.ToggleEventName);
        if (!first)
        {
            // Already running: bring the line down and leave.
            signal.Set();
            return 0;
        }
        App.ToggleSignal = signal;
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
