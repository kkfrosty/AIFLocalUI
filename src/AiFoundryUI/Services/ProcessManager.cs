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

    private void DebugLog(string message)
    {
        var logMessage = $"[ProcessManager] {message}";
        Console.WriteLine(logMessage);
        _log(logMessage);
    }

    public void Start(Config cfg, string selectedModel)
    {
        DebugLog($"Start called with model: '{selectedModel}'");
        
        if (IsRunning)
        {
            DebugLog("Service already running - returning early");
            _log("[info] Service already running.");
            return;
        }
        
        if (string.IsNullOrWhiteSpace(cfg.StartCommandTemplate))
        {
            DebugLog("ERROR: start_command_template is empty!");
            _log("[error] start_command_template is empty.");
            return;
        }
        
        if (string.IsNullOrWhiteSpace(selectedModel))
        {
            DebugLog("ERROR: No model selected!");
            _log("[error] No model selected.");
            return;
        }

        var command = cfg.StartCommandTemplate.Replace("{model}", selectedModel);
        DebugLog($"Command template: '{cfg.StartCommandTemplate}'");
        DebugLog($"Final command: '{command}'");

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
        {
            psi.WorkingDirectory = cfg.WorkingDir;
            DebugLog($"Working directory set to: '{cfg.WorkingDir}'");
        }
        else
        {
            DebugLog("No working directory specified");
        }

        foreach (var kv in cfg.Environment)
        {
            psi.Environment[kv.Key] = kv.Value;
            DebugLog($"Environment variable: {kv.Key}={kv.Value}");
        }

        DebugLog($"About to start process:");
        DebugLog($"  FileName: {psi.FileName}");
        DebugLog($"  Arguments: {string.Join(" ", psi.ArgumentList)}");
        DebugLog($"  WorkingDirectory: {psi.WorkingDirectory ?? "(not set)"}");

        try
        {
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (s, e) => { 
                if (e.Data != null) 
                {
                    var outputMsg = "[STDOUT] " + e.Data;
                    Console.WriteLine(outputMsg);
                    _log("[out] " + e.Data);
                    _onOutput(e.Data);
                }
            };
            _proc.ErrorDataReceived += (s, e) => { 
                if (e.Data != null) 
                {
                    var errorMsg = "[STDERR] " + e.Data;
                    Console.WriteLine(errorMsg);
                    _log("[err] " + e.Data);
                    _onOutput(e.Data);
                }
            };
            _proc.Exited += (s, e) => {
                var exitMsg = $"[EXIT] Process exited with code: {_proc?.ExitCode}";
                Console.WriteLine(exitMsg);
                _log("[info] Process exited.");
            };
            
            DebugLog("Starting process...");
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
            DebugLog($"Process started successfully. PID: {_proc.Id}");
            _log("[info] Start command launched.");
        }
        catch (Exception ex)
        {
            DebugLog($"EXCEPTION starting process: {ex}");
            _log("[error] Failed to start: " + ex.Message);
        }
    }

    public void Stop(Config cfg)
    {
        DebugLog("Stop called");
        
        try
        {
            if (!string.IsNullOrWhiteSpace(cfg.StopCommand))
            {
                DebugLog($"Executing stop command: '{cfg.StopCommand}'");
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
                var stopProc = Process.Start(stop);
                var exited = stopProc?.WaitForExit(5000) ?? false;
                DebugLog($"Stop command completed. Exited cleanly: {exited}");
                _log("[info] stop_command executed.");
            }
            else
            {
                DebugLog("No stop command configured");
            }
        }
        catch (Exception ex)
        {
            DebugLog($"Exception executing stop command: {ex}");
            _log("[warn] stop_command failed: " + ex.Message);
        }

        try
        {
            if (_proc != null && !_proc.HasExited)
            {
                DebugLog($"Killing process PID: {_proc.Id}");
                _proc.Kill(entireProcessTree: true);
                var exited = _proc.WaitForExit(3000);
                DebugLog($"Process kill completed. Exited: {exited}");
                _log("[info] Process terminated.");
            }
            else
            {
                DebugLog("Process is null or already exited - nothing to stop");
            }
        }
        catch (Exception ex)
        {
            DebugLog($"Exception killing process: {ex}");
            _log("[warn] Failed to stop process: " + ex.Message);
        }
        finally
        {
            _proc?.Dispose();
            _proc = null;
            DebugLog("ProcessManager cleaned up");
        }
    }
}
