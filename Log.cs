namespace BtAudioGuard;

/// <summary>Thread-safe logger. Writes to %AppData%\BtAudioGuard\log.txt and raises <see cref="Line"/> for the UI.</summary>
public static class Log
{
    private const long MaxBytes = 5_000_000;
    private static readonly object Gate = new();

    public static readonly string FilePath = Path.Combine(Settings.Dir, "log.txt");

    public static event Action<string>? Line;

    public static void Info(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
        try
        {
            Line?.Invoke(line);
        }
        catch
        {
            // A failing listener (UI closing) must not break the caller.
        }
    }

    public static IEnumerable<string> Tail(int lines)
    {
        lock (Gate)
        {
            try
            {
                return File.Exists(FilePath) ? File.ReadLines(FilePath).TakeLast(lines).ToList() : [];
            }
            catch
            {
                return [];
            }
        }
    }
}
