using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AiFoundryUI.Models;
using AiFoundryUI.Services;
using System.Linq;
using System.Threading.Tasks;

namespace AiFoundryUI;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private const string DefaultModelAlias = "phi-4-mini"; // preferred default when nothing is loaded
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
    private System.Threading.CancellationTokenSource? _chatCts;
    private System.Threading.CancellationTokenSource? _opCts; // for non-chat operations
    private bool _suppressAutoLoad = false; // avoid auto-loading on programmatic selection

    // Persistence-backed threads
    private readonly List<ChatThreadEntity> _threads = new();
    private ChatThreadEntity? _activeThread;
    private readonly ThreadsRepository _threadsRepo;
    private bool _isRefreshingThreads = false; // suppress selection reload side-effects

    public MainWindow()
    {
        InitializeComponent();
        _config = Config.LoadOrCreate();
        _foundryService = new FoundryService(Log);
        _chat = new ChatClient(_config, _foundryService);
        _monitor = new SystemMonitor();
    _threadsRepo = new ThreadsRepository();

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

    _ = InitializeThreadsAsync();
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

    private async void CmbModels_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Auto-hide welcome panel when model is selected
        if (CmbModels.SelectedItem != null)
        {
            WelcomePanel.Visibility = Visibility.Collapsed;
        }

        // Auto-load the model when user changes selection (not during programmatic selection)
        if (_suppressAutoLoad) return;
        var selected = CmbModels.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(selected)) return;

        // If it's already the current loaded model, skip
        if (_isModelLoaded && string.Equals(_currentModel, selected, StringComparison.OrdinalIgnoreCase))
            return;

        EnterBusyState($"Preparing {selected}...");
        try
        {
            var ok = await EnsureModelLoadedAsync(selected);
            if (!ok)
            {
                AppendChat("Error", $"Failed to load model '{selected}'.");
            }
        }
        finally
        {
            ExitBusyState();
        }
    }

    private void OnProcessOutput(string output)
    {
        // This method is no longer needed with the new FoundryService architecture
        // The FoundryService handles all CLI output processing internally
    Logger.Log($"Legacy OnProcessOutput called: {output}");
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
            var loadedModels = await _foundryService.GetCurrentlyLoadedModelsAsync();
            Logger.Log($"[startup] Available models: {string.Join(", ", models)} | Loaded(raw): {string.Join(", ", loadedModels)}");
            // Sanitize: strip any header/device tokens that should never appear
            var badTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "alias", "cpu", "gpu" };
            models = models.Where(m => !string.IsNullOrWhiteSpace(m) && !badTokens.Contains(m.Trim())).ToList();
            loadedModels = loadedModels.Where(m => !string.IsNullOrWhiteSpace(m) && !badTokens.Contains(m.Trim())).ToList();
            // Ensure any loaded alias not present gets included so user sees it
            if (loadedModels.Count > 0)
            {
                var merged = models.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var lm in loadedModels)
                    if (!merged.Contains(lm)) merged.Add(lm);
                models = merged.OrderBy(m=>m).ToList();
            }
        if (models?.Count > 0)
            {
                bool noLoadedAtStartup = loadedModels.Count == 0; // track for default auto-load
                Dispatcher.Invoke(() =>
                {
            _suppressAutoLoad = true; // prevent auto-load during initial binding/selection
                    CmbModels.ItemsSource = models;
                    if (loadedModels.Count > 0)
                    {
                        var normLoaded = loadedModels.Select(l => l.Trim())
                                                     .Where(l => !string.IsNullOrWhiteSpace(l))
                                                     .Distinct(StringComparer.OrdinalIgnoreCase)
                                                     .ToList();

                        string? match = null;

                        // 1. Exact case-insensitive match
                        match = models.FirstOrDefault(m => normLoaded.Any(l => string.Equals(l, m, StringComparison.OrdinalIgnoreCase)));
                        if (match != null) Logger.Log($"[startup-select] Exact match: {match}");

                        // 2. If only one loaded alias and not in list, add it and select
                        if (match == null && normLoaded.Count == 1)
                        {
                            var single = normLoaded[0];
                            if (!models.Any(m => string.Equals(m, single, StringComparison.OrdinalIgnoreCase)))
                            {
                                Logger.Log($"[startup-select] Single loaded alias '{single}' not in available list; injecting.");
                                models = models.Concat(new[]{ single }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m=>m).ToList();
                                CmbModels.ItemsSource = models; // refresh
                            }
                            match = single;
                        }

                        // 3. Fuzzy: contains either direction (length safeguard to avoid very short substrings)
                        if (match == null)
                        {
                            match = models.FirstOrDefault(m => normLoaded.Any(l =>
                                (l.Length > 5 && (l.Contains(m, StringComparison.OrdinalIgnoreCase) || m.Contains(l, StringComparison.OrdinalIgnoreCase)))));
                            if (match != null) Logger.Log($"[startup-select] Fuzzy contains match: {match}");
                        }

                        // 4. Prefix/suffix matches
                        if (match == null)
                        {
                            match = models.FirstOrDefault(m => normLoaded.Any(l => m.StartsWith(l, StringComparison.OrdinalIgnoreCase) || m.EndsWith(l, StringComparison.OrdinalIgnoreCase) || l.StartsWith(m, StringComparison.OrdinalIgnoreCase) || l.EndsWith(m, StringComparison.OrdinalIgnoreCase)));
                            if (match != null) Logger.Log($"[startup-select] Prefix/suffix match: {match}");
                        }

                        // 5. Levenshtein distance (pick best within threshold)
                        if (match == null)
                        {
                            int BestDistance(string a, string b)
                            {
                                var la = a.Length; var lb = b.Length;
                                var d = new int[la + 1, lb + 1];
                                for (int i = 0; i <= la; i++) d[i,0] = i;
                                for (int j = 0; j <= lb; j++) d[0,j] = j;
                                for (int i = 1; i <= la; i++)
                                    for (int j = 1; j <= lb; j++)
                                    {
                                        int cost = char.ToLowerInvariant(a[i-1]) == char.ToLowerInvariant(b[j-1]) ? 0 : 1;
                                        d[i,j] = Math.Min(Math.Min(d[i-1,j] + 1, d[i,j-1] + 1), d[i-1,j-1] + cost);
                                    }
                                return d[la, lb];
                            }
                            var candidates = new List<(string m,int dist)>();
                            foreach (var lm in normLoaded)
                                foreach (var av in models)
                                    candidates.Add((av, BestDistance(lm, av)));
                            var best = candidates.OrderBy(c=>c.dist).FirstOrDefault();
                            if (best.m != null && best.dist <= 3) // threshold
                            {
                                match = best.m;
                                Logger.Log($"[startup-select] Levenshtein match: {match} (distance {best.dist})");
                            }
                        }

                        if (match != null)
                        {
                            // Prefer selecting by index to avoid any object-identity quirks
                            var idx = models.FindIndex(m => string.Equals(m, match, StringComparison.OrdinalIgnoreCase));
                            if (idx >= 0)
                            {
                                CmbModels.SelectedIndex = idx;
                                _currentModel = models[idx];
                                Logger.Log($"[startup-select] Selected index {idx} for alias '{_currentModel}'");
                            }
                            else
                            {
                                // Fallback to SelectedItem
                                CmbModels.SelectedItem = match;
                                _currentModel = match;
                                Logger.Log($"[startup-select] Fallback SelectedItem for alias '{_currentModel}'");
                            }
                            _isModelLoaded = true;
                            UpdateCurrentModelDisplay();
                            UpdateStatus($"Model loaded: {match}");
                            TxtCurrentModel.Text = $"Model: {match}";
                        }
                        else
                        {
                            Logger.Log($"[startup-select] No match found. Loaded aliases: {string.Join(", ", normLoaded)}; model list: {string.Join(", ", models)}");
                            if (CmbModels.SelectedIndex < 0 && CmbModels.Items.Count > 0)
                                CmbModels.SelectedIndex = 0; // fallback
                        }
                    }
                    else if (CmbModels.SelectedIndex < 0)
                    {
                        // Prefer the default model alias when nothing is loaded
                        var idxDefault = models.FindIndex(m => string.Equals(m, DefaultModelAlias, StringComparison.OrdinalIgnoreCase));
                        CmbModels.SelectedIndex = idxDefault >= 0 ? idxDefault : 0;
                    }
                    _suppressAutoLoad = false;
                });
                Log($"[info] Loaded {models.Count} models: {string.Join(", ", models)}");
                UpdateStatus("Ready - pick a model to load");

                // If no models are loaded in the service, auto-load the default if available
                if (noLoadedAtStartup)
                {
                    var hasDefault = models.Any(m => string.Equals(m, DefaultModelAlias, StringComparison.OrdinalIgnoreCase));
                    if (hasDefault)
                    {
                        UpdateStatus($"Loading default model: {DefaultModelAlias}...");
                        ShowProgress($"Loading {DefaultModelAlias}...", null);
                        var ok = await EnsureModelLoadedAsync(DefaultModelAlias);
                        if (!ok)
                        {
                            ShowProgress($"Failed to load {DefaultModelAlias}", null);
                            await Task.Delay(1500);
                            HideProgress();
                        }
                    }
                }
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
    Logger.Log($"[AI Foundry] {msg}");
        
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
    Logger.Log("=== START BUTTON CLICKED ===");
        
        var selectedModel = CmbModels.SelectedItem as string ?? string.Empty;
    Logger.Log($"Selected model: '{selectedModel}'");
        
        if (string.IsNullOrWhiteSpace(selectedModel))
        {
            Logger.Log("ERROR: No model selected");
            MessageBox.Show("Select a model first.", "Missing model");
            return;
        }

        try
        {
            // Step 1: Check if model is cached (downloaded)
            UpdateStatus("Checking if model is cached...");
            bool isCached = await _foundryService.IsModelCachedAsync(selectedModel);
            Logger.Log($"Model {selectedModel} cached: {isCached}");

            // Step 2: If not cached, download it first
            if (!isCached)
            {
                Logger.Log($"Model {selectedModel} not cached, starting download...");
                UpdateStatus($"Downloading {selectedModel}...");
                ShowProgress($"Downloading {selectedModel}...", null);
                
                // Set up progress callback to show download progress
                var progress = new Progress<string>(output =>
                {
                    Logger.Log($"Download progress: {output}");
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
                
                Logger.Log($"Model {selectedModel} download completed");
                UpdateStatus("Download completed");
            }

            // Step 3: Check service status and start if needed
            Logger.Log("Checking foundry service status...");
            UpdateStatus("Checking service status...");
            var (isRunning, serviceUrl) = await _foundryService.GetServiceStatusAsync();
            
            if (!isRunning)
            {
                Logger.Log("Service not running, starting...");
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
                Logger.Log($"Service URL set to: {serviceUrl}");
            }

            // Step 5: Load the model
            Logger.Log($"Loading model {selectedModel}...");
            UpdateStatus($"Loading {selectedModel}...");
            ShowProgress($"Loading {selectedModel}...", null);
            
            await _foundryService.LoadModelAsync(selectedModel);
            Logger.Log("Model load command sent");
            
            // Step 6: Wait a brief moment for model to load, then try health check once
            UpdateStatus("Verifying model is ready...");
            await Task.Delay(3000); // Give model a few seconds to load
            
            try
            {
                bool isHealthy = await _chat.HealthOkAsync();
                if (isHealthy)
                {
                    Logger.Log("Health check PASSED!");
                    UpdateStatus("Running");
                    ShowProgress("Model ready", 100);
                    _currentModel = selectedModel;
                    await RefreshModelsAsync();
                    
                    // Hide progress after showing "ready" briefly
                    _ = Task.Delay(2000).ContinueWith(_ => Dispatcher.Invoke(() => HideProgress()));
                }
                else
                {
                    Logger.Log("Health check failed - service not responding");
                    UpdateStatus("Service not responding");
                    ShowProgress("Health check failed", null);
                    _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.Invoke(() => HideProgress()));
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Health check error: {ex.Message}");
                UpdateStatus("Health check failed");
                ShowProgress($"Health check error: {ex.Message}", null);
                _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.Invoke(() => HideProgress()));
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Error in BtnStart_Click: {ex.Message}");
            UpdateStatus("Error occurred");
            ShowProgress($"Error: {ex.Message}", null);
            _ = Task.Delay(5000).ContinueWith(_ => Dispatcher.Invoke(() => HideProgress()));
        }
    }

    private async void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        // If a chat is in progress, treat Stop as a cancel for chat
        if (_isChatBusy)
        {
            _chatCts?.Cancel();
            return;
        }

        // Otherwise, Stop should NOT unload the model or stop the service.
        // Just clear any progress UI and set a neutral status.
        HideProgress();
        UpdateStatus("Idle");
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
        // If currently busy with a chat, treat as cancel
        if (_isChatBusy)
        {
            _chatCts?.Cancel();
            return;
        }

        var model = CmbModels.SelectedItem as string ?? string.Empty;
        if (string.IsNullOrWhiteSpace(model))
        {
            // Try to fall back to default model
            var items = (CmbModels.ItemsSource as System.Collections.IEnumerable)?.Cast<string>().ToList() ?? new List<string>();
            if (items.Count > 0)
            {
                var idxDefault = items.FindIndex(m => string.Equals(m, DefaultModelAlias, StringComparison.OrdinalIgnoreCase));
                _suppressAutoLoad = true; // avoid double EnsureModelLoadedAsync via SelectionChanged
                if (idxDefault >= 0)
                {
                    CmbModels.SelectedIndex = idxDefault;
                    model = items[idxDefault];
                }
                else
                {
                    CmbModels.SelectedIndex = 0;
                    model = items[0];
                }
                _suppressAutoLoad = false;
            }
        }
        if (string.IsNullOrWhiteSpace(model))
        {
            MessageBox.Show("No models available.", "Missing model");
            return;
        }
        var prompt = TxtPrompt.Text.Trim();
        if (string.IsNullOrEmpty(prompt)) return;

        EnterBusyState("Preparing model...");

        // Ensure model is ready (download + load if needed)
        var prepOk = await EnsureModelLoadedAsync(model);
        if (!prepOk)
        {
            AppendChat("Error", $"Failed to prepare model '{model}'.");
            ExitBusyState();
            return;
        }

        EnterBusyState("Sending...");
        AppendChat("You", prompt);
        TxtPrompt.Clear();

        _messages.Add(new ChatMessage { Role = "user", Content = prompt });
        if (_activeThread != null)
        {
            await _threadsRepo.AddMessageAsync(_activeThread.Id, "user", prompt);
        }
        try
        {
            string reply;
            if (_config.OpenAICompatible)
            {
                // Use the model alias directly - ChatClient handles the URL resolution
                Logger.Log($"Sending chat request with model alias: '{model}'");
                var temp = (float)SldTemp.Value;
                _chatCts = new System.Threading.CancellationTokenSource();
                reply = await _chat.SendChatAsync(model, _messages, temp, _chatCts.Token);
            }
            else
            {
                reply = "(openai_compatible is false; customize ChatClient for your API.)";
            }
            _messages.Add(new ChatMessage { Role = "assistant", Content = reply });
            if (_activeThread != null)
            {
                await _threadsRepo.AddMessageAsync(_activeThread.Id, "assistant", reply);
                _activeThread.UpdatedUtc = DateTime.UtcNow;
                RefreshThreadListPreserveSelection();
            }
            AppendChat("Assistant", reply);
        }
        catch (Exception ex)
        {
            AppendChat("Error", ex.Message);
        }
        finally
        {
            ExitBusyState();
            _chatCts?.Dispose();
            _chatCts = null;
        }
    }

    private async Task<bool> EnsureModelLoadedAsync(string modelAlias)
    {
        try
        {
            // Ensure service is running and chat client is pointed at it
            var (isRunning, serviceUrl) = await _foundryService.GetServiceStatusAsync();
            if (!isRunning)
            {
                UpdateStatus("Starting service...");
                ShowProgress("Starting service...", null);
                serviceUrl = await _foundryService.StartServiceAsync();
                if (string.IsNullOrWhiteSpace(serviceUrl))
                {
                    UpdateStatus("Failed to start service");
                    ShowProgress("Service start failed", null);
                    await Task.Delay(2000);
                    HideProgress();
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(serviceUrl))
            {
                _chat.SetBaseUrl(serviceUrl);
                Dispatcher.Invoke(() => TxtServiceUrl.Text = serviceUrl);
                UpdateServiceStatusDisplay(true);
            }

            // Quick check: is model already loaded?
            var loaded = await _foundryService.GetCurrentlyLoadedModelsAsync();
            if (loaded.Contains(modelAlias))
            {
                _currentModel = modelAlias;
                _isModelLoaded = true;
                UpdateCurrentModelDisplay();
                return true;
            }

            UpdateStatus($"Checking cache for {modelAlias}...");
            ShowProgress($"Checking cache for {modelAlias}...", null);
            bool isCached = await _foundryService.IsModelCachedAsync(modelAlias);
            if (!isCached)
            {
                UpdateStatus($"Downloading {modelAlias}...");
                ShowProgress($"Downloading {modelAlias}...", null);
                var progress = new Progress<string>(line =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        var pct = ExtractProgressPercent(line);
                        if (pct.HasValue) UpdateProgressPercent(pct.Value); else LblProgressStatus.Text = line;
                    });
                });
                var downloaded = await _foundryService.DownloadModelAsync(modelAlias, progress);
                if (!downloaded)
                {
                    UpdateStatus("Download failed");
                    ShowProgress("Download failed", null);
                    await Task.Delay(2000);
                    HideProgress();
                    return false;
                }
            }

            UpdateStatus($"Loading {modelAlias}...");
            ShowProgress($"Loading {modelAlias}...", null);
            var loadedOk = await _foundryService.LoadModelAsync(modelAlias);
            if (!loadedOk)
            {
                UpdateStatus("Load failed");
                ShowProgress("Load failed", null);
                await Task.Delay(2000);
                HideProgress();
                return false;
            }

            // Validate health once
            await Task.Delay(2000);
            try
            {
                bool healthy = await _chat.HealthOkAsync();
                if (!healthy)
                {
                    UpdateStatus("Model not responding");
                    ShowProgress("Model not responding", null);
                    await Task.Delay(2000);
                    HideProgress();
                    return false;
                }
            }
            catch { /* ignore health exceptions; treat as failure */ }

            _currentModel = modelAlias;
            _isModelLoaded = true;
            UpdateCurrentModelDisplay();
            ShowProgress("Model ready", 100);
            _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(HideProgress));
            return true;
        }
        catch (Exception ex)
        {
            AppendChat("Error", $"Model prep error: {ex.Message}");
            return false;
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

        // Update thread metadata
        if (_activeThread != null)
        {
            _activeThread.UpdatedUtc = DateTime.UtcNow;
            // If this is the first user message for the thread, set the title
            if (who == "You")
            {
                // Title only if this was the first non-system message
                var existing = ChatMessages.Children.Count; // rough UI count; persisted check happens at repo write time
                if (existing <= 2) // sender label + message roughly implies first exchange
                {
                    var newTitle = text.Length > 40 ? text.Substring(0, 40) + "…" : text;
                    _activeThread.Title = newTitle;
                    _ = _threadsRepo.UpdateThreadTitleAsync(_activeThread.Id, newTitle);
                    RefreshThreadListPreserveSelection();
                }
            }
        }
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
        EnterBusyState("Starting service...");
        _opCts = new System.Threading.CancellationTokenSource();
        UpdateStatus("Starting service...");
        try
        {
            var serviceUrl = await _foundryService.StartServiceAsync(_opCts.Token);
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
        finally
        {
            ExitBusyState();
            _opCts?.Dispose(); _opCts = null;
        }
    }

    private async void BtnStopService_Click(object sender, RoutedEventArgs e)
    {
        EnterBusyState("Stopping service...");
        _opCts = new System.Threading.CancellationTokenSource();
        UpdateStatus("Stopping service...");
        try
        {
            var result = await _foundryService.StopServiceAsync(_opCts.Token);
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
        finally
        {
            ExitBusyState();
            _opCts?.Dispose(); _opCts = null;
        }
    }

    private async void BtnRestartService_Click(object sender, RoutedEventArgs e)
    {
        EnterBusyState("Restarting service...");
        _opCts = new System.Threading.CancellationTokenSource();
        UpdateStatus("Restarting service...");
        try
        {
            // Stop first
            await _foundryService.StopServiceAsync(_opCts.Token);
            await Task.Delay(2000); // Wait a bit
            
            // Start again
            var serviceUrl = await _foundryService.StartServiceAsync(_opCts.Token);
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
        finally
        {
            ExitBusyState();
            _opCts?.Dispose(); _opCts = null;
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

        EnterBusyState($"Loading {selectedModel}...");
        _opCts = new System.Threading.CancellationTokenSource();
        UpdateStatus("Loading model...");
        try
        {
            var result = await _foundryService.LoadModelAsync(selectedModel, _opCts.Token);
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
        finally
        {
            ExitBusyState();
            _opCts?.Dispose(); _opCts = null;
        }
    }

    private async void BtnUnloadModel_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentModel))
        {
            MessageBox.Show("No model is currently loaded.", "No model");
            return;
        }

        EnterBusyState("Unloading model...");
        _opCts = new System.Threading.CancellationTokenSource();
        UpdateStatus("Unloading model...");
        try
        {
            var result = await _foundryService.UnloadModelAsync(_currentModel, _opCts.Token);
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
        finally
        {
            ExitBusyState();
            _opCts?.Dispose(); _opCts = null;
        }
    }

    private void BtnBusyCancel_Click(object sender, RoutedEventArgs e)
    {
        // Prefer chat cancel when active
        if (_isChatBusy && _chatCts != null)
        {
            _chatCts.Cancel();
            return;
        }

        // Otherwise cancel non-chat operation by killing last process
        _opCts?.Cancel();
        _foundryService.CancelLastOperation();
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
            BtnSend.IsEnabled = _isModelLoaded && !_isChatBusy;
        });
    }

    // ===== Busy state helpers =====
    private void EnterBusyState(string? message = null)
    {
        _isChatBusy = true;
        Dispatcher.Invoke(() =>
        {
            BusyOverlay.Visibility = Visibility.Visible;
            // Disable most controls
            TxtPrompt.IsReadOnly = true;
            CmbModels.IsEnabled = false;
            BtnLoadModel.IsEnabled = false;
            BtnUnloadModel.IsEnabled = false;
            BtnStartService.IsEnabled = false;
            BtnStopService.IsEnabled = false;
            BtnRestartService.IsEnabled = false;
            BtnRefreshModels.IsEnabled = false;
            BtnOpenConfig.IsEnabled = false;
            // Toggle Send to Cancel
            BtnSend.Content = "Cancel";
            BtnSend.IsEnabled = true; // allow cancel
        });
    }

    private void ExitBusyState()
    {
        _isChatBusy = false;
        Dispatcher.Invoke(() =>
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
            // Re-enable controls according to state
            TxtPrompt.IsReadOnly = false;
            CmbModels.IsEnabled = true;
            BtnStartService.IsEnabled = !_isServiceRunning;
            BtnStopService.IsEnabled = _isServiceRunning;
            BtnRestartService.IsEnabled = _isServiceRunning;
            BtnRefreshModels.IsEnabled = true;
            BtnOpenConfig.IsEnabled = true;
            BtnSend.Content = "Send";
            UpdateUIState();
        });
    }

    // ===== Thread Pane Logic =====
    private async Task InitializeThreadsAsync()
    {
        await _threadsRepo.InitializeAsync();
        var existing = await _threadsRepo.GetThreadsAsync();
        _threads.Clear();
        _threads.AddRange(existing);
        if (_threads.Count == 0)
        {
            // Create initial thread with a system primer message
            var id = await _threadsRepo.CreateThreadAsync("New Chat");
            await _threadsRepo.AddMessageAsync(id, "system", "You are a helpful assistant.");
            _threads.Add(new ChatThreadEntity { Id = id, Title = "New Chat", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow });
        }
        _activeThread = _threads.OrderByDescending(t=>t.UpdatedUtc).FirstOrDefault();
        LstThreads.ItemsSource = null;
        LstThreads.ItemsSource = _threads.OrderByDescending(t=>t.UpdatedUtc).ToList();
        if (_activeThread != null)
            LstThreads.SelectedItem = _activeThread;
        // Load messages for active thread into UI
        if (_activeThread != null)
        {
            await LoadThreadIntoUIAsync(_activeThread);
        }
    }

    private void RefreshThreadListPreserveSelection()
    {
        var selected = _activeThread?.Id;
        var ordered = _threads.OrderByDescending(t=>t.UpdatedUtc).ToList();
        _isRefreshingThreads = true;
        try
        {
            LstThreads.ItemsSource = null;
            LstThreads.ItemsSource = ordered;
            if (selected != null)
            {
                var idx = ordered.FindIndex(t=>t.Id==selected);
                if (idx >= 0) LstThreads.SelectedIndex = idx;
            }
        }
        finally
        {
            _isRefreshingThreads = false;
        }
    }

    private async void BtnNewThread_Click(object sender, RoutedEventArgs e)
    {
        var id = await _threadsRepo.CreateThreadAsync("New Chat");
        await _threadsRepo.AddMessageAsync(id, "system", "You are a helpful assistant.");
        var thread = new ChatThreadEntity { Id = id, Title = "New Chat", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
        _threads.Add(thread);
        _activeThread = thread;
        RefreshThreadListPreserveSelection();
        await LoadThreadIntoUIAsync(thread);
    }

    private async void LstThreads_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    if (_isRefreshingThreads) return; // suppress duplicate reload
    if (LstThreads.SelectedItem is ChatThreadEntity ct)
        {
            _activeThread = ct;
            await LoadThreadIntoUIAsync(ct);
        }
    }

    private async void BtnDeleteThread_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is ChatThreadEntity thread)
        {
            var confirm = MessageBox.Show($"Delete thread '{thread.Title}'? This cannot be undone.", "Delete Thread", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                await _threadsRepo.DeleteThreadAsync(thread.Id);
                // Remove from in-memory list
                var wasActive = _activeThread?.Id == thread.Id;
                _threads.RemoveAll(t => t.Id == thread.Id);

                // Decide next active thread
                if (_threads.Count == 0)
                {
                    // Create a new default thread so UI is never empty
                    var id = await _threadsRepo.CreateThreadAsync("New Chat");
                    await _threadsRepo.AddMessageAsync(id, "system", "You are a helpful assistant.");
                    var newThread = new ChatThreadEntity { Id = id, Title = "New Chat", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
                    _threads.Add(newThread);
                    _activeThread = newThread;
                }
                else if (wasActive)
                {
                    _activeThread = _threads.OrderByDescending(t => t.UpdatedUtc).FirstOrDefault();
                }

                RefreshThreadListPreserveSelection();
                if (_activeThread != null)
                {
                    await LoadThreadIntoUIAsync(_activeThread);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to delete thread: " + ex.Message);
            }
        }
    }

    private async Task LoadThreadIntoUIAsync(ChatThreadEntity thread)
    {
        ChatMessages.Children.Clear();
        var messages = await _threadsRepo.GetMessagesAsync(thread.Id);
        foreach (var msg in messages)
        {
            if (msg.Role == "system") continue;
            var who = msg.Role == "assistant" ? "Assistant" : msg.Role == "user" ? "You" : msg.Role;
            AppendChat(who, msg.Content);
        }
        _messages.Clear();
        _messages.AddRange(messages.Select(m => new ChatMessage { Role = m.Role, Content = m.Content }));
    }

    private void BtnToggleThreadPane_Click(object sender, RoutedEventArgs e)
    {
        const double expandedWidth = 260;
        const double collapsedWidth = 56; // matches CollapsedThreadPane width
        bool currentlyExpanded = ExpandedThreadPane.Visibility == Visibility.Visible;

        if (currentlyExpanded)
        {
            // Collapse
            ExpandedThreadPane.Visibility = Visibility.Collapsed;
            CollapsedThreadPane.Visibility = Visibility.Visible;
            ThreadPaneColumn.Width = new GridLength(collapsedWidth);
        }
        else
        {
            // Expand
            ExpandedThreadPane.Visibility = Visibility.Visible;
            CollapsedThreadPane.Visibility = Visibility.Collapsed;
            ThreadPaneColumn.Width = new GridLength(expandedWidth);
        }
    }
}