using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AiFoundryUI.Models;

namespace AiFoundryUI.Services;

public class ChatClient
{
    private readonly HttpClient _http;
    private readonly Config _cfg;
    private readonly FoundryService? _foundryService;
    private string? _baseUrl;

    public ChatClient(Config cfg)
    {
        _cfg = cfg;
        
        // Create HttpClient with better timeout and connection settings
        var handler = new HttpClientHandler()
        {
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true // For local development
        };
        
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5) // Overall timeout for the entire request
        };
        
        if (!string.IsNullOrWhiteSpace(_cfg.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);
    }

    public ChatClient(Config cfg, FoundryService foundryService) : this(cfg)
    {
        _foundryService = foundryService;
    }

    public void SetBaseUrl(string baseUrl)
    {
        _baseUrl = baseUrl?.TrimEnd('/');
        Console.WriteLine($"[ChatClient] Base URL set to: {_baseUrl}");
    }

    /// <summary>
    /// Get the current service URL, either from foundry service status or fallback to cached base URL
    /// </summary>
    private async Task<string?> GetCurrentServiceUrlAsync()
    {
        // If we have a FoundryService, always check the current service status to get the latest URL
        if (_foundryService != null)
        {
            try
            {
                var (isRunning, serviceUrl) = await _foundryService.GetServiceStatusAsync();
                if (isRunning && !string.IsNullOrWhiteSpace(serviceUrl))
                {
                    DebugLog($"Got current service URL from foundry: {serviceUrl}");
                    return serviceUrl.TrimEnd('/');
                }
                else
                {
                    DebugLog("Foundry service not running or no URL available");
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Failed to get current service URL from foundry: {ex.Message}");
            }
        }

        // Fallback to cached base URL or config
        var fallbackUrl = !string.IsNullOrWhiteSpace(_baseUrl) ? _baseUrl : _cfg.ApiBase?.TrimEnd('/');
        DebugLog($"Using fallback URL: {fallbackUrl}");
        return fallbackUrl;
    }

    private void DebugLog(string message)
    {
        var logMessage = $"[ChatClient] {message}";
        Console.WriteLine(logMessage);
    }

    public async Task<bool> HealthOkAsync()
    {
        // Get the current service URL dynamically
        var baseUrl = await GetCurrentServiceUrlAsync();
        
        if (string.IsNullOrWhiteSpace(baseUrl)) 
        {
            DebugLog("No base URL available for health check");
            return false;
        }
        
        try
        {
            var healthUrl = $"{baseUrl}/v1/models";
            DebugLog($"Checking health at: {healthUrl}");
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            var r = await _http.GetAsync(healthUrl, cts.Token);
            DebugLog($"Health check response: {r.StatusCode}");
            return r.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            DebugLog($"Health check failed: {ex.Message}");
            return false;
        }
    }

    public async Task<List<string>> ListModelsAsync()
    {
        var results = new List<string>();

        // Get the current service URL dynamically
        var baseUrl = await GetCurrentServiceUrlAsync();
        
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            try
            {
                DebugLog($"Listing models from: {baseUrl}/v1/models");
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                var r = await _http.GetAsync($"{baseUrl}/v1/models", cts.Token);
                if (r.IsSuccessStatusCode)
                {
                    using var s = await r.Content.ReadAsStreamAsync(cts.Token);
                    using var doc = await JsonDocument.ParseAsync(s, cancellationToken: cts.Token);
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        results = data.EnumerateArray()
                            .Select(e => e.TryGetProperty("id", out var id) ? id.GetString() : null)
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .Cast<string>()
                            .ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Failed to list models via API: {ex.Message}");
            }
        }

        if (results.Count == 0 && _cfg.Models.Count > 0)
            results = _cfg.Models;

        return results;
    }

    public async Task<string> SendChatAsync(string model, List<ChatMessage> messages, float temperature)
    {
        // Get the current service URL dynamically - this ensures we always use the latest port
        var baseUrl = await GetCurrentServiceUrlAsync();
        
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            DebugLog("No base URL set for chat");
            return "Error: No service URL available";
        }

        // Resolve alias to actual loaded model ID if possible
        string effectiveModel = model;
        try
        {
            if (_foundryService != null)
            {
                var mappedId = await _foundryService.GetLoadedModelIdAsync(model);
                if (!string.IsNullOrWhiteSpace(mappedId))
                {
                    DebugLog($"Resolved alias '{model}' to model id '{mappedId}'");
                    effectiveModel = mappedId;
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog($"Model ID resolution failed for '{model}': {ex.Message}");
        }

        var url = $"{baseUrl}/v1/chat/completions";
        DebugLog($"Sending chat request to: {url}");

        var body = new
        {
            model = effectiveModel,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            temperature,
            max_tokens = 2048,
            stream = false
        };

        try
        {
            var json = JsonSerializer.Serialize(body);
            DebugLog($"Request body: {json.Substring(0, Math.Min(200, json.Length))}...");
            
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5)); // Increased to 5 minutes for longer responses like sonnets
            var response = await _http.PostAsync(url, content, cts.Token);
            
            DebugLog($"Chat response status: {response.StatusCode}");
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cts.Token);
                DebugLog($"Chat error response: {errorContent}");
                return $"Error {response.StatusCode}: {errorContent}";
            }

            var responseJson = await response.Content.ReadAsStringAsync(cts.Token);
            DebugLog($"Chat response: {responseJson.Substring(0, Math.Min(200, responseJson.Length))}...");
            
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                var firstChoice = choices.EnumerateArray().FirstOrDefault();
                if (firstChoice.TryGetProperty("message", out var message))
                {
                    if (message.TryGetProperty("content", out var messageContent))
                    {
                        var raw = messageContent.GetString() ?? "No response content";
                        return SanitizeAssistantContent(raw);
                    }
                }
                // Fallback: some providers put text in delta.content even on non-stream
                if (firstChoice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var deltaContent))
                {
                    var raw = deltaContent.GetString() ?? "No response content";
                    return SanitizeAssistantContent(raw);
                }
            }

            return "Error: Unexpected response format";
        }
        catch (Exception ex)
        {
            DebugLog($"Chat request failed: {ex}");
            
            // Provide more specific error messages
            if (ex is TaskCanceledException)
            {
                return "Error: Request timed out. The model may be taking too long to respond.";
            }
            else if (ex is HttpRequestException)
            {
                return $"Error: Network connection failed - {ex.Message}";
            }
            else if (ex is System.IO.IOException)
            {
                return "Error: Connection was interrupted. Please try again.";
            }
            
            return $"Error: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _http?.Dispose();
    }

    // Extract only the assistant's final message from provider-formatted content
    private static string SanitizeAssistantContent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // Pattern: <|channel|>final<|message|>...<|return|> (or <|end|>)
        var m = Regex.Match(text, "<\\|channel\\|>final<\\|message\\|>([\\s\\S]*?)(?:<\\|return\\|>|<\\|end\\|>|$)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return m.Groups[1].Value.Trim();
        }

        // If contains other channels, strip all known tag markers and return remainder
        if (text.Contains("<|channel|>") || text.Contains("<|message|>") || text.Contains("<|start|>") || text.Contains("<|end|>") || text.Contains("<|return|>"))
        {
            var cleaned = Regex.Replace(text, "<\\|(?:start|end|return|channel|message)\\|>", string.Empty, RegexOptions.IgnoreCase);
            return cleaned.Trim();
        }

        return text.Trim();
    }
}
