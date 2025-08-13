using System;
using System.Windows;
using AiFoundryUI.Models;
namespace AiFoundryUI;

public partial class SettingsWindow : Window
{
    private readonly Config _config;
    private readonly Func<string, System.Threading.Tasks.Task<string>>? _generatePromptAsync;

    public SettingsWindow(Config cfg, Func<string, System.Threading.Tasks.Task<string>>? generatePromptAsync = null)
    {
        InitializeComponent();
        _config = cfg;
        _generatePromptAsync = generatePromptAsync;
        TxtDefaultInstructions.Text = _config.DefaultInstructions ?? string.Empty;
    }

    private async void BtnGenerateGlobalPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (_generatePromptAsync == null)
        {
            MessageBox.Show("Prompt generation is unavailable.");
            return;
        }
        var idea = TxtGlobalPromptIdea.Text?.Trim();
        if (string.IsNullOrWhiteSpace(idea))
        {
            MessageBox.Show("Type what you want this prompt to achieve.");
            return;
        }
        try
        {
            var draft = await _generatePromptAsync(idea);
            if (!string.IsNullOrWhiteSpace(draft))
            {
                TxtDefaultInstructions.Text = draft.Trim();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to generate prompt: " + ex.Message);
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _config.DefaultInstructions = TxtDefaultInstructions.Text ?? string.Empty;
        Config.Save(_config);
        this.DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = false;
        Close();
    }
}
