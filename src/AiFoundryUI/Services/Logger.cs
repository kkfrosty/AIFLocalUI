using System.Diagnostics;

namespace AiFoundryUI.Services;

public static class Logger
{
    public static bool IsVerbose { get; set; } = true;

    public static void Log(string message)
    {
        if (!IsVerbose) return;
        Debug.WriteLine(message);
    }

    public static void Error(string message, Exception? ex = null)
    {
        Debug.WriteLine($"ERROR: {message} {(ex != null ? ex.Message : string.Empty)}");
        if (ex != null)
        {
            Debug.WriteLine(ex.StackTrace);
        }
    }
}
