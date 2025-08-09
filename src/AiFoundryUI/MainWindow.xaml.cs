using System.Text;
using System.Windows;
using AiFoundryUI.Models;
using AiFoundryUI.Services;

namespace AiFoundryUI;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly Config _config;
    private readonly ProcessManager _proc;
    private readonly ChatClient _chat;
    private readonly SystemMonitor _monitor;
    private readonly AiFoundryLocalClient _foundryClient;
    private readonly List<ChatMessage> _messages = new()
    {
        new ChatMessage { Role = "system", Content = "You are a helpful assistant." }
    };

    public MainWindow()
    {
        InitializeComponent();
        _config = Config.LoadOrCreate();
        _foundryClient = new AiFoundryLocalClient(Log);
        _proc = new ProcessManager(Log, OnProcessOutput);
        _chat = new ChatClient(_config);
        _monitor = new SystemMonitor();

        _monitor.MetricsUpdated += (_, m) =>
        {
            Dispatcher.Invoke(() =>
            {
                PbCpu.Value = m.CpuPercent;
                LblCpu.Text = $"{m.CpuPercent:0}%";
                PbMem.Value = m.MemoryPercent;
                LblMem.Text = $"{m.MemoryPercent:0}%";
                PbDisk.Value = m.DiskPercent;
                LblDisk.Text = $"{m.DiskPercent:0}%";
                PbGpu.Value = m.GpuPercent;
                LblGpu.Text = $"{m.GpuPercent:0}%";
            });
        };
        _monitor.Start();

        _ = LoadModelsOnStartupAsync();
    }

    private void OnProcessOutput(string output)
    {
        // Extract service URL from foundry output
        var serviceUrl = _foundryClient.ExtractServiceUrlFromOutput(output);
        if (!string.IsNullOrWhiteSpace(serviceUrl))
        {
            _foundryClient.SetBaseUrl(serviceUrl);
            // Update chat client's API base
            _config.ApiBase = serviceUrl + "/v1";
        }
    }

    private async Task LoadModelsOnStartupAsync()
    {
        UpdateStatus("Loading models...");
        try
        {
            var models = await _foundryClient.GetAvailableModelAliasesAsync();
            if (models.Count > 0)
            {
                Dispatcher.Invoke(() =>
                {
                    CmbModels.ItemsSource = models;
                    if (CmbModels.SelectedIndex < 0)
                        CmbModels.SelectedIndex = 0;
                });
                Log($"[info] Loaded {models.Count} models: {string.Join(", ", models)}");
            }
            else
            {
                Log("[warn] No models found via CLI");
            }
        }
        catch (Exception ex)
        {
            Log($"[error] Failed to load models: {ex.Message}");
        }
        finally
        {
            UpdateStatus("Idle");
        }
    }

    private void Log(string msg)
    {
        Dispatcher.Invoke(() =>
        {
            TxtLogs.AppendText(msg + Environment.NewLine);
            TxtLogs.CaretIndex = TxtLogs.Text.Length;
            TxtLogs.ScrollToEnd();
        });
    }

    private async Task InitialProbeAsync()
    {
        UpdateStatus("Checking...");
        LblHealth.Text = string.Empty;
        try
        {
            var ok = await _chat.HealthOkAsync();
            UpdateStatus(ok ? "Running" : "Idle");
            LblHealth.Text = ok ? "Health: OK" : "Health: No response";
            if (ok) await RefreshModelsAsync();
        }
        catch (Exception ex)
        {
            UpdateStatus("Idle");
            LblHealth.Text = "Health: Error";
            Log($"[error] Health check failed: {ex.Message}");
        }
    }

    private void UpdateStatus(string text) => Dispatcher.Invoke(() => TxtStatus.Text = text);

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_config.StartCommandTemplate))
        {
            MessageBox.Show("Set start_command_template in appsettings.json", "Missing config");
            return;
        }
        var selectedModel = CmbModels.SelectedItem as string ?? string.Empty;
        if (string.IsNullOrWhiteSpace(selectedModel))
        {
            MessageBox.Show("Select a model first.", "Missing model");
            return;
        }
        _proc.Start(_config, selectedModel);
        UpdateStatus("Starting...");
        for (int i = 0; i < 20; i++)
        {
            if (await _chat.HealthOkAsync())
            {
                UpdateStatus("Running");
                LblHealth.Text = "Health: OK";
                await RefreshModelsAsync();
                return;
            }
            await Task.Delay(1000);
        }
        UpdateStatus("No health/timeout");
        LblHealth.Text = "Health: timeout";
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _proc.Stop(_config);
        UpdateStatus("Stopped");
        LblHealth.Text = "";
    }

    private async void BtnRefreshModels_Click(object sender, RoutedEventArgs e)
    {
        await RefreshModelsAsync();
    }

    private async Task RefreshModelsAsync()
    {
        try
        {
            var models = await _foundryClient.GetAvailableModelAliasesAsync();
            CmbModels.ItemsSource = models;
            if (CmbModels.SelectedIndex < 0 && CmbModels.Items.Count > 0)
                CmbModels.SelectedIndex = 0;
            Log("[info] Models: " + (models.Count == 0 ? "none" : string.Join(", ", models)));
        }
        catch (Exception ex)
        {
            Log($"[error] Listing models failed: {ex.Message}");
        }
    }

    private void BtnSelectModel_Click(object sender, RoutedEventArgs e)
    {
        var model = CmbModels.SelectedItem as string ?? string.Empty;
        if (string.IsNullOrWhiteSpace(model))
        {
            MessageBox.Show("Pick a model first.", "Select Model");
            return;
        }

        Task.Run(async () =>
        {
            var ok = await _chat.SelectModelAsync(model);
            Log(ok ? "[info] Model selected." : "[warn] Model selection may have failed.");
        });
    }

    private async void BtnSend_Click(object sender, RoutedEventArgs e)
    {
        var model = CmbModels.SelectedItem as string ?? string.Empty;
        if (string.IsNullOrWhiteSpace(model))
        {
            MessageBox.Show("Select a model.", "Missing model");
            return;
        }
        var prompt = TxtPrompt.Text.Trim();
        if (string.IsNullOrEmpty(prompt)) return;

        AppendChat("You", prompt);
        TxtPrompt.Clear();

        _messages.Add(new ChatMessage { Role = "user", Content = prompt });
        try
        {
            string reply;
            if (_config.OpenAICompatible)
            {
                var temp = (float)SldTemp.Value;
                reply = await _chat.SendChatOpenAIAsync(model, _messages, temp);
            }
            else
            {
                reply = "(openai_compatible is false; customize ChatClient for your API.)";
            }
            _messages.Add(new ChatMessage { Role = "assistant", Content = reply });
            AppendChat("Assistant", reply);
        }
        catch (Exception ex)
        {
            AppendChat("Error", ex.Message);
        }
    }

    private void AppendChat(string who, string text)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{who}: {text}");
        sb.AppendLine();
        TxtChat.Text += sb.ToString();
    }

    private void BtnOpenConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Config.FilePath;
            if (!System.IO.File.Exists(path)) Config.Save(_config);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "notepad.exe",
                ArgumentList = { path },
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to open config: " + ex.Message);
        }
    }
}