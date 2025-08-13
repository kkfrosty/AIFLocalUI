using System.Text.Json;
using System.IO;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AiFoundryUI.Models;

public class Config
{
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    [JsonPropertyName("start_command_template")]
    public string StartCommandTemplate { get; set; } = "foundry model run {model}";
    public string StopCommand { get; set; } = "";
    public string WorkingDir { get; set; } = "";
    public Dictionary<string, string> Environment { get; set; } = new();

    public string ApiBase { get; set; } = "http://localhost:8000/v1";
    public string ApiKey { get; set; } = "";
    public string HealthUrl { get; set; } = "http://localhost:8000/health";
    public bool OpenAICompatible { get; set; } = true;

    public string ModelListCommand { get; set; } = "";
    public string SelectModelCommandTemplate { get; set; } = "";
    public List<string> Models { get; set; } = new() { "llama3.1", "phi-3.5", "qwen2.5" };

    public double DefaultTemperature { get; set; } = 0.7;

    // Global default system instructions applied when a thread has none
    public string DefaultInstructions { get; set; } = "";

    public static Config LoadOrCreate()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<Config>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (cfg != null) return cfg;
            }
        }
        catch { /* ignore and create */ }
        var def = new Config();
        Save(def);
        return def;
    }

    public static void Save(Config cfg)
    {
        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }
}
