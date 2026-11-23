namespace BateriaPerifericos;

static class Program
{
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BateriaPerifericos", "errores.log");

    [STAThread]
    static void Main()
    {
        // Evita dos instancias duplicadas en la barra.
        using var mutex = new Mutex(true, @"Local\BateriaPerifericos", out bool isNew);
        if (!isNew) return;

        // Cualquier error inesperado queda en %LOCALAPPDATA%\BateriaPerifericos\errores.log
        // y la app sigue funcionando en lugar de cerrarse.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception); e.SetObserved(); };

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext(new IBatteryProvider[]
        {
            new FractalScapeProvider(),
            new ScyroxProvider(),
        }));
    }

    private static void Log(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }
}
