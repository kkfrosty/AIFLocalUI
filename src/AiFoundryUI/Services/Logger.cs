using System.Diagnostics;

namespace AiFoundryUI.Services;

public static class Logger
{
    public static bool IsVerbose { get; set; } = true;
    public static bool MirrorToStdOut { get; set; } = true; // ensure VS Code Debug Console sees output

    private static readonly object _lock = new();

    public static void Log(string message)
    {
        if (!IsVerbose) return;
        lock (_lock)
        {
            Debug.WriteLine(message);
            if (MirrorToStdOut)
            {
                try { Console.WriteLine(message); } catch { /* ignored */ }
            }
        }
    }

    public static void Error(string message, Exception? ex = null)
    {
        lock (_lock)
        {
            var full = $"ERROR: {message} {(ex != null ? ex.Message : string.Empty)}";
            Debug.WriteLine(full);
            if (MirrorToStdOut)
            {
                try { Console.WriteLine(full); } catch { }
            }
            if (ex != null)
            {
                Debug.WriteLine(ex.StackTrace);
                if (MirrorToStdOut)
                {
                    try { Console.WriteLine(ex.StackTrace); } catch { }
                }
            }
        }
    }
}
