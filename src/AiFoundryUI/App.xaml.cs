using System.Configuration;
using System.Data;
using System.Windows;
using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace AiFoundryUI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
    // No external console; use Debug output only for faster startup
    Debug.WriteLine("[AI Foundry UI] Startup " + DateTime.Now);

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
    Debug.WriteLine("[AI Foundry UI] Exit " + DateTime.Now);
        base.OnExit(e);
    }
}

