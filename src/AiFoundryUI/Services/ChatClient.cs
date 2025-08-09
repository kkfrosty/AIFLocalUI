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

    public ChatClient(Config cfg)
    {
        _cfg = cfg;
        if (!string.IsNullOrWhiteSpace(_cfg.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);
    }

    public async Task<bool> HealthOkAsync()
    {
        var url = _cfg.HealthUrl;
        if (string.IsNullOrWhiteSpace(url)) return true;
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            var r = await _http.GetAsync(url, cts.Token);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<List<string>> ListModelsAsync()
    {
        var results = new List<string>();

        if (_cfg.OpenAICompatible)
        {
            var baseUrl = _cfg.ApiBase.TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                try
                {
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var r = await _http.GetAsync($"{baseUrl}/models", cts.Token);
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
                catch { /* ignore */ }
            }
        }

        if (results.Count == 0 && !string.IsNullOrWhiteSpace(_cfg.ModelListCommand))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(_cfg.ModelListCommand);
                var p = Process.Start(psi)!;
                var output = await p.StandardOutput.ReadToEndAsync();
                p.WaitForExit(15000);
                var fromCli = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                results = fromCli.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            }
            catch { /* ignore */ }
        }

        if (results.Count == 0 && _cfg.Models.Count > 0)
            results = _cfg.Models;

        return results;
    }

    public async Task<bool> SelectModelAsync(string modelId)
    {
        if (!string.IsNullOrWhiteSpace(_cfg.SelectModelCommandTemplate))
        {
            try
            {
                var cmd = _cfg.SelectModelCommandTemplate.Replace("{model}", modelId);
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(cmd);
                var p = Process.Start(psi)!;
                p.WaitForExit(30000);
                return true;
            }
            catch { return false; }
        }
        return true;
    }

    public async Task<string> SendChatOpenAIAsync(string model, List<ChatMessage> messages, float temperature)
    {
        var baseUrl = _cfg.ApiBase.TrimEnd('/');
        var url = $"{baseUrl}/chat/completions";
        var body = new
        {
            model,
            messages,
            temperature,
            stream = false
        };
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        var resp = await _http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: cts.Token);
        try
        {
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return content ?? "";
        }
        catch
        {
            return doc.RootElement.ToString();
        }
    }
}
