namespace PyDisplay;

/// <summary>Non-fatal error log next to the settings file. Never throws.</summary>
internal static class Log
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, int> _counts = new();

    public static void Error(string where, Exception ex)
    {
        try
        {
            lock (_lock)
            {
                // a sensor failing every second must not grow the log forever
                int c = _counts.GetValueOrDefault(where) + 1;
                _counts[where] = c;
                if (c > 5) return;
                Directory.CreateDirectory(AppSettings.Dir);
                File.AppendAllText(Path.Combine(AppSettings.Dir, "error.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
