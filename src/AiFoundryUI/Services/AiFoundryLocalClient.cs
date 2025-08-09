using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using AiFoundryUI.Models;

namespace AiFoundryUI.Services;

public class AiFoundryLocalClient
{
    private readonly HttpClient _http = new();
    private readonly Action<string> _log;
    private string? _baseUrl;

    public AiFoundryLocalClient(Action<string> log)
    {
        _log = log;
    }

    public void SetBaseUrl(string baseUrl)
    {
        _baseUrl = baseUrl?.TrimEnd('/');
    }

    /// <summary>
    /// Gets available models via CLI 'foundry model list'
    /// </summary>
    public async Task<List<string>> GetAvailableModelAliasesAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("foundry model list");

        try
        {
            var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            
            // Parse table format to extract unique aliases
            var aliases = new HashSet<string>();
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            
            bool inTable = false;
            foreach (var line in lines)
            {
                // Skip header and separator lines
                if (line.Contains("Alias") && line.Contains("Device") && line.Contains("Task"))
                {
                    inTable = true;
                    continue;
                }
                if (line.Contains("---") || !inTable)
                    continue;
                
                // Extract alias from first column (non-empty aliases only)
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
                {
                    var alias = parts[0].Trim();
                    // Skip if it's not a valid alias (contains GPU/CPU/device info)
                    if (!alias.Contains("GPU") && !alias.Contains("CPU") && 
                        !alias.Contains("chat-completion") && !alias.Contains("GB") &&
                        !alias.Contains("MIT") && !alias.Contains("apache"))
                    {
                        aliases.Add(alias);
                    }
                }
            }
            
            var result = aliases.OrderBy(a => a).ToList();
            _log($"[info] Found {result.Count} unique model aliases via CLI");
            return result;
        }
        catch (Exception ex)
        {
            _log($"[error] Failed to get models via CLI: {ex.Message}");
            return new List<string>();
        }
    }

    /// <summary>
    /// Gets available models via REST API GET /foundry/list
    /// </summary>
    public async Task<List<string>> GetAvailableModelsFromApiAsync()
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
        {
            _log("[warn] No base URL set for API calls");
            return new List<string>();
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await _http.GetAsync($"{_baseUrl}/foundry/list", cts.Token);
            
            if (!response.IsSuccessStatusCode)
            {
                _log($"[warn] API call failed: {response.StatusCode}");
                return new List<string>();
            }

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);

            var aliases = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var modelsArray))
            {
                foreach (var model in modelsArray.EnumerateArray())
                {
                    if (model.TryGetProperty("alias", out var aliasElement))
                    {
                        var alias = aliasElement.GetString();
                        if (!string.IsNullOrWhiteSpace(alias))
                            aliases.Add(alias);
                    }
                }
            }

            _log($"[info] Found {aliases.Count} models via API");
            return aliases;
        }
        catch (Exception ex)
        {
            _log($"[error] Failed to get models via API: {ex.Message}");
            return new List<string>();
        }
    }

    /// <summary>
    /// Gets loaded models via REST API GET /openai/loadedmodels
    /// </summary>
    public async Task<List<string>> GetLoadedModelsAsync()
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            return new List<string>();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await _http.GetAsync($"{_baseUrl}/openai/loadedmodels", cts.Token);
            
            if (!response.IsSuccessStatusCode)
                return new List<string>();

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var models = await JsonSerializer.DeserializeAsync<string[]>(stream, cancellationToken: cts.Token);
            
            return models?.ToList() ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Checks if the service is running by calling GET /openai/status
    /// </summary>
    public async Task<bool> IsServiceRunningAsync()
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            return false;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var response = await _http.GetAsync($"{_baseUrl}/openai/status", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts service URL from foundry command output
    /// Expected format: "🟢 Service is Started on http://127.0.0.1:52356/, PID 3728!"
    /// </summary>
    public string? ExtractServiceUrlFromOutput(string output)
    {
        try
        {
            var lines = output.Split('\n');
            foreach (var line in lines)
            {
                if (line.Contains("Service is Started on"))
                {
                    var start = line.IndexOf("http://");
                    if (start >= 0)
                    {
                        var end = line.IndexOf("/,", start);
                        if (end > start)
                        {
                            var url = line.Substring(start, end - start);
                            _log($"[info] Detected service URL: {url}");
                            return url;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log($"[warn] Failed to extract service URL: {ex.Message}");
        }
        return null;
    }
}
