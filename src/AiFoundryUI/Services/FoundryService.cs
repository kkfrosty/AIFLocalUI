using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AiFoundryUI.Services;

public class FoundryService
{
    private readonly Action<string> _log;
    private Process? _lastProcess;

    public FoundryService(Action<string> log)
    {
        _log = log;
    }

    private void DebugLog(string message)
    {
        var logMessage = $"[FoundryService] {message}";
        Console.WriteLine(logMessage);
        _log(logMessage);
    }

    public async Task<(int exitCode, string output, string error)> RunFoundryCommandAsync(string command, System.Threading.CancellationToken? token = null)
    {
        DebugLog($"Executing: foundry {command}");

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
        psi.ArgumentList.Add($"foundry {command}");

        try
        {
            var process = Process.Start(psi)!;
            _lastProcess = process;
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            if (token.HasValue)
            {
                try { await process.WaitForExitAsync(token.Value); }
                catch (TaskCanceledException)
                {
                    TryKillProcessTree(process);
                    throw;
                }
            }
            else
            {
                await process.WaitForExitAsync();
            }

            DebugLog($"Exit code: {process.ExitCode}");
            if (!string.IsNullOrWhiteSpace(output))
                DebugLog($"STDOUT: {output}");
            if (!string.IsNullOrWhiteSpace(error))
                DebugLog($"STDERR: {error}");

            return (process.ExitCode, output, error);
        }
        catch (Exception ex)
        {
            DebugLog($"Exception: {ex}");
            return (-1, "", ex.Message);
        }
    }

    private void TryKillProcessTree(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(true);
        }
        catch { /* ignore */ }
    }

    public void CancelLastOperation()
    {
        var p = _lastProcess;
        if (p == null) return;
        TryKillProcessTree(p);
    }

    /// <summary>
    /// Check if foundry service is running. Returns (isRunning, serviceUrl)
    /// </summary>
    public async Task<(bool isRunning, string? serviceUrl)> GetServiceStatusAsync(System.Threading.CancellationToken? token = null)
    {
        var (exitCode, output, error) = await RunFoundryCommandAsync("service status", token);
        
        if (exitCode == 0)
        {
            // Check for either pattern:
            // "Service is Started on http://127.0.0.1:51075/, PID 31784!"
            // "Model management service is running on http://127.0.0.1:54962/openai/status"
            if (output.Contains("Service is Started on") || output.Contains("service is running on"))
            {
                // Extract URL - handle both patterns
                var match = Regex.Match(output, @"(?:Service is Started on|service is running on) (http://[^/\s,]+)");
                if (match.Success)
                {
                    var url = match.Groups[1].Value;
                    // Remove any trailing path like /openai/status to get the base URL
                    if (url.EndsWith("/openai/status"))
                    {
                        url = url.Replace("/openai/status", "");
                    }
                    DebugLog($"Service is running at: {url}");
                    return (true, url);
                }
            }
        }

        DebugLog("Service is not running");
        return (false, null);
    }

    /// <summary>
    /// Start the foundry service. Returns the service URL if successful.
    /// </summary>
    public async Task<string?> StartServiceAsync(System.Threading.CancellationToken? token = null)
    {
        DebugLog("Starting foundry service...");
        var (exitCode, output, error) = await RunFoundryCommandAsync("service start", token);
        
        if (exitCode == 0)
        {
            // Handle both cases:
            // "Service is Started on http://127.0.0.1:51075/, PID 31784!"
            // "Service is already running on http://127.0.0.1:54962/."
            if (output.Contains("Service is Started on") || output.Contains("Service is already running on"))
            {
                var match = Regex.Match(output, @"(?:Service is Started on|Service is already running on) (http://[^/\s,]+)");
                if (match.Success)
                {
                    var url = match.Groups[1].Value;
                    // Remove any trailing path like /openai/status to get the base URL
                    if (url.EndsWith("/openai/status"))
                    {
                        url = url.Replace("/openai/status", "");
                    }
                    DebugLog($"Service running at: {url}");
                    return url;
                }
            }
        }

        DebugLog($"Failed to start service. Exit code: {exitCode}, Error: {error}");
        return null;
    }

    /// <summary>
    /// Get list of all available model aliases (both cached and downloadable)
    /// </summary>
    public async Task<List<string>> GetAvailableModelsAsync(System.Threading.CancellationToken? token = null)
    {
        var (exitCode, output, error) = await RunFoundryCommandAsync("model list", token);
        
        if (exitCode != 0)
        {
            DebugLog($"Failed to get model list. Exit code: {exitCode}");
            return new List<string>();
        }

        var aliases = new HashSet<string>();
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        
        bool inTable = false;
        foreach (var line in lines)
        {
            if (line.Contains("Alias") && line.Contains("Device") && line.Contains("Task"))
            {
                inTable = true;
                continue;
            }
            if (line.Contains("---") || !inTable)
                continue;
            
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                var alias = parts[0].Trim();
                if (!alias.Contains("GPU") && !alias.Contains("CPU") && 
                    !alias.Contains("chat-completion") && !alias.Contains("GB"))
                {
                    aliases.Add(alias);
                }
            }
        }
        
        var result = aliases.OrderBy(a => a).ToList();
        DebugLog($"Found {result.Count} available model aliases");
        return result;
    }

    /// <summary>
    /// Get list of cached models
    /// </summary>
    public async Task<List<string>> GetCachedModelsAsync(System.Threading.CancellationToken? token = null)
    {
        var (exitCode, output, error) = await RunFoundryCommandAsync("cache list", token);
        
        if (exitCode != 0)
        {
            DebugLog($"Failed to get cache list. Exit code: {exitCode}");
            return new List<string>();
        }

        var cachedModels = new List<string>();
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var line in lines)
        {
            if (line.Contains("💾"))
            {
                // Extract alias from line like: "💾 phi-4-mini                    Phi-4-mini-instruct-cuda-gpu"
                var match = Regex.Match(line, @"💾\s+([^\s]+)");
                if (match.Success)
                {
                    cachedModels.Add(match.Groups[1].Value);
                }
            }
        }
        
        DebugLog($"Found {cachedModels.Count} cached models: {string.Join(", ", cachedModels)}");
        return cachedModels;
    }

    /// <summary>
    /// Check if a specific model is cached
    /// </summary>
    public async Task<bool> IsModelCachedAsync(string modelAlias)
    {
        var cachedModels = await GetCachedModelsAsync();
        bool isCached = cachedModels.Any(cached => cached.Equals(modelAlias, StringComparison.OrdinalIgnoreCase) || 
                                                   cached.Contains(modelAlias, StringComparison.OrdinalIgnoreCase));
        DebugLog($"Checking if '{modelAlias}' is cached. Cached models: [{string.Join(", ", cachedModels)}]. Result: {isCached}");
        return isCached;
    }

    /// <summary>
    /// Download a model with progress reporting
    /// </summary>
    public async Task<bool> DownloadModelAsync(string modelAlias, IProgress<string>? progressCallback = null, System.Threading.CancellationToken? token = null)
    {
        DebugLog($"Starting download of model: {modelAlias}");
        
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
        psi.ArgumentList.Add($"foundry model download {modelAlias}");

        try
        {
            var process = Process.Start(psi)!;
            _lastProcess = process;
            
            // Read output line by line for progress updates
            while (!process.StandardOutput.EndOfStream)
            {
                if (token.HasValue && token.Value.IsCancellationRequested)
                {
                    TryKillProcessTree(process);
                    throw new TaskCanceledException();
                }
                var line = await process.StandardOutput.ReadLineAsync();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    DebugLog($"Download progress: {line}");
                    progressCallback?.Report(line);
                }
            }
            
            if (token.HasValue)
            {
                try { await process.WaitForExitAsync(token.Value); }
                catch (TaskCanceledException)
                {
                    TryKillProcessTree(process);
                    throw;
                }
            }
            else
            {
                await process.WaitForExitAsync();
            }
            
            var success = process.ExitCode == 0;
            DebugLog($"Download completed. Success: {success}");
            return success;
        }
        catch (Exception ex)
        {
            DebugLog($"Download failed with exception: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Load a model
    /// </summary>
    public async Task<bool> LoadModelAsync(string modelAlias, System.Threading.CancellationToken? token = null)
    {
        DebugLog($"Loading model: {modelAlias}");
        var (exitCode, output, error) = await RunFoundryCommandAsync($"model load {modelAlias}", token);
        
        var success = exitCode == 0 && output.Contains("loaded successfully");
        DebugLog($"Model load result: {success}");
        return success;
    }

    /// <summary>
    /// Get the actual model ID for a loaded model alias
    /// </summary>
    public async Task<string?> GetLoadedModelIdAsync(string modelAlias, System.Threading.CancellationToken? token = null)
    {
        DebugLog($"Getting loaded model ID for alias: {modelAlias}");
        var (exitCode, output, error) = await RunFoundryCommandAsync("service list", token);
        
        if (exitCode != 0)
        {
            DebugLog($"Failed to get service list. Exit code: {exitCode}");
            return null;
        }

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        
        bool inModelsSection = false;
        foreach (var line in lines)
        {
            if (line.Contains("Models running in service:"))
            {
                inModelsSection = true;
                continue;
            }
            if (!inModelsSection) continue;
            
            // Look for lines like: "🟢  gpt-oss-20b                    gpt-oss-20b-cuda-gpu"
            if (line.Contains(modelAlias))
            {
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    // Find the alias in the parts and get the model ID (last part)
                    for (int i = 0; i < parts.Length - 1; i++)
                    {
                        if (parts[i] == modelAlias)
                        {
                            var modelId = parts[parts.Length - 1];
                            DebugLog($"Found model ID '{modelId}' for alias '{modelAlias}'");
                            return modelId;
                        }
                    }
                }
            }
        }
        
        DebugLog($"No loaded model found for alias: {modelAlias}");
        return null;
    }

    /// <summary>
    /// Get list of currently loaded models using "foundry service list"
    /// </summary>
    public async Task<List<string>> GetCurrentlyLoadedModelsAsync(System.Threading.CancellationToken? token = null)
    {
        DebugLog("Getting currently loaded models with 'foundry service list'");
        var (exitCode, output, error) = await RunFoundryCommandAsync("service list", token);
        
        if (exitCode != 0)
        {
            DebugLog($"Failed to get service list. Exit code: {exitCode}");
            return new List<string>();
        }

        var loadedModels = new List<string>();
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        
        // Check for "No models are currently loaded in the service." message
        if (output.Contains("No models are currently loaded in the service"))
        {
            DebugLog("No models currently loaded in service");
            return loadedModels; // Return empty list
        }

        bool inModelsSection = false;
        foreach (var line in lines)
        {
            // Look for the section that lists running models
            if (line.Contains("Models running in service:"))
            {
                inModelsSection = true;
                continue;
            }
            if (!inModelsSection) continue;
            
            // Look for lines like: "🟢  gpt-oss-20b                    gpt-oss-20b-cuda-gpu"
            // Extract the alias (second column) and model ID (third column)
            if (line.Contains("🟢"))
            {
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    // The format is typically: [icon] [alias] [model-id]
                    // We want to return the alias, but could also return the model ID
                    var alias = parts[1];
                    var modelId = parts[2];
                    
                    loadedModels.Add(alias);
                    DebugLog($"Found loaded model: {alias} (ID: {modelId})");
                }
            }
        }
        
        DebugLog($"Found {loadedModels.Count} loaded models");
        return loadedModels;
    }

    /// <summary>
    /// Stop the foundry service
    /// </summary>
    public async Task<bool> StopServiceAsync(System.Threading.CancellationToken? token = null)
    {
        DebugLog("Stopping foundry service...");
        var (exitCode, output, error) = await RunFoundryCommandAsync("service stop", token);
        
        var success = exitCode == 0;
        if (success)
        {
            DebugLog("Service stopped successfully");
        }
        else
        {
            DebugLog($"Failed to stop service. Exit code: {exitCode}, Error: {error}");
        }
        return success;
    }

    /// <summary>
    /// Unload a model
    /// </summary>
    public async Task<bool> UnloadModelAsync(string modelAlias, System.Threading.CancellationToken? token = null)
    {
        DebugLog($"Unloading model: {modelAlias}");
        var (exitCode, output, error) = await RunFoundryCommandAsync($"model unload {modelAlias}", token);
        
        var success = exitCode == 0;
        DebugLog($"Model unload result: {success}");
        return success;
    }
}
