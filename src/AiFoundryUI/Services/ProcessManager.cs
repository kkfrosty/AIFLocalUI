using System;
using System.Diagnostics;
using AiFoundryUI.Models;

namespace AiFoundryUI.Services;

public class ProcessManager
{
    private Process? _proc;
    private readonly Action<string> _log;
    private readonly Action<string> _onOutput;

    public ProcessManager(Action<string> log, Action<string>? onOutput = null) 
    {
        _log = log;
        _onOutput = onOutput ?? (_ => { });
    }

    public bool IsRunning => _proc != null && !_proc.HasExited;

    public void Start(Config cfg, string selectedModel)
    {
        if (IsRunning)
        {
            _log("[info] Service already running.");
            return;
        }
        if (string.IsNullOrWhiteSpace(cfg.StartCommandTemplate))
        {
            _log("[error] start_command_template is empty.");
            return;
        }
        if (string.IsNullOrWhiteSpace(selectedModel))
        {
            _log("[error] No model selected.");
            return;
        }

        var command = cfg.StartCommandTemplate.Replace("{model}", selectedModel);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        if (!string.IsNullOrWhiteSpace(cfg.WorkingDir))
            psi.WorkingDirectory = cfg.WorkingDir;

        foreach (var kv in cfg.Environment)
            psi.Environment[kv.Key] = kv.Value;

        try
        {
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (s, e) => { 
                if (e.Data != null) 
                {
                    _log("[out] " + e.Data);
                    _onOutput(e.Data);
                }
            };
            _proc.ErrorDataReceived += (s, e) => { 
                if (e.Data != null) 
                {
                    _log("[err] " + e.Data);
                    _onOutput(e.Data);
                }
            };
            _proc.Exited += (s, e) => _log("[info] Process exited.");
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
            _log("[info] Start command launched.");
        }
        catch (Exception ex)
        {
            _log("[error] Failed to start: " + ex.Message);
        }
    }

    public void Stop(Config cfg)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(cfg.StopCommand))
            {
                var stop = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false
                };
                stop.ArgumentList.Add("-NoProfile");
                stop.ArgumentList.Add("-ExecutionPolicy");
                stop.ArgumentList.Add("Bypass");
                stop.ArgumentList.Add("-Command");
                stop.ArgumentList.Add(cfg.StopCommand);
                Process.Start(stop)?.WaitForExit(5000);
                _log("[info] stop_command executed.");
            }
        }
        catch (Exception ex)
        {
            _log("[warn] stop_command failed: " + ex.Message);
        }

        try
        {
            if (_proc != null && !_proc.HasExited)
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(3000);
                _log("[info] Process terminated.");
            }
        }
        catch (Exception ex)
        {
            _log("[warn] Failed to stop process: " + ex.Message);
        }
        finally
        {
            _proc?.Dispose();
            _proc = null;
        }
    }
}
