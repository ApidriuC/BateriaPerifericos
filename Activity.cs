namespace BateriaPerifericos;

/// <summary>Registro corto de actividad en %LOCALAPPDATA%\BateriaPerifericos\actividad.log (últimas ~300 líneas).</summary>
internal static class Activity
{
    private static readonly string File = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BateriaPerifericos", "actividad.log");
    private static readonly object Gate = new();

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File)!);
                var lines = System.IO.File.Exists(File) ? System.IO.File.ReadAllLines(File).ToList() : new List<string>();
                lines.Add($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
                if (lines.Count > 300) lines.RemoveRange(0, lines.Count - 300);
                System.IO.File.WriteAllLines(File, lines);
            }
        }
        catch { }
    }
}
