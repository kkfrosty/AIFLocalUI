using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AiFoundryUI.Models;
using AiFoundryUI.Services;

namespace AiFoundryUI;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly Config _config;
    private readonly ChatClient _chat;
    private readonly SystemMonitor _monitor;
    private readonly FoundryService _foundryService;
    private readonly List<ChatMessage> _messages = new()
    {
        new ChatMessage { Role = "system", Content = "You are a helpful assistant." }
    };

    private string? _currentModel;
    private bool _isModelLoaded = false;
    private bool _isChatBusy = false;
    private bool _isServiceRunning = false;

    public MainWindow()
    {
        InitializeComponent();
        _config = Config.LoadOrCreate();
        _foundryService = new FoundryService(Log);
        _chat = new ChatClient(_config, _foundryService);
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
                if (!string.IsNullOrWhiteSpace(m.GpuName))
                    LblGpuName.Text = m.GpuName;
                if (m.GpuMemTotalMB > 0)
                    LblGpuMem.Text = $"{m.GpuMemUsedMB:0} / {m.GpuMemTotalMB:0} MB";
                else
                    LblGpuMem.Text = string.Empty;
            });
        };
        _monitor.Start();

        // Initially disable everything except model selection
        // SetUIState(false, false, false, false);

        _ = LoadModelsOnStartupAsync();
        
        // Setup temperature slider value display
        SldTemp.ValueChanged += (s, e) => LblTemp.Text = $"{e.NewValue:0.0}";
    }

    // Event handlers for new UI elements
    private void TxtPrompt_GotFocus(object sender, RoutedEventArgs e)
    {
        if (TxtPrompt.Text == "Type your message here..." && TxtPrompt.Foreground.ToString() == "#FF9CA3AF")
        {
            TxtPrompt.Text = "";
            TxtPrompt.Foreground = System.Windows.Media.Brushes.Black;
        }
    }

    private void TxtPrompt_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtPrompt.Text))
        {
            TxtPrompt.Text = "Type your message here...";
            TxtPrompt.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(156, 163, 175));
        }
    }

    private void TxtPrompt_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && !e.KeyboardDevice.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
        {
            e.Handled = true;
            BtnSend_Click(sender, e);
        }
    }

    private void CmbModels_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Auto-hide welcome panel when model is selected
        if (CmbModels.SelectedItem != null)
        {
            WelcomePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OnProcessOutput(string output)
    {
        // This method is no longer needed with the new FoundryService architecture
        // The FoundryService handles all CLI output processing internally
        Console.WriteLine($"Legacy OnProcessOutput called: {output}");
    }

    private void ParseAndDisplayProgress(string output)
    {
        Dispatcher.Invoke(() =>
        {
            // Extract percentage first - it might be present in any type of message
            var percent = ExtractProgressPercent(output);
            
            // Check for download progress patterns
            if (output.Contains("Downloading") || output.Contains("downloading"))
            {
                ShowProgress("Downloading model...", percent);
            }
            else if (output.Contains("Loading") || output.Contains("loading"))
            {
                ShowProgress("Loading model...", percent);
            }
            else if (output.Contains("Starting") || output.Contains("starting"))
            {
                ShowProgress("Starting model...", percent);
            }
            else if (output.Contains("Ready") || output.Contains("ready") || output.Contains("started"))
            {
                ShowProgress("Model ready", 100);
                // Hide progress after a brief delay
                _ = Task.Delay(2000).ContinueWith(_ => Dispatcher.Invoke(() => HideProgress()));
            }
            else if (output.Contains("Error") || output.Contains("error") || output.Contains("failed"))
            {
                ShowProgress("Error occurred", null);
                _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.Invoke(() => HideProgress()));
            }
            else if (percent.HasValue && ProgressPanel.Visibility == Visibility.Visible)
            {
                // If we're already showing progress and we get a percentage update, just update the progress
                UpdateProgressPercent(percent.Value);
            }
        });
    }

    private int? ExtractProgressPercent(string output)
    {
        // Look for percentage patterns like "45%", "75.5%", etc.
        var regex = new System.Text.RegularExpressions.Regex(@"(\d+(?:\.\d+)?)%");
        var match = regex.Match(output);
        if (match.Success && double.TryParse(match.Groups[1].Value, out double percent))
        {
            return (int)Math.Round(percent);
        }
        return null;
    }

    private void UpdateProgressPercent(int percent)
    {
        if (ProgressPanel.Visibility == Visibility.Visible)
        {
            PbProgress.Value = percent;
            LblProgressPercent.Text = $"{percent}%";
            PbProgress.IsIndeterminate = false;
            PbProgress.Visibility = Visibility.Visible;
            LblProgressPercent.Visibility = Visibility.Visible;
        }
    }

    private void ShowProgress(string message, int? percent)
    {
        System.Diagnostics.Debug.WriteLine($"[ShowProgress] Message: {message}, Percent: {percent}");
        ProgressPanel.Visibility = Visibility.Visible;
        LblProgressStatus.Text = message;
        
        if (percent.HasValue)
        {
            PbProgress.IsIndeterminate = false;
            PbProgress.Value = percent.Value;
            LblProgressPercent.Text = $"{percent}%";
            PbProgress.Visibility = Visibility.Visible;
            LblProgressPercent.Visibility = Visibility.Visible;
            System.Diagnostics.Debug.WriteLine($"[ShowProgress] Set progress to {percent}% - Progress bar value: {PbProgress.Value}");
        }
        else
        {
            PbProgress.IsIndeterminate = true;
            LblProgressPercent.Text = "";
            PbProgress.Visibility = Visibility.Visible;
            LblProgressPercent.Visibility = Visibility.Collapsed;
            System.Diagnostics.Debug.WriteLine($"[ShowProgress] Set indeterminate progress");
        }
    }

    private void HideProgress()
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        PbProgress.IsIndeterminate = false;
    }

    private async Task LoadModelsOnStartupAsync()
    {
        UpdateStatus("Checking foundry service...");
        
        try
        {
            // Step 1: Check if foundry service is already running
            var (isRunning, serviceUrl) = await _foundryService.GetServiceStatusAsync();
            UpdateServiceStatusDisplay(isRunning);
            
            // Step 2: If not running, start the service
            if (!isRunning)
            {
                UpdateStatus("Starting foundry service...");
                serviceUrl = await _foundryService.StartServiceAsync();
                UpdateServiceStatusDisplay(!string.IsNullOrWhiteSpace(serviceUrl));
                
                if (string.IsNullOrWhiteSpace(serviceUrl))
                {
                    UpdateStatus("Failed to start foundry service");
                    Log("[error] Failed to start foundry service. Please check that 'foundry' command is available.");
                    return;
                }
            }

            // Step 3: Configure chat client with the actual service URL
            if (!string.IsNullOrWhiteSpace(serviceUrl))
            {
                _chat.SetBaseUrl(serviceUrl);
                Dispatcher.Invoke(() => TxtServiceUrl.Text = serviceUrl);
                Log($"[info] Foundry service running at: {serviceUrl}");
            }

            // Step 4: Now get available models from foundry cache
            UpdateStatus("Loading available models...");
            var models = await _foundryService.GetAvailableModelsAsync();
            
            if (models?.Count > 0)
            {
                Dispatcher.Invoke(() =>
                {
                    CmbModels.ItemsSource = models;
                    if (CmbModels.SelectedIndex < 0)
                        CmbModels.SelectedIndex = 0;
                });
                Log($"[info] Loaded {models.Count} models: {string.Join(", ", models)}");
                UpdateStatus("Ready - Select a model and click Start");
            }
            else
            {
                Log("[warn] No models found in foundry cache");
                UpdateStatus("No models available");
            }
        }
        catch (Exception ex)
        {
            Log($"[error] Failed to initialize foundry service: {ex.Message}");
            UpdateStatus("Service initialization failed");
        }
    }

    private void Log(string msg)
    {
        // For the new clean UI, we'll only show important status updates
        // Verbose logs are no longer displayed in the UI
        System.Diagnostics.Debug.WriteLine($"[AI Foundry] {msg}");
        Console.WriteLine($"[AI Foundry] {msg}"); // Add console output for debugging
        
        // Show only important status messages in the status area
        if (msg.Contains("[error]"))
        {
            UpdateStatus("Error occurred");
        }
        else if (msg.Contains("Starting"))
        {
            UpdateStatus("Starting model...");
        }
        else if (msg.Contains("models"))
        {
            // Don't spam status with model loading details
        }
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

    private void UpdateStatus(string text) => Dispatcher.Invoke(() => TxtServiceStatus.Text = text);

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        Console.WriteLine("=== START BUTTON CLICKED ===");
        
        var selectedModel = CmbModels.SelectedItem as string ?? string.Empty;
        Console.WriteLine($"Selected model: '{selectedModel}'");
        
        if (string.IsNullOrWhiteSpace(selectedModel))
        {
            Console.WriteLine("ERROR: No model selected");
            MessageBox.Show("Select a model first.", "Missing model");
            return;
        }

        try
        {
            // Step 1: Check if model is cached (downloaded)
            UpdateStatus("Checking if model is cached...");
            bool isCached = await _foundryService.IsModelCachedAsync(selectedModel);
            Console.WriteLine($"Model {selectedModel} cached: {isCached}");

            // Step 2: If not cached, download it first
            if (!isCached)
            {
                Console.WriteLine($"Model {selectedModel} not cached, starting download...");
                UpdateStatus($"Downloading {selectedModel}...");
                ShowProgress($"Downloading {selectedModel}...", null);
                
                // Set up progress callback to show download progress
                var progress = new Progress<string>(output =>
                {
                    Console.WriteLine($"Download progress: {output}");
                    Dispatcher.Invoke(() =>
                    {
                        var percent = ExtractProgressPercent(output);
                        if (percent.HasValue)
                        {
                            UpdateProgressPercent(percent.Value);
                        }
                        else
                        {
                            ShowProgress($"Downloading: {output}", null);
                        }
                    });
                });

                bool downloadSuccess = await _foundryService.DownloadModelAsync(selectedModel, progress);
                
                if (!downloadSuccess)
                {
                    UpdateStatus("Download failed");
                    ShowProgress("Download failed", null);
                    _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.Invoke(() => HideProgress()));
                    return;
                }
                
                Console.WriteLine($"Model {selectedModel} download completed");
                UpdateStatus("Download completed");
            }

            // Step 3: Check service status and start if needed
            Console.WriteLine("Checking foundry service status...");
            UpdateStatus("Checking service status...");
            var (isRunning, serviceUrl) = await _foundryService.GetServiceStatusAsync();
            
            if (!isRunning)
            {
                Console.WriteLine("Service not running, starting...");
                UpdateStatus("Starting foundry service...");
                ShowProgress("Starting service...", null);
                serviceUrl = await _foundryService.StartServiceAsync();
                
                if (string.IsNullOrWhiteSpace(serviceUrl))
                {
                    UpdateStatus("Failed to start service");
                    ShowProgress("Service start failed", null);
                    _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.Invoke(() => HideProgress()));
                    return;
                }
            }

            // Step 4: Configure chat client with service URL
            if (!string.IsNullOrWhiteSpace(serviceUrl))
            {
                _chat.SetBaseUrl(serviceUrl);
                Dispatcher.Invoke(() => TxtServiceUrl.Text = serviceUrl);
                Console.WriteLine($"Service URL set to: {serviceUrl}");
            }

            // Step 5: Load the model
            Console.WriteLine($"Loading model {selectedModel}...");
            UpdateStatus($"Loading {selectedModel}...");
            ShowProgress($"Loading {selectedModel}...", null);
            
            await _foundryService.LoadModelAsync(selectedModel);
            Console.WriteLine("Model load command sent");
            
            // Step 6: Wait a brief moment for model to load, then try health check once
            UpdateStatus("Verifying model is ready...");
            await Task.Delay(3000); // Give model a few seconds to load
            
            try
            {
                bool isHealthy = await _chat.HealthOkAsync();
                if (isHealthy)
                {
                    Console.WriteLine("Health check PASSED!");
                    UpdateStatus("Running");
                    ShowProgress("Model ready", 100);
                    _currentModel = selectedModel;
                    await RefreshModelsAsync();
                    
                    // Hide progress after showing "ready" briefly
                    _ = Task.Delay(2000).ContinueWith(_ => Dispatcher.Invoke(() => HideProgress()));
                }
                else
                {
                    Console.WriteLine("Health check failed - service not responding");
                    UpdateStatus("Service not responding");
                    ShowProgress("Health check failed", null);
                    _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.Invoke(() => HideProgress()));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Health check error: {ex.Message}");
                UpdateStatus("Health check failed");
                ShowProgress($"Health check error: {ex.Message}", null);
                _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.Invoke(() => HideProgress()));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in BtnStart_Click: {ex.Message}");
            UpdateStatus("Error occurred");
            ShowProgress($"Error: {ex.Message}", null);
            _ = Task.Delay(5000).ContinueWith(_ => Dispatcher.Invoke(() => HideProgress()));
        }
    }

    private async void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_currentModel))
        {
            await _foundryService.UnloadModelAsync(_currentModel);
        }
        UpdateStatus("Stopped");
        TxtServiceUrl.Text = "";
        LblHealth.Text = "";
        HideProgress();
    }

    private async void BtnRefreshModels_Click(object sender, RoutedEventArgs e)
    {
        await RefreshModelsAsync();
    }

    private async Task RefreshModelsAsync()
    {
        try
        {
            var models = await _foundryService.GetAvailableModelsAsync();
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

        // Model selection is handled by the foundry service
        Log("[info] Model selection handled by foundry service.");
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
                // Use the model alias directly - ChatClient handles the URL resolution
                Console.WriteLine($"Sending chat request with model alias: '{model}'");
                var temp = (float)SldTemp.Value;
                reply = await _chat.SendChatAsync(model, _messages, temp);
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
        Dispatcher.Invoke(() =>
        {
            // Hide welcome panel when first message appears
            WelcomePanel.Visibility = Visibility.Collapsed;
            
            // Create a new message bubble
            var messagePanel = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 16),
                HorizontalAlignment = who == "You" ? HorizontalAlignment.Right : HorizontalAlignment.Left
            };

            // Add sender label
            var senderLabel = new TextBlock
            {
                Text = who,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    who == "You" ? System.Windows.Media.Color.FromRgb(59, 130, 246) : System.Windows.Media.Color.FromRgb(16, 185, 129)),
                FontSize = 12
            };

            // Add message content - use TextBox to make it copyable
            var messageText = new TextBox
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Background = new System.Windows.Media.SolidColorBrush(
                    who == "You" ? System.Windows.Media.Color.FromRgb(59, 130, 246) : System.Windows.Media.Color.FromRgb(243, 244, 246)),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    who == "You" ? System.Windows.Media.Colors.White : System.Windows.Media.Colors.Black),
                Padding = new Thickness(12, 8, 12, 8),
                MaxWidth = 500,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                IsTabStop = false
            };

            messagePanel.Children.Add(senderLabel);
            messagePanel.Children.Add(messageText);
            ChatMessages.Children.Add(messagePanel);

            // Auto-scroll to bottom
            ChatScrollViewer.ScrollToBottom();
        });
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

    // ===== SERVICE MANAGEMENT HANDLERS =====
    
    private async void BtnStartService_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatus("Starting service...");
        try
        {
            var serviceUrl = await _foundryService.StartServiceAsync();
            if (!string.IsNullOrWhiteSpace(serviceUrl))
            {
                TxtServiceUrl.Text = serviceUrl;
                UpdateServiceStatusDisplay(true);
                UpdateStatus("Service started");
                Log($"[info] Service started at: {serviceUrl}");
            }
            else
            {
                UpdateStatus("Failed to start service");
                Log("[error] Failed to start foundry service");
            }
        }
        catch (Exception ex)
        {
            UpdateStatus("Service start failed");
            Log($"[error] Service start error: {ex.Message}");
        }
    }

    private async void BtnStopService_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatus("Stopping service...");
        try
        {
            var result = await _foundryService.StopServiceAsync();
            if (result)
            {
                TxtServiceUrl.Text = "";
                UpdateServiceStatusDisplay(false);
                UpdateStatus("Service stopped");
                Log("[info] Service stopped");
                
                // Clear model state since service is stopped
                _isModelLoaded = false;
                _currentModel = null;
                UpdateCurrentModelDisplay();
                UpdateUIState();
            }
            else
            {
                UpdateStatus("Failed to stop service");
                Log("[error] Failed to stop foundry service");
            }
        }
        catch (Exception ex)
        {
            UpdateStatus("Service stop failed");
            Log($"[error] Service stop error: {ex.Message}");
        }
    }

    private async void BtnRestartService_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatus("Restarting service...");
        try
        {
            // Stop first
            await _foundryService.StopServiceAsync();
            await Task.Delay(2000); // Wait a bit
            
            // Start again
            var serviceUrl = await _foundryService.StartServiceAsync();
            if (!string.IsNullOrWhiteSpace(serviceUrl))
            {
                TxtServiceUrl.Text = serviceUrl;
                UpdateServiceStatusDisplay(true);
                UpdateStatus("Service restarted");
                Log($"[info] Service restarted at: {serviceUrl}");
                
                // Clear model state and reload
                _isModelLoaded = false;
                _currentModel = null;
                UpdateCurrentModelDisplay();
                UpdateUIState();
                
                // Reload models list
                await RefreshModelsAsync();
            }
            else
            {
                UpdateStatus("Restart failed");
                Log("[error] Service restart failed");
            }
        }
        catch (Exception ex)
        {
            UpdateStatus("Restart failed");
            Log($"[error] Service restart error: {ex.Message}");
        }
    }

    // ===== MODEL MANAGEMENT HANDLERS =====
    
    private async void BtnLoadModel_Click(object sender, RoutedEventArgs e)
    {
        var selectedModel = CmbModels.SelectedItem as string ?? string.Empty;
        if (string.IsNullOrWhiteSpace(selectedModel))
        {
            MessageBox.Show("Select a model first.", "Missing model");
            return;
        }

        UpdateStatus("Loading model...");
        try
        {
            var result = await _foundryService.LoadModelAsync(selectedModel);
            if (result)
            {
                _currentModel = selectedModel;
                _isModelLoaded = true;
                
                UpdateStatus($"Model loaded: {selectedModel}");
                UpdateCurrentModelDisplay();
                UpdateUIState();
                Log($"[info] Model loaded successfully: {selectedModel}");
            }
            else
            {
                UpdateStatus("Failed to load model");
                Log($"[error] Failed to load model: {selectedModel}");
            }
        }
        catch (Exception ex)
        {
            UpdateStatus("Model load failed");
            Log($"[error] Model load error: {ex.Message}");
        }
    }

    private async void BtnUnloadModel_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentModel))
        {
            MessageBox.Show("No model is currently loaded.", "No model");
            return;
        }

        UpdateStatus("Unloading model...");
        try
        {
            var result = await _foundryService.UnloadModelAsync(_currentModel);
            if (result)
            {
                var previousModel = _currentModel;
                _currentModel = null;
                _isModelLoaded = false;
                
                UpdateStatus("Model unloaded");
                UpdateCurrentModelDisplay();
                UpdateUIState();
                Log($"[info] Model unloaded: {previousModel}");
            }
            else
            {
                UpdateStatus("Failed to unload model");
                Log($"[error] Failed to unload model: {_currentModel}");
            }
        }
        catch (Exception ex)
        {
            UpdateStatus("Model unload failed");
            Log($"[error] Model unload error: {ex.Message}");
        }
    }

    // ===== UI STATE HELPERS =====
    
    private void UpdateServiceStatusDisplay(bool isRunning)
    {
        Dispatcher.Invoke(() =>
        {
            _isServiceRunning = isRunning;
            ServiceStatusIndicator.Fill = new SolidColorBrush(isRunning ? Colors.Green : Colors.Red);
            TxtServiceStatus.Text = isRunning ? "Service Running" : "Service Stopped";

            // Enable/disable for safety
            BtnStartService.IsEnabled = !isRunning;
            BtnStopService.IsEnabled = isRunning;
            BtnRestartService.IsEnabled = isRunning;

            // Visibility rules per requirements
            BtnStartService.Visibility = isRunning ? Visibility.Collapsed : Visibility.Visible;
            BtnStopService.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;
            BtnRestartService.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private void UpdateCurrentModelDisplay()
    {
        Dispatcher.Invoke(() =>
        {
            TxtCurrentModel.Text = _isModelLoaded && !string.IsNullOrEmpty(_currentModel) 
                ? $"Model: {_currentModel}" 
                : "No model loaded";
        });
    }

    private void UpdateUIState()
    {
        Dispatcher.Invoke(() =>
        {
            BtnLoadModel.IsEnabled = !string.IsNullOrEmpty(CmbModels.SelectedItem as string);
            BtnUnloadModel.IsEnabled = _isModelLoaded && !string.IsNullOrEmpty(_currentModel);
            BtnSend.IsEnabled = _isModelLoaded;
        });
    }
}