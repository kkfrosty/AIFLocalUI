using System.Configuration;
using System.Data;
using System.Windows;
using System.IO;
using System.Runtime.InteropServices;

namespace AiFoundryUI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FreeConsole();

    protected override void OnStartup(StartupEventArgs e)
    {
        // Allocate a console window for debug output
        AllocConsole();
        Console.WriteLine("=== AI Foundry UI Debug Console ===");
        Console.WriteLine($"Started at: {DateTime.Now}");
        Console.WriteLine("This console will show debug information about foundry commands and responses.");
        Console.WriteLine();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Console.WriteLine("=== Shutting down ===");
        FreeConsole();
        base.OnExit(e);
    }
}

