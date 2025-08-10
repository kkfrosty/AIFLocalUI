using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AiFoundryUI.Models;

namespace AiFoundryUI.Services;

public class ChatClient
{
    private readonly HttpClient _http = new();
    private readonly Config _cfg;
    private string? _baseUrl;

    public ChatClient(Config cfg)
    {
        _cfg = cfg;
        if (!string.IsNullOrWhiteSpace(_cfg.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);
    }

    public void SetBaseUrl(string baseUrl)
    {
        _baseUrl = baseUrl?.TrimEnd('/');
        Console.WriteLine($"[ChatClient] Base URL set to: {_baseUrl}");
    }

    private void DebugLog(string message)
    {
        var logMessage = $"[ChatClient] {message}";
        Console.WriteLine(logMessage);
    }

    public async Task<bool> HealthOkAsync()
    {
        // For foundry service, try the /v1/models endpoint as a health check
        var baseUrl = !string.IsNullOrWhiteSpace(_baseUrl) ? _baseUrl : _cfg.ApiBase?.TrimEnd('/');
        
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

        var baseUrl = !string.IsNullOrWhiteSpace(_baseUrl) ? _baseUrl : _cfg.ApiBase?.TrimEnd('/');
        
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
        var baseUrl = !string.IsNullOrWhiteSpace(_baseUrl) ? _baseUrl : _cfg.ApiBase?.TrimEnd('/');
        
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            DebugLog("No base URL set for chat");
            return "Error: No service URL available";
        }

        var url = $"{baseUrl}/v1/chat/completions";
        DebugLog($"Sending chat request to: {url}");

        var body = new
        {
            model,
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
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
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
                        return messageContent.GetString() ?? "No response content";
                    }
                }
            }

            return "Error: Unexpected response format";
        }
        catch (Exception ex)
        {
            DebugLog($"Chat request failed: {ex}");
            return $"Error: {ex.Message}";
        }
    }
}
